using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>
/// Transactional operational saga store. Operation heads are mutable
/// projections; participant receipts and transitions are immutable rows.
/// </summary>
public sealed class SqliteCrossStoreOperationStore : ICrossStoreOperationStore
{
    private readonly string databasePath;
    private readonly bool pooling;

    public SqliteCrossStoreOperationStore(
        string databasePath,
        bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.pooling = pooling;
    }

    public async Task<CrossStoreOperation> StartAsync(
        StartCrossStoreOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existingId = await FindIdByIdempotencyKeyAsync(
            connection, transaction, request.SessionId, request.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingId is not null)
        {
            var existing = await ReadAsync(connection, transaction, existingId.Value, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("Operation index points to a missing operation.");
            if (existing.ExternalOperation != request.ExternalOperation ||
                !string.Equals(existing.Kind, request.Kind, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Operation idempotency key '{request.IdempotencyKey}' is already used by a different operation.");
            transaction.Commit();
            return existing;
        }

        var id = request.OperationId ?? CrossStoreOperationId.New();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cross_store_operations(
                    operation_id, session_id, external_system, external_operation_id, kind, idempotency_key,
                    state, created_at, updated_at, version, reconciliation_reason)
                VALUES($id, $sessionId, $system, $operationId, $kind, $key, $state, $created, $updated, 1, NULL);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            insert.Parameters.AddWithValue("$system", request.ExternalOperation.System);
            insert.Parameters.AddWithValue("$operationId", request.ExternalOperation.Id.ToString("D"));
            insert.Parameters.AddWithValue("$kind", request.Kind);
            insert.Parameters.AddWithValue("$key", request.IdempotencyKey);
            insert.Parameters.AddWithValue("$state", (int)CrossStoreOperationState.Prepared);
            insert.Parameters.AddWithValue("$created", Format(request.StartedAt));
            insert.Parameters.AddWithValue("$updated", Format(request.StartedAt));
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    $"Operation '{id}' conflicts with an existing operation or active external operation.",
                    exception);
            }
        }
        await InsertTransitionAsync(
            connection, transaction, id, 1, CrossStoreOperationState.Prepared,
            request.StartedAt, null, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{id}' disappeared after creation.");
    }

    public async Task<CrossStoreOperation?> GetAsync(
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrossStoreOperation?> FindByExternalOperationAsync(
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id
            FROM cross_store_operations
            WHERE external_system = $system AND external_operation_id = $operationId
            ORDER BY CASE WHEN state <> $completed THEN 0 ELSE 1 END, created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$system", externalOperation.System);
        command.Parameters.AddWithValue("$operationId", externalOperation.Id.ToString("D"));
        command.Parameters.AddWithValue("$completed", (int)CrossStoreOperationState.Completed);
        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is null
            ? null
            : await ReadAsync(connection, null, CrossStoreOperationId.Parse(raw), cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CrossStoreOperation>> ListAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id FROM cross_store_operations WHERE session_id = $sessionId ORDER BY created_at, operation_id;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var ids = new List<CrossStoreOperationId>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(CrossStoreOperationId.Parse(reader.GetString(0)));
        var operations = new List<CrossStoreOperation>(ids.Count);
        foreach (var id in ids)
            operations.Add(await ReadAsync(connection, null, id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Operation '{id}' disappeared while listing."));
        return operations;
    }

    public async Task<CrossStoreOperation> RecordParticipantAsync(
        CrossStoreOperationId operationId,
        CrossStoreParticipantReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.IdempotencyKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var operation = await RequireAsync(connection, transaction, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.State == CrossStoreOperationState.Completed)
            throw new InvalidOperationException("A completed operation cannot accept participant receipts.");
        var existing = operation.Participants.SingleOrDefault(item =>
            item.Participant.Equals(receipt.Participant, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!EquivalentReceipt(existing, receipt))
                throw new InvalidOperationException(
                    $"Participant '{receipt.Participant}' already has a different immutable receipt.");
            transaction.Commit();
            return operation;
        }
        if (!string.Equals(
                receipt.IdempotencyKey,
                operation.ParticipantIdempotencyKey(receipt.Participant),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Participant '{receipt.Participant}' receipt has an invalid idempotency key.");

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cross_store_participant_receipts(
                    operation_id, participant, idempotency_key, state, recorded_at,
                    before_identity, after_identity, result_hash, recovery_action)
                VALUES($id, $participant, $key, $state, $recordedAt,
                    $before, $after, $hash, $recovery);
                """;
            insert.Parameters.AddWithValue("$id", operationId.ToString());
            insert.Parameters.AddWithValue("$participant", receipt.Participant);
            insert.Parameters.AddWithValue("$key", receipt.IdempotencyKey);
            insert.Parameters.AddWithValue("$state", (int)receipt.State);
            insert.Parameters.AddWithValue("$recordedAt", Format(receipt.RecordedAt));
            insert.Parameters.AddWithValue("$before", (object?)receipt.BeforeIdentity ?? DBNull.Value);
            insert.Parameters.AddWithValue("$after", (object?)receipt.AfterIdentity ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hash", (object?)receipt.ResultHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$recovery", (object?)receipt.RecoveryAction ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpdateHeadAsync(
            connection, transaction, operationId, operation.Version,
            operation.State, receipt.RecordedAt, operation.ReconciliationReason,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{operationId}' disappeared after receipt append.");
    }

    public async Task<CrossStoreOperation> TransitionAsync(
        CrossStoreOperationId operationId,
        CrossStoreOperationState targetState,
        DateTimeOffset occurredAt,
        string? reconciliationReason = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var operation = await RequireAsync(connection, transaction, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.State == targetState)
        {
            transaction.Commit();
            return operation;
        }
        if (!CanTransition(operation.State, targetState))
            throw new InvalidOperationException(
                $"Operation cannot transition from {operation.State} to {targetState}.");
        if (targetState == CrossStoreOperationState.ReconciliationRequired &&
            string.IsNullOrWhiteSpace(reconciliationReason))
            throw new ArgumentException("A reconciliation reason is required.", nameof(reconciliationReason));
        if (targetState == CrossStoreOperationState.Completed &&
            operation.Participants.Any(item => item.State == CrossStoreParticipantState.Failed))
            throw new InvalidOperationException("An operation with a failed participant cannot complete.");

        var reason = targetState == CrossStoreOperationState.ReconciliationRequired
            ? reconciliationReason
            : null;
        await InsertTransitionAsync(
            connection, transaction, operationId, operation.Transitions.Count + 1,
            targetState, occurredAt, reason, cancellationToken).ConfigureAwait(false);
        await UpdateHeadAsync(
            connection, transaction, operationId, operation.Version,
            targetState, occurredAt, reason, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{operationId}' disappeared after transition.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling,
            DefaultTimeout = 30
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS cross_store_operations(
                operation_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL,
                external_system TEXT NOT NULL,
                external_operation_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                version INTEGER NOT NULL,
                reconciliation_reason TEXT NULL,
                UNIQUE(session_id, idempotency_key)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_cross_store_active_external_operation
                ON cross_store_operations(external_system, external_operation_id)
                WHERE state <> {{(int)CrossStoreOperationState.Completed}};
            CREATE INDEX IF NOT EXISTS ix_cross_store_session
                ON cross_store_operations(session_id, created_at);
            CREATE TABLE IF NOT EXISTS cross_store_participant_receipts(
                operation_id TEXT NOT NULL REFERENCES cross_store_operations(operation_id) ON DELETE RESTRICT,
                participant TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                recorded_at TEXT NOT NULL,
                before_identity TEXT NULL,
                after_identity TEXT NULL,
                result_hash TEXT NULL,
                recovery_action TEXT NULL,
                PRIMARY KEY(operation_id, participant),
                UNIQUE(operation_id, idempotency_key)
            );
            CREATE TABLE IF NOT EXISTS cross_store_operation_transitions(
                operation_id TEXT NOT NULL REFERENCES cross_store_operations(operation_id) ON DELETE RESTRICT,
                sequence INTEGER NOT NULL,
                state INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                reason TEXT NULL,
                PRIMARY KEY(operation_id, sequence)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<CrossStoreOperation?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, external_system, external_operation_id, kind, idempotency_key, state,
                   created_at, updated_at, version, reconciliation_reason
            FROM cross_store_operations WHERE operation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var sessionId = SessionId.Parse(reader.GetString(0));
        var externalOperation = new ExternalOperationReference(
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)));
        var kind = reader.GetString(3);
        var key = reader.GetString(4);
        var state = (CrossStoreOperationState)reader.GetInt32(5);
        var createdAt = Parse(reader.GetString(6));
        var updatedAt = Parse(reader.GetString(7));
        var version = reader.GetInt64(8);
        var reason = reader.IsDBNull(9) ? null : reader.GetString(9);
        await reader.DisposeAsync().ConfigureAwait(false);
        var participants = await ReadParticipantsAsync(
            connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        var transitions = await ReadTransitionsAsync(
            connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        return new CrossStoreOperation
        {
            Id = operationId,
            SessionId = sessionId,
            ExternalOperation = externalOperation,
            Kind = kind,
            IdempotencyKey = key,
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Version = version,
            ReconciliationReason = reason,
            Participants = participants,
            Transitions = transitions
        };
    }

    private static async Task<IReadOnlyList<CrossStoreParticipantReceipt>> ReadParticipantsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT participant, idempotency_key, state, recorded_at, before_identity,
                   after_identity, result_hash, recovery_action
            FROM cross_store_participant_receipts
            WHERE operation_id = $id ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString());
        var result = new List<CrossStoreParticipantReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CrossStoreParticipantReceipt
            {
                Participant = reader.GetString(0),
                IdempotencyKey = reader.GetString(1),
                State = (CrossStoreParticipantState)reader.GetInt32(2),
                RecordedAt = Parse(reader.GetString(3)),
                BeforeIdentity = reader.IsDBNull(4) ? null : reader.GetString(4),
                AfterIdentity = reader.IsDBNull(5) ? null : reader.GetString(5),
                ResultHash = reader.IsDBNull(6) ? null : reader.GetString(6),
                RecoveryAction = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        return result;
    }

    private static async Task<IReadOnlyList<CrossStoreOperationTransition>> ReadTransitionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sequence, state, occurred_at, reason FROM cross_store_operation_transitions WHERE operation_id = $id ORDER BY sequence;";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        var result = new List<CrossStoreOperationTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CrossStoreOperationTransition
            {
                Sequence = reader.GetInt64(0),
                State = (CrossStoreOperationState)reader.GetInt32(1),
                OccurredAt = Parse(reader.GetString(2)),
                Reason = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        return result;
    }

    private static async Task<CrossStoreOperation> RequireAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Operation '{operationId}' does not exist.");

    private static async Task<CrossStoreOperationId?> FindIdByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM cross_store_operations WHERE session_id = $sessionId AND idempotency_key = $key;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$key", idempotencyKey);
        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is null ? null : CrossStoreOperationId.Parse(raw);
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrossStoreOperationId operationId,
        long sequence,
        CrossStoreOperationState state,
        DateTimeOffset occurredAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO cross_store_operation_transitions(operation_id, sequence, state, occurred_at, reason) VALUES($id, $sequence, $state, $occurredAt, $reason);";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$occurredAt", Format(occurredAt));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrossStoreOperationId operationId,
        long expectedVersion,
        CrossStoreOperationState state,
        DateTimeOffset updatedAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE cross_store_operations SET state = $state, updated_at = $updatedAt, version = version + 1, reconciliation_reason = $reason WHERE operation_id = $id AND version = $version;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Operation '{operationId}' changed concurrently.");
    }

    private static bool EquivalentReceipt(
        CrossStoreParticipantReceipt existing,
        CrossStoreParticipantReceipt replay) =>
        existing.Participant == replay.Participant &&
        existing.IdempotencyKey == replay.IdempotencyKey &&
        existing.State == replay.State &&
        existing.BeforeIdentity == replay.BeforeIdentity &&
        existing.AfterIdentity == replay.AfterIdentity &&
        existing.ResultHash == replay.ResultHash &&
        existing.RecoveryAction == replay.RecoveryAction;

    private static bool CanTransition(
        CrossStoreOperationState source,
        CrossStoreOperationState target) =>
        target == CrossStoreOperationState.ReconciliationRequired ||
        (source, target) is
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.RevisionCommitted) or
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.RevisionCommitted, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.Published, CrossStoreOperationState.Completed) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.RevisionCommitted) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Published) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Completed);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
