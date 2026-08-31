using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>
/// Transactional operational saga store. Operation heads are mutable
/// projections; participant receipts and transitions are immutable rows.
/// </summary>
public sealed class SqliteCrossStoreOperationStore :
    ICrossStoreOperationStore,
    ISessionEvidenceOutbox
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
        ValidateSessionId(request.SessionId, nameof(request));
        ValidateExternalOperation(request.ExternalOperation, nameof(request));
        if (request.StartedAt == default)
            throw new ArgumentException("An operation start time is required.", nameof(request));
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
        ValidateOperationId(id, nameof(request));
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cross_store_operations(
                    operation_id, session_id, external_system, external_operation_id, kind, idempotency_key,
                    state, application_phase, created_at, updated_at, version, status_reason_code)
                VALUES($id, $sessionId, $system, $operationId, $kind, $key, $state, $phase, $created, $updated, 1, NULL);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            insert.Parameters.AddWithValue("$system", request.ExternalOperation.System);
            insert.Parameters.AddWithValue("$operationId", request.ExternalOperation.Id);
            insert.Parameters.AddWithValue("$kind", request.Kind);
            insert.Parameters.AddWithValue("$key", request.IdempotencyKey);
            insert.Parameters.AddWithValue("$state", (int)CrossStoreOperationState.Prepared);
            insert.Parameters.AddWithValue("$phase", (object?)request.InitialApplicationPhase ?? DBNull.Value);
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
            request.InitialApplicationPhase, request.StartedAt, null, cancellationToken).ConfigureAwait(false);
        await InsertEvidenceAsync(
            connection,
            transaction,
            request.SessionId,
            SessionEventTypes.OperationPrepared,
            request.StartedAt,
            $"operation:{id}:prepared",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operationId"] = id.ToString(),
                ["operationKind"] = request.Kind,
                ["operationState"] = CrossStoreOperationState.Prepared.ToString(),
                ["externalSystem"] = request.ExternalOperation.System,
                ["externalOperationId"] = request.ExternalOperation.Id,
                ["applicationPhase"] = request.InitialApplicationPhase ?? string.Empty
            },
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{id}' disappeared after creation.");
    }

    public async Task<CrossStoreOperation?> GetAsync(
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrossStoreOperation?> FindByExternalOperationAsync(
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default)
    {
        ValidateExternalOperation(externalOperation, nameof(externalOperation));
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
        command.Parameters.AddWithValue("$operationId", externalOperation.Id);
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
        ValidateSessionId(sessionId, nameof(sessionId));
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
        ValidateOperationId(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.IdempotencyKey);
        if (!Enum.IsDefined(receipt.State))
            throw new ArgumentOutOfRangeException(nameof(receipt));
        if (receipt.RecordedAt == default)
            throw new ArgumentException("A participant receipt time is required.", nameof(receipt));
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
        if (receipt.RecordedAt < operation.UpdatedAt)
            throw new ArgumentOutOfRangeException(
                nameof(receipt),
                "Participant receipt time cannot precede the current operation state.");
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
                    before_identity, after_identity, result_hash, suggested_action_code)
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
            insert.Parameters.AddWithValue("$recovery", (object?)receipt.SuggestedActionCode ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpdateHeadAsync(
            connection, transaction, operationId, operation.Version,
            operation.State, operation.ApplicationPhase, receipt.RecordedAt, operation.StatusReasonCode,
            cancellationToken).ConfigureAwait(false);
        await InsertEvidenceAsync(
            connection,
            transaction,
            operation.SessionId,
            SessionEventTypes.OperationParticipantRecorded,
            receipt.RecordedAt,
            $"operation:{operationId}:participant:{receipt.Participant}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operationId"] = operationId.ToString(),
                ["participant"] = receipt.Participant,
                ["participantState"] = receipt.State.ToString(),
                ["beforeIdentity"] = receipt.BeforeIdentity ?? string.Empty,
                ["afterIdentity"] = receipt.AfterIdentity ?? string.Empty,
                ["resultHash"] = receipt.ResultHash ?? string.Empty,
                ["suggestedActionCode"] = receipt.SuggestedActionCode ?? string.Empty
            },
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{operationId}' disappeared after receipt append.");
    }

    public async Task<CrossStoreOperation> TransitionAsync(
        CrossStoreOperationId operationId,
        CrossStoreOperationState targetState,
        DateTimeOffset occurredAt,
        string? applicationPhase = null,
        string? reasonCode = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));
        if (!Enum.IsDefined(targetState))
            throw new ArgumentOutOfRangeException(nameof(targetState));
        if (occurredAt == default)
            throw new ArgumentException("A transition time is required.", nameof(occurredAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var operation = await RequireAsync(connection, transaction, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.State == targetState &&
            string.Equals(operation.ApplicationPhase, applicationPhase, StringComparison.Ordinal) &&
            (targetState != CrossStoreOperationState.ReconciliationRequired ||
             string.Equals(operation.StatusReasonCode, reasonCode, StringComparison.Ordinal)))
        {
            transaction.Commit();
            return operation;
        }
        if (occurredAt < operation.UpdatedAt)
            throw new ArgumentOutOfRangeException(
                nameof(occurredAt),
                "Operation transition time cannot precede the current operation state.");
        if (!CanTransition(operation.State, targetState))
            throw new InvalidOperationException(
                $"Operation cannot transition from {operation.State} to {targetState}.");
        if (targetState == CrossStoreOperationState.ReconciliationRequired &&
            string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A reconciliation reason code is required.", nameof(reasonCode));
        if (targetState == CrossStoreOperationState.Completed &&
            operation.Participants.Any(item => item.State == CrossStoreParticipantState.Failed))
            throw new InvalidOperationException("An operation with a failed participant cannot complete.");

        var reason = targetState == CrossStoreOperationState.ReconciliationRequired
            ? reasonCode
            : null;
        await InsertTransitionAsync(
            connection, transaction, operationId, operation.Transitions.Count + 1,
            targetState, applicationPhase, occurredAt, reason, cancellationToken).ConfigureAwait(false);
        await UpdateHeadAsync(
            connection, transaction, operationId, operation.Version,
            targetState, applicationPhase, occurredAt, reason, cancellationToken).ConfigureAwait(false);
        var transitionSequence = operation.Transitions.Count + 1;
        await InsertEvidenceAsync(
            connection,
            transaction,
            operation.SessionId,
            SessionEventTypes.OperationTransitioned,
            occurredAt,
            $"operation:{operationId}:transition:{transitionSequence}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operationId"] = operationId.ToString(),
                ["operationState"] = targetState.ToString(),
                ["applicationPhase"] = applicationPhase ?? string.Empty,
                ["reasonCode"] = reason ?? string.Empty,
                ["transitionSequence"] = transitionSequence.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Operation '{operationId}' disappeared after transition.");
    }

    public async Task<IReadOnlyList<SessionEvidenceOutboxRecord>> ListPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_id, session_id, event_type, occurred_at,
                   idempotency_key, cross_system_refs_json, delivered_at
            FROM cross_store_evidence_outbox
            WHERE delivered_at IS NULL
            ORDER BY occurred_at, receipt_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<SessionEvidenceOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SessionEvidenceOutboxRecord
            {
                ReceiptId = Guid.Parse(reader.GetString(0)),
                SessionId = SessionId.Parse(reader.GetString(1)),
                EventType = reader.GetString(2),
                OccurredAt = Parse(reader.GetString(3)),
                IdempotencyKey = reader.GetString(4),
                CrossSystemRefs = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        reader.GetString(5))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                DeliveredAt = reader.IsDBNull(6) ? null : Parse(reader.GetString(6))
            });
        return result;
    }

    public async Task MarkDeliveredAsync(
        Guid receiptId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty)
            throw new ArgumentException("A non-empty receipt ID is required.", nameof(receiptId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE cross_store_evidence_outbox
            SET delivered_at = COALESCE(delivered_at, $deliveredAt)
            WHERE receipt_id = $receiptId;
            """;
        command.Parameters.AddWithValue("$deliveredAt", Format(deliveredAt));
        command.Parameters.AddWithValue("$receiptId", receiptId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException(
                $"Evidence outbox record '{receiptId:D}' does not exist.");
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
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await HongxianSqliteSchema.EnsureAsync(
                connection,
                HongxianSqliteSchema.OperationComponent,
                1,
                MigrateSchemaAsync,
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task MigrateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        if (fromVersion != 0)
            throw new HongxianSqliteSchemaCompatibilityException(
                HongxianSqliteSchema.OperationComponent, fromVersion, 1);
        await HongxianSqliteSchema.ExecuteAsync(
            connection,
            transaction,
            $$"""
            CREATE TABLE IF NOT EXISTS cross_store_operations(
                operation_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL,
                external_system TEXT NOT NULL,
                external_operation_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                application_phase TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                version INTEGER NOT NULL,
                status_reason_code TEXT NULL,
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
                suggested_action_code TEXT NULL,
                PRIMARY KEY(operation_id, participant),
                UNIQUE(operation_id, idempotency_key)
            );
            CREATE TABLE IF NOT EXISTS cross_store_operation_transitions(
                operation_id TEXT NOT NULL REFERENCES cross_store_operations(operation_id) ON DELETE RESTRICT,
                sequence INTEGER NOT NULL,
                state INTEGER NOT NULL,
                application_phase TEXT NULL,
                occurred_at TEXT NOT NULL,
                reason_code TEXT NULL,
                PRIMARY KEY(operation_id, sequence)
            );
            CREATE TABLE IF NOT EXISTS cross_store_evidence_outbox(
                receipt_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                cross_system_refs_json TEXT NOT NULL,
                delivered_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cross_store_evidence_pending
                ON cross_store_evidence_outbox(delivered_at, occurred_at, receipt_id);
            """,
            cancellationToken).ConfigureAwait(false);

        await HongxianSqliteSchema.EnsureColumnAsync(
            connection, transaction, "cross_store_operations", "application_phase",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await HongxianSqliteSchema.EnsureColumnAsync(
            connection, transaction, "cross_store_operations", "status_reason_code",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await HongxianSqliteSchema.EnsureColumnAsync(
            connection, transaction, "cross_store_participant_receipts", "suggested_action_code",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await HongxianSqliteSchema.EnsureColumnAsync(
            connection, transaction, "cross_store_operation_transitions", "application_phase",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await HongxianSqliteSchema.EnsureColumnAsync(
            connection, transaction, "cross_store_operation_transitions", "reason_code",
            "TEXT NULL", cancellationToken).ConfigureAwait(false);

        if (await HongxianSqliteSchema.ColumnExistsAsync(
                connection, transaction, "cross_store_operations", "reconciliation_reason",
                cancellationToken).ConfigureAwait(false))
            await HongxianSqliteSchema.ExecuteAsync(
                connection, transaction,
                "UPDATE cross_store_operations SET status_reason_code = reconciliation_reason WHERE status_reason_code IS NULL;",
                cancellationToken).ConfigureAwait(false);
        if (await HongxianSqliteSchema.ColumnExistsAsync(
                connection, transaction, "cross_store_participant_receipts", "recovery_action",
                cancellationToken).ConfigureAwait(false))
            await HongxianSqliteSchema.ExecuteAsync(
                connection, transaction,
                "UPDATE cross_store_participant_receipts SET suggested_action_code = recovery_action WHERE suggested_action_code IS NULL;",
                cancellationToken).ConfigureAwait(false);
        if (await HongxianSqliteSchema.ColumnExistsAsync(
                connection, transaction, "cross_store_operation_transitions", "reason",
                cancellationToken).ConfigureAwait(false))
            await HongxianSqliteSchema.ExecuteAsync(
                connection, transaction,
                "UPDATE cross_store_operation_transitions SET reason_code = reason WHERE reason_code IS NULL;",
                cancellationToken).ConfigureAwait(false);

        await HongxianSqliteSchema.ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE cross_store_operations
            SET application_phase = CASE state
                    WHEN 1 THEN 'legacy-revision-committed'
                    WHEN 2 THEN 'legacy-published'
                    ELSE application_phase
                END
            WHERE application_phase IS NULL AND state IN (1, 2);
            UPDATE cross_store_operation_transitions
            SET application_phase = CASE state
                    WHEN 1 THEN 'legacy-revision-committed'
                    WHEN 2 THEN 'legacy-published'
                    ELSE application_phase
                END
            WHERE application_phase IS NULL AND state IN (1, 2);
            UPDATE cross_store_operations SET state = 1 WHERE state = 2;
            UPDATE cross_store_operation_transitions SET state = 1 WHERE state = 2;
            """,
            cancellationToken).ConfigureAwait(false);
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
                   application_phase, created_at, updated_at, version, status_reason_code
            FROM cross_store_operations WHERE operation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var sessionId = SessionId.Parse(reader.GetString(0));
        var externalOperation = new ExternalOperationReference(
            reader.GetString(1),
            reader.GetString(2));
        var kind = reader.GetString(3);
        var key = reader.GetString(4);
        var state = (CrossStoreOperationState)reader.GetInt32(5);
        var applicationPhase = reader.IsDBNull(6) ? null : reader.GetString(6);
        var createdAt = Parse(reader.GetString(7));
        var updatedAt = Parse(reader.GetString(8));
        var version = reader.GetInt64(9);
        var reason = reader.IsDBNull(10) ? null : reader.GetString(10);
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
            ApplicationPhase = applicationPhase,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Version = version,
            StatusReasonCode = reason,
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
                   after_identity, result_hash, suggested_action_code
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
                SuggestedActionCode = reader.IsDBNull(7) ? null : reader.GetString(7)
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
        command.CommandText = "SELECT sequence, state, application_phase, occurred_at, reason_code FROM cross_store_operation_transitions WHERE operation_id = $id ORDER BY sequence;";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        var result = new List<CrossStoreOperationTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CrossStoreOperationTransition
            {
                Sequence = reader.GetInt64(0),
                State = (CrossStoreOperationState)reader.GetInt32(1),
                ApplicationPhase = reader.IsDBNull(2) ? null : reader.GetString(2),
                OccurredAt = Parse(reader.GetString(3)),
                Reason = reader.IsDBNull(4) ? null : reader.GetString(4)
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

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        string eventType,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        IReadOnlyDictionary<string, string> references,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cross_store_evidence_outbox(
                receipt_id, session_id, event_type, occurred_at,
                idempotency_key, cross_system_refs_json, delivered_at)
            VALUES($receiptId, $sessionId, $eventType, $occurredAt, $key, $refs, NULL)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$receiptId", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$occurredAt", Format(occurredAt));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(references));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrossStoreOperationId operationId,
        long sequence,
        CrossStoreOperationState state,
        string? applicationPhase,
        DateTimeOffset occurredAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO cross_store_operation_transitions(operation_id, sequence, state, application_phase, occurred_at, reason_code) VALUES($id, $sequence, $state, $phase, $occurredAt, $reason);";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$phase", (object?)applicationPhase ?? DBNull.Value);
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
        string? applicationPhase,
        DateTimeOffset updatedAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE cross_store_operations SET state = $state, application_phase = $phase, updated_at = $updatedAt, version = version + 1, status_reason_code = $reason WHERE operation_id = $id AND version = $version;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$phase", (object?)applicationPhase ?? DBNull.Value);
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
        existing.SuggestedActionCode == replay.SuggestedActionCode;

    private static bool CanTransition(
        CrossStoreOperationState source,
        CrossStoreOperationState target) =>
        target == CrossStoreOperationState.ReconciliationRequired ||
        (source, target) is
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.Active) or
            (CrossStoreOperationState.Prepared, CrossStoreOperationState.Completed) or
            (CrossStoreOperationState.Active, CrossStoreOperationState.Active) or
            (CrossStoreOperationState.Active, CrossStoreOperationState.Completed) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Active) or
            (CrossStoreOperationState.ReconciliationRequired, CrossStoreOperationState.Completed);

    private static void ValidateSessionId(SessionId sessionId, string parameterName)
    {
        if (sessionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", parameterName);
    }

    private static void ValidateOperationId(
        CrossStoreOperationId operationId,
        string parameterName)
    {
        if (operationId.Value == Guid.Empty)
            throw new ArgumentException(
                "A non-empty cross-store operation ID is required.",
                parameterName);
    }

    private static void ValidateExternalOperation(
        ExternalOperationReference operation,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(operation.System) ||
            string.IsNullOrWhiteSpace(operation.Id))
            throw new ArgumentException(
                "A complete external operation reference is required.",
                parameterName);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
