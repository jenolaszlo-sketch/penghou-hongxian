using System.Text.Json;

namespace Penghou.Hongxian;

/// <summary>Versions the Hongxian event envelope independently of its payload.</summary>
public static class SessionEventEnvelopeSchema
{
    public const int MinimumSupportedVersion = 1;

    public const int CurrentVersion = 2;
}

/// <summary>Application-owned identity and version of an event payload.</summary>
public sealed record SessionPayloadSchema(string Name, int Version)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Name.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(Name), "Payload schema names cannot exceed 200 characters.");
        if (Version < 1)
            throw new ArgumentOutOfRangeException(nameof(Version), "Payload schema versions start at 1.");
    }
}

/// <summary>Transforms one application payload schema version into its successor.</summary>
public interface ISessionPayloadUpcaster
{
    string SchemaName { get; }

    int SourceVersion { get; }

    int TargetVersion { get; }

    JsonElement Upcast(JsonElement payload);
}

/// <summary>A payload and the schema version it currently represents.</summary>
public sealed record UpcastedSessionPayload(
    SessionPayloadSchema Schema,
    JsonElement Payload);

/// <summary>
/// Resolves deterministic, application-supplied upcast chains without changing
/// immutable ledger entries.
/// </summary>
public sealed class SessionPayloadUpcasterRegistry
{
    private readonly IReadOnlyDictionary<(string Name, int Version), ISessionPayloadUpcaster> upcasters;

    public SessionPayloadUpcasterRegistry(IEnumerable<ISessionPayloadUpcaster>? upcasters = null)
    {
        var registered = new Dictionary<(string Name, int Version), ISessionPayloadUpcaster>();
        foreach (var upcaster in upcasters ?? [])
        {
            ArgumentNullException.ThrowIfNull(upcaster);
            var source = new SessionPayloadSchema(upcaster.SchemaName, upcaster.SourceVersion);
            source.Validate();
            if (upcaster.TargetVersion != upcaster.SourceVersion + 1)
                throw new ArgumentException(
                    "Payload upcasters must advance exactly one schema version.",
                    nameof(upcasters));
            if (!registered.TryAdd((source.Name, source.Version), upcaster))
                throw new ArgumentException(
                    $"An upcaster for '{source.Name}' version {source.Version} is already registered.",
                    nameof(upcasters));
        }
        this.upcasters = registered;
    }

    /// <summary>Upcasts to an explicit application schema version.</summary>
    public UpcastedSessionPayload Upcast(
        SessionPayloadSchema sourceSchema,
        JsonElement payload,
        int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(sourceSchema);
        sourceSchema.Validate();
        if (targetVersion < sourceSchema.Version)
            throw new ArgumentOutOfRangeException(
                nameof(targetVersion),
                "Immutable payload reads cannot downcast to an older schema version.");

        var current = payload.Clone();
        var version = sourceSchema.Version;
        while (version < targetVersion)
        {
            if (!upcasters.TryGetValue((sourceSchema.Name, version), out var upcaster))
                throw new UnsupportedSessionPayloadSchemaException(
                    sourceSchema,
                    targetVersion,
                    version);
            current = upcaster.Upcast(current).Clone();
            version = upcaster.TargetVersion;
        }
        return new UpcastedSessionPayload(
            new SessionPayloadSchema(sourceSchema.Name, version),
            current);
    }
}

/// <summary>Raised when a persisted event envelope is newer than this library.</summary>
public sealed class UnsupportedSessionEventSchemaException(int detectedVersion)
    : Exception(
        $"Session event envelope version {detectedVersion} is unsupported; " +
        $"this library supports versions {SessionEventEnvelopeSchema.MinimumSupportedVersion} " +
        $"through {SessionEventEnvelopeSchema.CurrentVersion}.")
{
    public int DetectedVersion { get; } = detectedVersion;

    public int SupportedVersion { get; } = SessionEventEnvelopeSchema.CurrentVersion;

    public int MinimumSupportedVersion { get; } =
        SessionEventEnvelopeSchema.MinimumSupportedVersion;
}

/// <summary>Raised when no complete application payload upcast path exists.</summary>
public sealed class UnsupportedSessionPayloadSchemaException(
    SessionPayloadSchema sourceSchema,
    int requestedVersion,
    int missingSourceVersion)
    : Exception(
        $"Payload schema '{sourceSchema.Name}' cannot be upcast from version " +
        $"{sourceSchema.Version} to {requestedVersion}; the version " +
        $"{missingSourceVersion} upcaster is not registered.")
{
    public SessionPayloadSchema SourceSchema { get; } = sourceSchema;

    public int RequestedVersion { get; } = requestedVersion;

    public int MissingSourceVersion { get; } = missingSourceVersion;
}
