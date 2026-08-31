using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>
/// Concurrency-safe operational catalog for session identity, external-operation routing,
/// and accepted revisions. Immutable conversation evidence remains in
/// the per-session Siming ledger; this database is authoritative mutable state.
/// </summary>
public sealed class SqliteSessionCatalog :
    ISessionStore,
    ISessionDecisionLeaseProvider,
    ISessionEvidenceOutbox
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly bool pooling;
    private readonly TimeSpan leaseRetryDelay;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan leaseRenewalInterval;

    public SqliteSessionCatalog(
        string databasePath,
        TimeProvider? timeProvider = null,
        bool pooling = true,
        TimeSpan? decisionLeaseDuration = null,
        TimeSpan? decisionLeaseRenewalInterval = null,
        TimeSpan? decisionLeaseRetryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pooling = pooling;
        leaseDuration = decisionLeaseDuration ?? TimeSpan.FromSeconds(30);
        leaseRenewalInterval = decisionLeaseRenewalInterval ?? TimeSpan.FromSeconds(10);
        leaseRetryDelay = decisionLeaseRetryDelay ?? TimeSpan.FromMilliseconds(25);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(decisionLeaseDuration));
        if (leaseRenewalInterval <= TimeSpan.Zero || leaseRenewalInterval >= leaseDuration)
            throw new ArgumentOutOfRangeException(nameof(decisionLeaseRenewalInterval));
        if (leaseRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(decisionLeaseRetryDelay));
    }

    public async Task<Session> CreateAsync(
        string contextId,
        string resourceId,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        SessionContractValidation.ValidateSessionIdentity(contextId, resourceId);
        var id = sessionId ?? SessionId.New();
        ValidateSessionId(id, nameof(sessionId));
        var createdAt = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(session_id, context_id, resource_id, created_at, current_revision, version)
            VALUES($sessionId, $contextId, $resourceId, $createdAt, NULL, 0);
            """;
        command.Parameters.AddWithValue("$sessionId", id.ToString());
        command.Parameters.AddWithValue("$contextId", contextId);
        command.Parameters.AddWithValue("$resourceId", resourceId);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Session '{id}' already exists.", exception);
        }
        await InsertLifecycleReceiptAsync(
            connection,
            transaction,
            id,
            SessionEventTypes.SessionCreated,
            createdAt,
            $"session:{id}:created",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sessionId"] = id.ToString(),
                ["contextId"] = contextId,
                ["resourceId"] = resourceId,
                ["catalogVersion"] = "0"
            },
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();

        return new Session
        {
            Id = id,
            ContextId = contextId,
            ResourceId = resourceId,
            CreatedAt = createdAt
        };
    }

    public async Task<Session?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId, nameof(sessionId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Session>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, context_id, resource_id, created_at, current_revision, version
            FROM sessions
            ORDER BY created_at DESC, session_id DESC;
            """;
        var headers = new List<(SessionId Id, string ContextId, string ResourceId, DateTimeOffset CreatedAt, string? Revision, long Version)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                headers.Add(MapHeader(reader));
        }

        var sessions = new List<Session>(headers.Count);
        foreach (var header in headers)
            sessions.Add(await ReadSessionAsync(connection, header.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session '{header.Id}' disappeared while listing the catalog."));
        return sessions;
    }

    public async Task<Session?> FindByExternalOperationAsync(
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default)
    {
        ValidateExternalOperation(externalOperation, nameof(externalOperation));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM session_external_operations WHERE external_system = $system AND external_operation_id = $operationId;";
        command.Parameters.AddWithValue("$system", externalOperation.System);
        command.Parameters.AddWithValue("$operationId", externalOperation.Id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string raw
            ? await ReadSessionAsync(connection, SessionId.Parse(raw), cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<Session> AttachExternalOperationAsync(
        SessionId sessionId,
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId, nameof(sessionId));
        ValidateExternalOperation(externalOperation, nameof(externalOperation));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await ReadSessionAsync(connection, sessionId, cancellationToken, transaction).ConfigureAwait(false) is null)
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_external_operations(external_system, external_operation_id, session_id, attached_at)
            VALUES($system, $operationId, $sessionId, $attachedAt)
            ON CONFLICT(external_system, external_operation_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$system", externalOperation.System);
        command.Parameters.AddWithValue("$operationId", externalOperation.Id);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$attachedAt", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        var attached = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = "SELECT session_id FROM session_external_operations WHERE external_system = $system AND external_operation_id = $operationId;";
        owner.Parameters.AddWithValue("$system", externalOperation.System);
        owner.Parameters.AddWithValue("$operationId", externalOperation.Id);
        var actualOwner = (string?)await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualOwner, sessionId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"External operation '{externalOperation}' already belongs to session '{actualOwner}'.");
        if (attached == 1)
        {
            await using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = "UPDATE sessions SET version = version + 1 WHERE session_id = $sessionId;";
            advance.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.ExecutionAttached,
                timeProvider.GetUtcNow(),
                $"session:{sessionId}:external-operation:{externalOperation.System}:{externalOperation.Id}:attached",
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["externalSystem"] = externalOperation.System,
                    ["externalOperationId"] = externalOperation.Id
                },
                cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
        return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared after external-operation attachment.");
    }

    public async Task<Session> UpdateRevisionAsync(
        SessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId, nameof(sessionId));
        SessionContractValidation.ValidateRevision(expectedRevision, nameof(expectedRevision));
        SessionContractValidation.ValidateRevision(replacementRevision, nameof(replacementRevision));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedRevision is null
            ? "UPDATE sessions SET current_revision = $replacement, version = version + 1 WHERE session_id = $sessionId AND current_revision IS NULL;"
            : "UPDATE sessions SET current_revision = $replacement, version = version + 1 WHERE session_id = $sessionId AND current_revision = $expected;";
        command.Parameters.AddWithValue("$replacement", replacementRevision);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        if (expectedRevision is not null)
            command.Parameters.AddWithValue("$expected", expectedRevision);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.RevisionAccepted,
                timeProvider.GetUtcNow(),
                $"session:{sessionId}:resource:{replacementRevision}",
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["fromRevision"] = expectedRevision ?? "uninitialized",
                    ["toRevision"] = replacementRevision
                },
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return await ReadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        }
        var current = await ReadSessionAsync(
            connection, sessionId, cancellationToken, transaction).ConfigureAwait(false);
        if (current is null)
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        transaction.Commit();
        throw new SessionRevisionConflictException(
            sessionId,
            expectedRevision,
            current.CurrentRevision,
            current.Version);
    }

    public async ValueTask<ISessionDecisionLease> AcquireAsync(
        SessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId, nameof(sessionId));
        if (operationId == Guid.Empty)
            throw new ArgumentException("A non-empty operation ID is required.", nameof(operationId));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var previousLease = await ReadDecisionLeaseAsync(
                connection, transaction, sessionId, cancellationToken).ConfigureAwait(false);
            if (previousLease is not null && previousLease.Value.ExpiresAt > now)
            {
                transaction.Commit();
                await Task.Delay(leaseRetryDelay, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            var token = await NextFencingTokenAsync(
                connection, transaction, sessionId, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_decision_leases(session_id, operation_id, fencing_token, acquired_at, expires_at)
                VALUES($sessionId, $operationId, $token, $now, $expires)
                ON CONFLICT(session_id) DO UPDATE SET
                    operation_id = excluded.operation_id,
                    fencing_token = excluded.fencing_token,
                    acquired_at = excluded.acquired_at,
                    expires_at = excluded.expires_at;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            var expiresAt = now + leaseDuration;
            command.Parameters.AddWithValue("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                if (previousLease is not null && previousLease.Value.ExpiresAt <= now)
                {
                    await InsertLifecycleReceiptAsync(
                        connection,
                        transaction,
                        sessionId,
                        SessionEventTypes.DecisionLeaseExpired,
                        now,
                        $"session:{sessionId}:decision-lease:{previousLease.Value.Token}:expired",
                        previousLease.Value.OperationId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sessionId"] = sessionId.ToString(),
                            ["operationId"] = previousLease.Value.OperationId.ToString("D"),
                            ["fencingToken"] = previousLease.Value.Token.ToString(CultureInfo.InvariantCulture),
                            ["expiredAt"] = previousLease.Value.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                await InsertLifecycleReceiptAsync(
                    connection,
                    transaction,
                    sessionId,
                    SessionEventTypes.DecisionLeaseAcquired,
                    now,
                    $"session:{sessionId}:decision-lease:{token}:acquired",
                    operationId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["sessionId"] = sessionId.ToString(),
                        ["operationId"] = operationId.ToString("D"),
                        ["fencingToken"] = token.ToString(CultureInfo.InvariantCulture),
                        ["expiresAt"] = expiresAt.ToString("O", CultureInfo.InvariantCulture)
                    },
                    cancellationToken).ConfigureAwait(false);
                transaction.Commit();
                return new SqliteDecisionLease(
                    this, sessionId, operationId, token, now, expiresAt);
            }
            transaction.Commit();
            await Task.Delay(leaseRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DateTimeOffset> RenewAsync(
        SessionId sessionId,
        long token,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_decision_leases SET expires_at = $expires WHERE session_id = $sessionId AND fencing_token = $token;";
        var expiresAt = now + leaseDuration;
        command.Parameters.AddWithValue("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$token", token);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new SessionDecisionLeaseLostException(sessionId, token);
        return expiresAt;
    }

    private async Task ReleaseAsync(
        SessionId sessionId,
        Guid operationId,
        long token)
    {
        await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM session_decision_leases WHERE session_id = $sessionId AND fencing_token = $token;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$token", token);
        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) == 1)
        {
            var now = timeProvider.GetUtcNow();
            await InsertLifecycleReceiptAsync(
                connection,
                transaction,
                sessionId,
                SessionEventTypes.DecisionLeaseReleased,
                now,
                $"session:{sessionId}:decision-lease:{token}:released",
                operationId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["operationId"] = operationId.ToString("D"),
                    ["fencingToken"] = token.ToString(CultureInfo.InvariantCulture)
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        transaction.Commit();
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
            SELECT receipt_id, session_id, event_type, occurred_at, idempotency_key,
                   correlation_id, cross_system_refs_json, delivered_at
            FROM session_lifecycle_receipts
            WHERE delivered_at IS NULL
            ORDER BY occurred_at, receipt_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<SessionEvidenceOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SessionEvidenceOutboxRecord
            {
                ReceiptId = Guid.Parse(reader.GetString(0)),
                SessionId = SessionId.Parse(reader.GetString(1)),
                EventType = reader.GetString(2),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                IdempotencyKey = reader.GetString(4),
                CorrelationId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                CrossSystemRefs = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                DeliveredAt = reader.IsDBNull(7)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
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
        if (deliveredAt == default)
            throw new ArgumentException("A delivery time is required.", nameof(deliveredAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_lifecycle_receipts SET delivered_at = COALESCE(delivered_at, $deliveredAt) WHERE receipt_id = $receiptId;";
        command.Parameters.AddWithValue("$deliveredAt", deliveredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$receiptId", receiptId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException($"Evidence outbox record '{receiptId:D}' does not exist.");
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
                HongxianSqliteSchema.CatalogComponent,
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
                HongxianSqliteSchema.CatalogComponent, fromVersion, 1);
        await HongxianSqliteSchema.ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS sessions(
                session_id TEXT PRIMARY KEY NOT NULL,
                context_id TEXT NOT NULL,
                resource_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                current_revision TEXT NULL,
                version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS session_external_operations(
                external_system TEXT NOT NULL,
                external_operation_id TEXT NOT NULL,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                attached_at TEXT NOT NULL,
                PRIMARY KEY(external_system, external_operation_id)
            );
            CREATE INDEX IF NOT EXISTS ix_session_external_operations_session
                ON session_external_operations(session_id, attached_at);
            CREATE TABLE IF NOT EXISTS session_decision_leases(
                session_id TEXT PRIMARY KEY NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL,
                fencing_token INTEGER NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_decision_fence_counters(
                session_id TEXT PRIMARY KEY NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                last_token INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_lifecycle_receipts(
                receipt_id TEXT PRIMARY KEY NOT NULL,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                correlation_id TEXT NULL,
                cross_system_refs_json TEXT NOT NULL,
                delivered_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_session_lifecycle_pending
                ON session_lifecycle_receipts(delivered_at, occurred_at, receipt_id);
            """,
            cancellationToken).ConfigureAwait(false);

        // Preview 1 used GUID text fencing tokens. Leases are ephemeral and
        // cannot remain authoritative across a package/schema upgrade.
        await HongxianSqliteSchema.ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM session_decision_leases;",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Session?> ReadSessionAsync(
        SqliteConnection connection,
        SessionId sessionId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, context_id, resource_id, created_at, current_revision, version FROM sessions WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var header = MapHeader(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        var externalOperations = await ReadExternalOperationsAsync(
            connection, header.Id, cancellationToken, transaction).ConfigureAwait(false);
        return new Session
        {
            Id = header.Id,
            ContextId = header.ContextId,
            ResourceId = header.ResourceId,
            CreatedAt = header.CreatedAt,
            CurrentRevision = header.Revision,
            ExternalOperations = externalOperations,
            Version = header.Version
        };
    }

    private static (SessionId Id, string ContextId, string ResourceId, DateTimeOffset CreatedAt, string? Revision, long Version) MapHeader(
        SqliteDataReader reader) =>
        (
            SessionId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5)
        );

    private static async Task<IReadOnlyList<ExternalOperationReference>> ReadExternalOperationsAsync(
        SqliteConnection connection,
        SessionId id,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var runs = connection.CreateCommand();
        runs.Transaction = transaction;
        runs.CommandText = "SELECT external_system, external_operation_id FROM session_external_operations WHERE session_id = $sessionId ORDER BY attached_at, external_system, external_operation_id;";
        runs.Parameters.AddWithValue("$sessionId", id.ToString());
        var externalOperations = new List<ExternalOperationReference>();
        await using var runReader = await runs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await runReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            externalOperations.Add(new ExternalOperationReference(
                runReader.GetString(0),
                runReader.GetString(1)));
        return externalOperations;
    }

    private static async Task InsertLifecycleReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        string eventType,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        Guid? correlationId,
        IReadOnlyDictionary<string, string> crossSystemRefs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_lifecycle_receipts(
                receipt_id, session_id, event_type, occurred_at, idempotency_key,
                correlation_id, cross_system_refs_json, delivered_at)
            VALUES($receiptId, $sessionId, $eventType, $occurredAt, $key,
                $correlationId, $refs, NULL)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$receiptId", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$correlationId", correlationId is null
            ? DBNull.Value
            : correlationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(crossSystemRefs));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> NextFencingTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_decision_fence_counters(session_id, last_token)
            VALUES($sessionId, 1)
            ON CONFLICT(session_id) DO UPDATE SET last_token = last_token + 1
            RETURNING last_token;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Fencing token allocation returned no value."));
    }

    private async Task AssertLeaseOwnershipAsync(
        SessionId sessionId,
        Guid operationId,
        long token,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT expires_at
            FROM session_decision_leases
            WHERE session_id = $sessionId
              AND operation_id = $operationId
              AND fencing_token = $token;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        command.Parameters.AddWithValue("$token", token);
        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null || DateTimeOffset.Parse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= now)
            throw new SessionDecisionLeaseLostException(sessionId, token);
    }

    private static async Task<(Guid OperationId, long Token, DateTimeOffset ExpiresAt)?> ReadDecisionLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id, fencing_token, expires_at FROM session_decision_leases WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return (
            Guid.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void ValidateSessionId(SessionId sessionId, string parameterName)
    {
        if (sessionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", parameterName);
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

    private sealed class SqliteDecisionLease : ISessionDecisionLease
    {
        private readonly SqliteSessionCatalog owner;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenSource lost = new();
        private readonly Task renewal;
        private Exception? renewalFailure;
        private long expiresAtUnixMilliseconds;
        private int disposed;

        public SqliteDecisionLease(
            SqliteSessionCatalog owner,
            SessionId sessionId,
            Guid operationId,
            long token,
            DateTimeOffset acquiredAt,
            DateTimeOffset expiresAt)
        {
            this.owner = owner;
            SessionId = sessionId;
            OperationId = operationId;
            AcquiredAt = acquiredAt;
            FencingToken = token;
            expiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds();
            renewal = RenewLoopAsync();
        }

        public SessionId SessionId { get; }
        public Guid OperationId { get; }
        public DateTimeOffset AcquiredAt { get; }
        public long FencingToken { get; }
        public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(
            Interlocked.Read(ref expiresAtUnixMilliseconds));
        public CancellationToken LeaseLost => lost.Token;

        public async Task AssertOwnershipAsync(
            CancellationToken cancellationToken = default)
        {
            if (lost.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
                throw new SessionDecisionLeaseLostException(
                    SessionId, FencingToken, renewalFailure);
            try
            {
                await owner.AssertLeaseOwnershipAsync(
                    SessionId, OperationId, FencingToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SessionDecisionLeaseLostException)
            {
                await lost.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await stop.CancelAsync().ConfigureAwait(false);
            try { await renewal.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception) { renewalFailure ??= exception; }
            try
            {
                await owner.ReleaseAsync(SessionId, OperationId, FencingToken).ConfigureAwait(false);
            }
            finally
            {
                stop.Dispose();
                lost.Dispose();
            }
            if (renewalFailure is not null)
                throw new SessionDecisionLeaseLostException(
                    SessionId, FencingToken, renewalFailure);
        }

        private async Task RenewLoopAsync()
        {
            while (true)
            {
                await Task.Delay(
                    owner.leaseRenewalInterval, owner.timeProvider, stop.Token)
                    .ConfigureAwait(false);
                try
                {
                    var expiresAt = await owner.RenewAsync(
                        SessionId, FencingToken, stop.Token).ConfigureAwait(false);
                    Interlocked.Exchange(
                        ref expiresAtUnixMilliseconds,
                        expiresAt.ToUnixTimeMilliseconds());
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    renewalFailure = exception;
                    await lost.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
    }
}
