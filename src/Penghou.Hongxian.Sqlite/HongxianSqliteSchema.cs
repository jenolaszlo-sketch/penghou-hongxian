using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>Raised when a database component is newer than this package supports.</summary>
public sealed class HongxianSqliteSchemaCompatibilityException : Exception
{
    public HongxianSqliteSchemaCompatibilityException(
        string component,
        int detectedVersion,
        int supportedVersion)
        : base(
            $"Hongxian SQLite component '{component}' uses schema version " +
            $"{detectedVersion}, but this package supports at most {supportedVersion}.")
    {
        Component = component;
        DetectedVersion = detectedVersion;
        SupportedVersion = supportedVersion;
    }

    public string Component { get; }

    public int DetectedVersion { get; }

    public int SupportedVersion { get; }
}

internal static class HongxianSqliteSchema
{
    public const string CatalogComponent = "session-catalog";
    public const string ProjectionComponent = "session-projection";
    public const string OperationComponent = "cross-store-operation";

    public static async Task EnsureAsync(
        SqliteConnection connection,
        string component,
        int supportedVersion,
        Func<SqliteConnection, SqliteTransaction, int, CancellationToken, Task> migrate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        if (supportedVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(supportedVersion));
        ArgumentNullException.ThrowIfNull(migrate);

        using var transaction = connection.BeginTransaction(deferred: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS hongxian_schema_versions(
                component TEXT PRIMARY KEY NOT NULL,
                version INTEGER NOT NULL CHECK(version >= 1)
            );
            """,
            cancellationToken).ConfigureAwait(false);

        var detectedVersion = await ReadVersionAsync(
            connection, transaction, component, cancellationToken).ConfigureAwait(false);
        if (detectedVersion > supportedVersion)
            throw new HongxianSqliteSchemaCompatibilityException(
                component, detectedVersion, supportedVersion);
        if (detectedVersion < supportedVersion)
        {
            await migrate(
                connection, transaction, detectedVersion, cancellationToken)
                .ConfigureAwait(false);
            await using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = """
                INSERT INTO hongxian_schema_versions(component, version)
                VALUES($component, $version)
                ON CONFLICT(component) DO UPDATE SET version = excluded.version;
                """;
            version.Parameters.AddWithValue("$component", component);
            version.Parameters.AddWithValue("$version", supportedVersion);
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        return false;
    }

    public static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(
                connection, transaction, table, column, cancellationToken)
            .ConfigureAwait(false))
            return;
        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE \"{table.Replace("\"", "\"\"")}\" ADD COLUMN " +
            $"\"{column.Replace("\"", "\"\"")}\" {declaration};",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string component,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT version FROM hongxian_schema_versions WHERE component = $component;";
        command.Parameters.AddWithValue("$component", component);
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is null ? 0 : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }
}
