using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>Rebuildable current-state projections for independently stored session ledgers.</summary>
public sealed class SqliteSessionProjectionStore :
    ISessionProjectionStore,
    ISessionProjectionDeliveryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly bool pooling;

    public SqliteSessionProjectionStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pooling = pooling;
    }

    public async Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is not null && sessionEvent.Sequence <= current.AppliedSequence)
        {
            if (sessionEvent.Sequence == current.AppliedSequence &&
                !string.Equals(sessionEvent.Hash, current.HeadHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Session projection head conflict for '{sessionEvent.SessionId}' at sequence {sessionEvent.Sequence}.");
            await MarkAppliedAsync(
                connection, transaction, sessionEvent, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return;
        }
        var expected = (current?.AppliedSequence ?? 0) + 1;
        if (sessionEvent.Sequence != expected)
            throw new InvalidOperationException($"Session projection gap for '{sessionEvent.SessionId}': expected sequence {expected}, received {sessionEvent.Sequence}.");
        var state = SessionTimelineProjection.Apply(current?.State, sessionEvent);
        await WriteAsync(connection, transaction, new SessionProjectionSnapshot(
                sessionEvent.SessionId, sessionEvent.Sequence, sessionEvent.Hash, state), cancellationToken)
            .ConfigureAwait(false);
        await MarkAppliedAsync(
            connection, transaction, sessionEvent, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<SessionProjectionSnapshot?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, applied_sequence, head_hash, state_json FROM session_projections ORDER BY session_id;";
        var result = new List<SessionProjectionSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Map(reader));
        return result;
    }

    public async Task<SessionProjectionSnapshot?> RebuildAsync(
        VerifiedSessionHistory history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(history.VerifiedHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(history.VerifiedHead.LedgerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(history.VerifiedHead.Hash);
        if (history.VerifiedHead.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(history));
        if (history.Events.Any(item => item.SessionId != history.SessionId))
            throw new ArgumentException(
                "Every rebuilt event must belong to the verified session.", nameof(history));
        var ordered = history.Events.OrderBy(item => item.Sequence).ToArray();
        if (ordered.LongLength != history.VerifiedHead.Sequence)
            throw new InvalidOperationException(
                $"Cannot rebuild session '{history.SessionId}': verified head sequence " +
                $"{history.VerifiedHead.Sequence} does not match {ordered.LongLength} supplied events.");
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Sequence != index + 1)
                throw new InvalidOperationException(
                    $"Cannot rebuild session '{history.SessionId}': sequence {index + 1} is missing.");
            var expectedPrevious = index == 0 ? null : ordered[index - 1].Hash;
            if (!string.Equals(
                    ordered[index].PreviousHash,
                    expectedPrevious,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Cannot rebuild session '{history.SessionId}': hash-chain continuity failed at sequence {index + 1}.");
        }
        if (ordered.Length > 0 && !string.Equals(
                ordered[^1].Hash,
                history.VerifiedHead.Hash,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot rebuild session '{history.SessionId}': supplied history does not reach the verified head hash.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(
            connection, transaction, history.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.AppliedSequence > history.VerifiedHead.Sequence)
            throw new InvalidOperationException(
                $"Cannot rebuild session '{history.SessionId}' to verified sequence {history.VerifiedHead.Sequence}: " +
                $"the projection has already reached sequence {current.AppliedSequence}.");
        if (current is not null && current.AppliedSequence == history.VerifiedHead.Sequence &&
            !string.Equals(current.HeadHash, history.VerifiedHead.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Session projection head conflict for '{history.SessionId}' at sequence {history.VerifiedHead.Sequence}.");
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM session_projections WHERE session_id = $sessionId;";
            delete.Parameters.AddWithValue("$sessionId", history.SessionId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        SessionCurrentState? state = null;
        foreach (var sessionEvent in ordered) state = SessionTimelineProjection.Apply(state, sessionEvent);
        if (state is null) { transaction.Commit(); return null; }
        var snapshot = new SessionProjectionSnapshot(
            history.SessionId, ordered[^1].Sequence, ordered[^1].Hash, state);
        await WriteAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        await MarkAppliedAsync(
            connection, transaction, ordered[^1], cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return snapshot;
    }

    public async Task RecordCommittedAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadDeliveryStatusAsync(
            connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.CommittedSequence == sessionEvent.Sequence &&
            !string.Equals(current.CommittedHeadHash, sessionEvent.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Projection delivery head conflict for '{sessionEvent.SessionId}' at sequence {sessionEvent.Sequence}.");
        if (current is null || sessionEvent.Sequence > current.CommittedSequence)
        {
            var applied = current ?? await StatusFromProjectionAsync(
                connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
            await WriteDeliveryStatusAsync(
                connection,
                transaction,
                new SessionProjectionDeliveryStatus(
                    sessionEvent.SessionId,
                    sessionEvent.Sequence,
                    sessionEvent.Hash,
                    applied?.AppliedSequence ?? 0,
                    applied?.AppliedHeadHash,
                    timeProvider.GetUtcNow(),
                    current?.LastFailureType,
                    current?.LastFailureDetail),
                cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public async Task RecordFailureAsync(
        SessionEvent sessionEvent,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ArgumentNullException.ThrowIfNull(exception);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadDeliveryStatusAsync(
            connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false)
            ?? await StatusFromProjectionAsync(
                connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        var committedSequence = Math.Max(
            current?.CommittedSequence ?? 0,
            sessionEvent.Sequence);
        var committedHash = committedSequence == sessionEvent.Sequence
            ? sessionEvent.Hash
            : current?.CommittedHeadHash;
        await WriteDeliveryStatusAsync(
            connection,
            transaction,
            new SessionProjectionDeliveryStatus(
                sessionEvent.SessionId,
                committedSequence,
                committedHash,
                current?.AppliedSequence ?? 0,
                current?.AppliedHeadHash,
                timeProvider.GetUtcNow(),
                exception.GetType().FullName,
                BoundDetail(exception.Message)),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<SessionProjectionDeliveryStatus?> GetDeliveryStatusAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDeliveryStatusAsync(
            connection, null, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionProjectionDeliveryStatus>> ListLaggingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, committed_sequence, committed_head_hash,
                   applied_sequence, applied_head_hash, updated_at,
                   last_failure_type, last_failure_detail
            FROM session_projection_delivery
            WHERE applied_sequence < committed_sequence
               OR applied_head_hash IS NOT committed_head_hash
            ORDER BY updated_at, session_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<SessionProjectionDeliveryStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapDeliveryStatus(reader));
        return result;
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
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS session_projections(
                session_id TEXT PRIMARY KEY NOT NULL,
                applied_sequence INTEGER NOT NULL,
                head_hash TEXT NOT NULL,
                state_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_projection_delivery(
                session_id TEXT PRIMARY KEY NOT NULL,
                committed_sequence INTEGER NOT NULL,
                committed_head_hash TEXT NULL,
                applied_sequence INTEGER NOT NULL,
                applied_head_hash TEXT NULL,
                updated_at TEXT NOT NULL,
                last_failure_type TEXT NULL,
                last_failure_detail TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_session_projection_lag
                ON session_projection_delivery(applied_sequence, committed_sequence, updated_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<SessionProjectionSnapshot?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, applied_sequence, head_hash, state_json FROM session_projections WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionProjectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_projections(session_id, applied_sequence, head_hash, state_json)
            VALUES($sessionId, $sequence, $headHash, $state)
            ON CONFLICT(session_id) DO UPDATE SET
                applied_sequence = excluded.applied_sequence,
                head_hash = excluded.head_hash,
                state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$sessionId", snapshot.SessionId.ToString());
        command.Parameters.AddWithValue("$sequence", snapshot.AppliedSequence);
        command.Parameters.AddWithValue("$headHash", snapshot.HeadHash);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(snapshot.State, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SessionProjectionSnapshot Map(SqliteDataReader reader) =>
        new(
            SessionId.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetString(2),
            JsonSerializer.Deserialize<SessionCurrentState>(reader.GetString(3), SerializerOptions)
                ?? throw new InvalidDataException("Session projection state is empty."));

    private async Task MarkAppliedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        var current = await ReadDeliveryStatusAsync(
            connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        var projection = await ReadAsync(
            connection, transaction, sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        var appliedSequence = projection?.AppliedSequence ?? sessionEvent.Sequence;
        var appliedHash = projection?.HeadHash ?? sessionEvent.Hash;
        var committedSequence = Math.Max(current?.CommittedSequence ?? 0, appliedSequence);
        var committedHash = current is not null && current.CommittedSequence > appliedSequence
            ? current.CommittedHeadHash
            : appliedHash;
        await WriteDeliveryStatusAsync(
            connection,
            transaction,
            new SessionProjectionDeliveryStatus(
                sessionEvent.SessionId,
                committedSequence,
                committedHash,
                appliedSequence,
                appliedHash,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SessionProjectionDeliveryStatus?> StatusFromProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var projection = await ReadAsync(
            connection, transaction, sessionId, cancellationToken).ConfigureAwait(false);
        return projection is null
            ? null
            : new SessionProjectionDeliveryStatus(
                sessionId,
                projection.AppliedSequence,
                projection.HeadHash,
                projection.AppliedSequence,
                projection.HeadHash,
                DateTimeOffset.MinValue);
    }

    private static async Task<SessionProjectionDeliveryStatus?> ReadDeliveryStatusAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, committed_sequence, committed_head_hash,
                   applied_sequence, applied_head_hash, updated_at,
                   last_failure_type, last_failure_detail
            FROM session_projection_delivery WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapDeliveryStatus(reader)
            : null;
    }

    private static async Task WriteDeliveryStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionProjectionDeliveryStatus status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_projection_delivery(
                session_id, committed_sequence, committed_head_hash,
                applied_sequence, applied_head_hash, updated_at,
                last_failure_type, last_failure_detail)
            VALUES($sessionId, $committedSequence, $committedHash,
                $appliedSequence, $appliedHash, $updatedAt, $failureType, $failureDetail)
            ON CONFLICT(session_id) DO UPDATE SET
                committed_sequence = excluded.committed_sequence,
                committed_head_hash = excluded.committed_head_hash,
                applied_sequence = excluded.applied_sequence,
                applied_head_hash = excluded.applied_head_hash,
                updated_at = excluded.updated_at,
                last_failure_type = excluded.last_failure_type,
                last_failure_detail = excluded.last_failure_detail;
            """;
        command.Parameters.AddWithValue("$sessionId", status.SessionId.ToString());
        command.Parameters.AddWithValue("$committedSequence", status.CommittedSequence);
        command.Parameters.AddWithValue("$committedHash", (object?)status.CommittedHeadHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$appliedSequence", status.AppliedSequence);
        command.Parameters.AddWithValue("$appliedHash", (object?)status.AppliedHeadHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", status.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$failureType", (object?)status.LastFailureType ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureDetail", (object?)status.LastFailureDetail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SessionProjectionDeliveryStatus MapDeliveryStatus(
        SqliteDataReader reader) => new(
            SessionId.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private static string BoundDetail(string detail) =>
        detail.Length <= 2048 ? detail : detail[..2048];
}
