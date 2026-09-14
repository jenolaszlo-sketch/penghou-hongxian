using System.Collections.ObjectModel;

namespace Penghou.Hongxian;

[Flags]
public enum ExperienceProviderCapability
{
    None = 0,
    ExactLookup = 1,
    BoundedTraversal = 2,
    LexicalSearch = 4,
    VectorSearch = 8,
    HybridRanking = 16,
    AsOf = 32,
    CheckpointConsistency = 64,
    RemoteOperation = 128
}

public sealed record ExperienceProviderCapabilities
{
    public ExperienceProviderCapabilities(string providerName, ExperienceProviderCapability supported)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("A non-empty provider name is required.", nameof(providerName));
        if (providerName.Length > SessionContractLimits.ProjectionProviderCharacters ||
            !IsPortableIdentifier(providerName))
            throw new ArgumentException(
                "Provider names must use lowercase portable identifier characters.",
                nameof(providerName));
        if (!IsValidSet(supported))
            throw new ArgumentOutOfRangeException(nameof(supported));
        ProviderName = providerName;
        Supported = supported;
    }

    public string ProviderName { get; }
    public ExperienceProviderCapability Supported { get; }

    public bool Supports(ExperienceProviderCapability capability) =>
        capability != ExperienceProviderCapability.None &&
        IsValidSet(capability) &&
        (Supported & capability) == capability;

    internal static bool IsValidSet(ExperienceProviderCapability capabilities)
    {
        const ExperienceProviderCapability all =
            ExperienceProviderCapability.ExactLookup |
            ExperienceProviderCapability.BoundedTraversal |
            ExperienceProviderCapability.LexicalSearch |
            ExperienceProviderCapability.VectorSearch |
            ExperienceProviderCapability.HybridRanking |
            ExperienceProviderCapability.AsOf |
            ExperienceProviderCapability.CheckpointConsistency |
            ExperienceProviderCapability.RemoteOperation;
        return (capabilities & ~all) == 0;
    }

    private static bool IsPortableIdentifier(string value)
    {
        static bool IsLowerAsciiAlphaNumeric(char character) =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9';

        if (!IsLowerAsciiAlphaNumeric(value[0])) return false;
        return value.Skip(1).All(character =>
            IsLowerAsciiAlphaNumeric(character) ||
            character is '.' or '-' or '_' or ':' or '/');
    }
}

public enum ExperienceRecallCompletion
{
    Completed,
    Unsupported
}

public enum ExperienceProviderFreshness
{
    Current,
    Stale,
    Unknown
}

/// <summary>
/// Explicit result metadata prevents a provider from silently changing query
/// meaning when an optional capability is unavailable.
/// </summary>
public sealed record ExperienceRecallResult<T>
{
    public ExperienceRecallResult(
        IReadOnlyList<T> items,
        ExperienceRecallCompletion completion = ExperienceRecallCompletion.Completed,
        ExperienceProviderFreshness freshness = ExperienceProviderFreshness.Current,
        bool isDegraded = false,
        bool isTruncated = false,
        ExperienceProviderCapability unsupportedCapabilities = ExperienceProviderCapability.None,
        ExperienceProjectionCheckpoint? checkpoint = null,
        string? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!Enum.IsDefined(completion)) throw new ArgumentOutOfRangeException(nameof(completion));
        if (!Enum.IsDefined(freshness)) throw new ArgumentOutOfRangeException(nameof(freshness));
        var isUnsupported = completion == ExperienceRecallCompletion.Unsupported;
        if (!ExperienceProviderCapabilities.IsValidSet(unsupportedCapabilities))
            throw new ArgumentOutOfRangeException(nameof(unsupportedCapabilities));
        if (isUnsupported != (unsupportedCapabilities != ExperienceProviderCapability.None))
            throw new ArgumentException(
                "Unsupported completion and unsupported capabilities must be supplied together.",
                nameof(unsupportedCapabilities));
        if (isUnsupported && items.Count != 0)
            throw new ArgumentException(
                "An unsupported operation cannot return fallback items.",
                nameof(items));
        if ((isUnsupported || isDegraded || isTruncated ||
             freshness != ExperienceProviderFreshness.Current) &&
            string.IsNullOrWhiteSpace(diagnostic))
            throw new ArgumentException(
                "Non-current or incomplete results require a diagnostic.",
                nameof(diagnostic));
        if (diagnostic?.Length > 2_048 || diagnostic?.Any(char.IsControl) == true)
            throw new ArgumentException(
                "Diagnostics must be bounded and free of control characters.",
                nameof(diagnostic));
        Items = new ReadOnlyCollection<T>(items.ToArray());
        Completion = completion;
        Freshness = freshness;
        IsDegraded = isDegraded;
        IsTruncated = isTruncated;
        UnsupportedCapabilities = unsupportedCapabilities;
        Checkpoint = checkpoint;
        Diagnostic = diagnostic;
    }

    public IReadOnlyList<T> Items { get; }
    public ExperienceRecallCompletion Completion { get; }
    public ExperienceProviderFreshness Freshness { get; }
    public bool IsDegraded { get; }
    public bool IsTruncated { get; }
    public ExperienceProviderCapability UnsupportedCapabilities { get; }
    public ExperienceProjectionCheckpoint? Checkpoint { get; }
    public string? Diagnostic { get; }

    public static ExperienceRecallResult<T> Success(
        IEnumerable<T> items,
        ExperienceProjectionCheckpoint? checkpoint = null) =>
        new(items.ToArray(), checkpoint: checkpoint);

    public static ExperienceRecallResult<T> Unsupported(
        ExperienceProviderCapability capability,
        string diagnostic) =>
        new(Array.Empty<T>(), ExperienceRecallCompletion.Unsupported,
            unsupportedCapabilities: capability, diagnostic: diagnostic);

    public static ExperienceRecallResult<T> Degraded(
        IEnumerable<T> items,
        string diagnostic,
        ExperienceProviderFreshness freshness = ExperienceProviderFreshness.Unknown,
        bool isTruncated = false,
        ExperienceProjectionCheckpoint? checkpoint = null) =>
        new(
            items.ToArray(),
            freshness: freshness,
            isDegraded: true,
            isTruncated: isTruncated,
            checkpoint: checkpoint,
            diagnostic: diagnostic);

    public static ExperienceRecallResult<T> Stale(
        IEnumerable<T> items,
        string diagnostic,
        ExperienceProjectionCheckpoint? checkpoint = null) =>
        new(
            items.ToArray(),
            freshness: ExperienceProviderFreshness.Stale,
            checkpoint: checkpoint,
            diagnostic: diagnostic);

    public static ExperienceRecallResult<T> Truncated(
        IEnumerable<T> items,
        string diagnostic,
        ExperienceProjectionCheckpoint? checkpoint = null) =>
        new(
            items.ToArray(),
            isTruncated: true,
            checkpoint: checkpoint,
            diagnostic: diagnostic);
}

public enum ExperienceModelWriteOutcome
{
    Applied,
    AlreadyPresent,
    Conflict
}

public sealed record ExperienceModelWriteResult(
    ExperienceModelWriteOutcome Outcome,
    string? Diagnostic = null);

public sealed record ExperienceEntityLookupRequest
{
    public ExperienceEntityLookupRequest(
        ExperienceProjectionId projectionId,
        ExperienceEntityId entityId,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        ProjectionId = projectionId;
        EntityId = entityId;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }
    public ExperienceEntityId EntityId { get; }
    public ExperienceProjectionPosition? AsOf { get; }
}

public sealed record ExperienceRelationTraversalRequest
{
    public ExperienceRelationTraversalRequest(
        ExperienceProjectionId projectionId,
        ExperienceEntityId start,
        string? relationKind = null,
        int maximumDepth = 1,
        int maximumItems = 100,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty) throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        if (start.Value == Guid.Empty) throw new ArgumentException("A non-empty start entity ID is required.", nameof(start));
        if (relationKind is not null && (relationKind.Length > ExperienceContractLimits.KindCharacters || relationKind.Any(char.IsControl)))
            throw new ArgumentException("Relation kinds must be bounded and free of control characters.", nameof(relationKind));
        if (maximumDepth is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumItems is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        ProjectionId = projectionId;
        Start = start;
        RelationKind = relationKind;
        MaximumDepth = maximumDepth;
        MaximumItems = maximumItems;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }
    public ExperienceEntityId Start { get; }
    public string? RelationKind { get; }
    public int MaximumDepth { get; }
    public int MaximumItems { get; }
    public ExperienceProjectionPosition? AsOf { get; }
}

public sealed record ExperienceLexicalSearchRequest
{
    public ExperienceLexicalSearchRequest(
        ExperienceProjectionId projectionId,
        string query,
        int maximumItems = 20,
        int maximumBytes = 65_536,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty) throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A non-empty search query is required.", nameof(query));
        if (query.Length > 2_048 || query.Any(char.IsControl)) throw new ArgumentException("Search queries must be bounded and free of control characters.", nameof(query));
        if (maximumItems is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (maximumBytes is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ProjectionId = projectionId;
        Query = query;
        MaximumItems = maximumItems;
        MaximumBytes = maximumBytes;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }
    public string Query { get; }
    public int MaximumItems { get; }
    public int MaximumBytes { get; }
    public ExperienceProjectionPosition? AsOf { get; }
}

/// <summary>Portable write surface for projected experience records.</summary>
public interface IExperienceProjectionModelWriter
{
    ExperienceProviderCapabilities Capabilities { get; }

    Task<ExperienceModelWriteResult> UpsertEntityAsync(
        ExperienceEntity entity,
        CancellationToken cancellationToken = default);

    Task<ExperienceModelWriteResult> UpsertRelationAsync(
        ExperienceRelation relation,
        CancellationToken cancellationToken = default);

    Task DeleteProjectionAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only portable recall surface. Query semantics are bounded by request types.</summary>
public interface IExperienceRecallReader
{
    ExperienceProviderCapabilities Capabilities { get; }

    Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
        ExperienceEntityLookupRequest request,
        CancellationToken cancellationToken = default);

    Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
        ExperienceRelationTraversalRequest request,
        CancellationToken cancellationToken = default);

    Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
        ExperienceLexicalSearchRequest request,
        CancellationToken cancellationToken = default);
}
