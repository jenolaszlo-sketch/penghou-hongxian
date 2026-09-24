using System.Text.Json;

namespace Penghou.Hongxian;

/// <summary>Versions the Hongxian event envelope independently of its payload.</summary>
public static class SessionEventEnvelopeSchema
{
    public const int MinimumSupportedVersion = 1;

    public const int CurrentVersion = 3;
}

/// <summary>Well-known, extensible evidence-nature values.</summary>
public static class SessionEvidenceNatures
{
    public const string Unspecified = "unspecified";
    public const string Assertion = "assertion";
    public const string Observation = "observation";
    public const string Measurement = "measurement";
    public const string Decision = "decision";
    public const string Receipt = "receipt";
    public const string Diagnostic = "diagnostic";
}

/// <summary>Well-known, extensible capture-basis values.</summary>
public static class SessionEvidenceBases
{
    public const string Unspecified = "unspecified";
    public const string ParticipantClaim = "participant-claim";
    public const string AuthorityReceipt = "authority-receipt";
    public const string DirectObservation = "direct-observation";
    public const string Derived = "derived";
}

/// <summary>Well-known, extensible capture-time disposition values.</summary>
public static class SessionEvidenceDispositions
{
    public const string Unassessed = "unassessed";
    public const string Supported = "supported";
    public const string Contradicted = "contradicted";
    public const string Superseded = "superseded";
}

/// <summary>
/// Immutable capture-time evidence semantics. Values are portable identifier
/// tokens: lowercase ASCII letter/digit first, then lowercase ASCII
/// letters/digits or '.', '-', '_', ':', '/'.
/// </summary>
public sealed record SessionEvidenceDescriptor(string Nature, string Basis, string Disposition)
{
    public static SessionEvidenceDescriptor Unspecified { get; } = new(
        SessionEvidenceNatures.Unspecified,
        SessionEvidenceBases.Unspecified,
        SessionEvidenceDispositions.Unassessed);

    public void Validate()
    {
        ValidateValue(Nature, SessionContractLimits.EvidenceNatureCharacters, nameof(Nature));
        ValidateValue(Basis, SessionContractLimits.EvidenceBasisCharacters, nameof(Basis));
        ValidateValue(Disposition, SessionContractLimits.EvidenceDispositionCharacters, nameof(Disposition));
    }

    private static void ValidateValue(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty evidence value is required.", parameterName);
        if (value.Length > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Values cannot exceed {maximum} characters.");
        if (!IsToken(value))
            throw new ArgumentException(
                "Evidence values must be lowercase portable identifier tokens.", parameterName);
    }

    private static bool IsToken(string value)
    {
        static bool IsAlphaNumeric(char c) =>
            c is >= 'a' and <= 'z' or >= '0' and <= '9';

        if (!IsAlphaNumeric(value[0])) return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAlphaNumeric(character) && character is not ('.' or '-' or '_' or ':' or '/'))
                return false;
        }
        return true;
    }
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

/// <summary>Raised when a persisted event carries no payload schema at all.</summary>
public sealed class MissingSessionPayloadSchemaException(
    Guid eventId,
    SessionPayloadSchema targetSchema)
    : Exception(
        $"Session event '{eventId:D}' has no recorded payload schema; " +
        $"no upcast path to '{targetSchema.Name}' version {targetSchema.Version} can be resolved.")
{
    public Guid EventId { get; } = eventId;

    public SessionPayloadSchema TargetSchema { get; } = targetSchema;
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
