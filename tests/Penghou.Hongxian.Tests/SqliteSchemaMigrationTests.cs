using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SqliteSchemaMigrationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-schema-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedDatabase_TracksCatalogAndProjectionVersionsIndependently()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "shared.db");
        var catalog = new SqliteSessionCatalog(path, pooling: false);
        var projection = new SqliteSessionProjectionStore(path, pooling: false);

        await catalog.ListAsync(ct);
        await projection.ListAsync(ct);

        await using var connection = await OpenAsync(path, ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT component, version FROM hongxian_schema_versions ORDER BY component;";
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            versions.Add(reader.GetString(0), reader.GetInt64(1));
        versions.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["session-catalog"] = 1,
            ["session-projection"] = 1
        });
    }

    [Fact]
    public async Task NewerComponentSchema_IsRejectedWithTypedDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "newer.db");
        var catalog = new SqliteSessionCatalog(path, pooling: false);
        await catalog.ListAsync(ct);
        await using (var connection = await OpenAsync(path, ct))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE hongxian_schema_versions
                SET version = 99
                WHERE component = 'session-catalog';
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        var action = () => new SqliteSessionCatalog(path, pooling: false).ListAsync(ct);

        var error = await action.Should()
            .ThrowAsync<HongxianSqliteSchemaCompatibilityException>();
        error.Which.Component.Should().Be("session-catalog");
        error.Which.DetectedVersion.Should().Be(99);
        error.Which.SupportedVersion.Should().Be(1);
    }

    [Fact]
    public async Task PreviewOneOperationSchema_IsUpgradedWithoutLosingEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "legacy.db");
        var operationId = CrossStoreOperationId.New();
        var sessionId = SessionId.New();
        await CreateLegacyOperationDatabaseAsync(
            path, operationId, sessionId, blockMigration: false, ct);

        var store = new SqliteCrossStoreOperationStore(path, pooling: false);
        var operation = await store.GetAsync(operationId, ct);

        operation.Should().NotBeNull();
        operation!.State.Should().Be(CrossStoreOperationState.Active);
        operation.ApplicationPhase.Should().Be("legacy-published");
        operation.StatusReasonCode.Should().Be("legacy-reason");
        operation.Participants.Should().ContainSingle().Which.SuggestedActionCode
            .Should().Be("legacy-recovery");
        operation.Transitions.Select(item => item.State).Should().Equal(
            CrossStoreOperationState.Prepared,
            CrossStoreOperationState.Active);
        operation.Transitions[^1].ApplicationPhase.Should().Be("legacy-published");
        operation.Transitions[^1].Reason.Should().Be("legacy-transition-reason");
    }

    [Fact]
    public async Task FailedMigration_RollsBackAndCanBeRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "retry.db");
        var operationId = CrossStoreOperationId.New();
        var sessionId = SessionId.New();
        await CreateLegacyOperationDatabaseAsync(
            path, operationId, sessionId, blockMigration: true, ct);
        var store = new SqliteCrossStoreOperationStore(path, pooling: false);

        var failed = () => store.GetAsync(operationId, ct);
        await failed.Should().ThrowAsync<SqliteException>()
            .WithMessage("*migration blocked*");

        await using (var connection = await OpenAsync(path, ct))
        {
            await using var inspect = connection.CreateCommand();
            inspect.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name = 'hongxian_schema_versions';
                """;
            Convert.ToInt64(await inspect.ExecuteScalarAsync(ct)).Should().Be(0);
            await using var unblock = connection.CreateCommand();
            unblock.CommandText = "DROP TRIGGER block_legacy_migration;";
            await unblock.ExecuteNonQueryAsync(ct);
        }

        var recovered = await store.GetAsync(operationId, ct);
        recovered!.State.Should().Be(CrossStoreOperationState.Active);
        recovered.ApplicationPhase.Should().Be("legacy-published");
    }

    private static async Task CreateLegacyOperationDatabaseAsync(
        string path,
        CrossStoreOperationId operationId,
        SessionId sessionId,
        bool blockMigration,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = await OpenAsync(path, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE cross_store_operations(
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
            CREATE TABLE cross_store_participant_receipts(
                operation_id TEXT NOT NULL,
                participant TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state INTEGER NOT NULL,
                recorded_at TEXT NOT NULL,
                before_identity TEXT NULL,
                after_identity TEXT NULL,
                result_hash TEXT NULL,
                recovery_action TEXT NULL,
                PRIMARY KEY(operation_id, participant)
            );
            CREATE TABLE cross_store_operation_transitions(
                operation_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                state INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                reason TEXT NULL,
                PRIMARY KEY(operation_id, sequence)
            );
            INSERT INTO cross_store_operations(
                operation_id, session_id, external_system, external_operation_id,
                kind, idempotency_key, state, created_at, updated_at, version,
                reconciliation_reason)
            VALUES(
                '{{operationId}}', '{{sessionId}}', 'legacy-engine', 'legacy/run-7',
                'legacy-kind', 'legacy-key', 2, '2026-08-31T00:00:00.0000000+00:00',
                '2026-08-31T00:00:02.0000000+00:00', 3, 'legacy-reason');
            INSERT INTO cross_store_participant_receipts(
                operation_id, participant, idempotency_key, state, recorded_at,
                recovery_action)
            VALUES(
                '{{operationId}}', 'legacy-participant', 'legacy-participant-key', 2,
                '2026-08-31T00:00:01.0000000+00:00', 'legacy-recovery');
            INSERT INTO cross_store_operation_transitions(
                operation_id, sequence, state, occurred_at, reason)
            VALUES
                ('{{operationId}}', 1, 0, '2026-08-31T00:00:00.0000000+00:00', NULL),
                ('{{operationId}}', 2, 2, '2026-08-31T00:00:02.0000000+00:00',
                    'legacy-transition-reason');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!blockMigration) return;
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER block_legacy_migration
            BEFORE UPDATE ON cross_store_operations
            BEGIN
                SELECT RAISE(ABORT, 'migration blocked');
            END;
            """;
        await trigger.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqliteConnection> OpenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
