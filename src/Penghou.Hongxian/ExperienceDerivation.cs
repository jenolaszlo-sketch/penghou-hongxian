using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Hongxian;

/// <summary>Bounded limits for derived summaries and embeddings.</summary>
public static class ExperienceDerivationLimits
{
    public const int GeneratorProviderCharacters = 128;
    public const int GeneratorModelCharacters = 200;
    public const int GeneratorVersionCharacters = 128;
    public const int SummaryCharacters = 8_192;
    public const int EmbeddingDimensions = 2_048;
    public const int DerivationStableKeyCharacters = 200;
    public const int DisclosureScopeCharacters = 200;
}

/// <summary>
/// Identity of the host-supplied generator behind a derivation. Hongxian never
/// invokes models itself; the generator name, model, and version let a later
/// audit tell which provider produced derived data and whether it changed.
/// </summary>
public sealed record ExperienceGeneratorIdentity(string Provider, string Model, string Version)
{
    public string Provider { get; } = ValidateIdentifier(
        Provider,
        ExperienceDerivationLimits.GeneratorProviderCharacters,
        nameof(Provider));

    public string Model { get; } = ValidateText(
        Model,
        ExperienceDerivationLimits.GeneratorModelCharacters,
        nameof(Model));

    public string Version { get; } = ValidateText(
        Version,
        ExperienceDerivationLimits.GeneratorVersionCharacters,
        nameof(Version));

    private static string ValidateIdentifier(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty generator provider is required.", parameterName);
        if (value.Length > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Generator providers cannot exceed {maximum} characters.");
        if (!IsPortableIdentifier(value))
            throw new ArgumentException(
                "Generator providers must use lowercase portable identifier characters.", parameterName);
        return value;
    }

    private static string ValidateText(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty generator value is required.", parameterName);
        if (value.Length > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Generator values cannot exceed {maximum} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Generator values cannot contain control characters.", parameterName);
        return value;
    }

    private static bool IsPortableIdentifier(string value)
    {
        static bool IsLowerAsciiAlphaNumeric(char character) =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9';

        if (!IsLowerAsciiAlphaNumeric(value[0])) return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsLowerAsciiAlphaNumeric(character) &&
                character is not ('.' or '-' or '_' or ':' or '/'))
                return false;
        }

        return true;
    }
}

/// <summary>Summarization input: the source record, a character budget, and policy versions.</summary>
public sealed record ExperienceSummaryRequest(
    ExperienceEntity Record,
    int MaximumCharacters,
    string PolicyVersion,
    string StableKey = "default")
{
    public ExperienceEntity Record { get; } =
        Record ?? throw new ArgumentNullException(nameof(Record));

    public int MaximumCharacters { get; } = MaximumCharacters is < 1 or > ExperienceDerivationLimits.SummaryCharacters
        ? throw new ArgumentOutOfRangeException(
            nameof(MaximumCharacters),
            $"Maximum characters must be between 1 and {ExperienceDerivationLimits.SummaryCharacters}.")
        : MaximumCharacters;

    public string PolicyVersion { get; } = ValidatePolicyVersion(PolicyVersion);

    public string StableKey { get; } = ValidateStableKey(StableKey);

    internal static string ValidatePolicyVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty policy version is required.", nameof(value));
        if (value.Length > SessionContractLimits.PolicyVersionCharacters)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (value.Any(char.IsControl))
            throw new ArgumentException("Policy versions cannot contain control characters.", nameof(value));
        return value;
    }

    internal static string ValidateStableKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty stable key is required.", nameof(value));
        if (value.Length > ExperienceDerivationLimits.DerivationStableKeyCharacters)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (value.Any(char.IsControl))
            throw new ArgumentException("Stable keys cannot contain control characters.", nameof(value));
        return value;
    }
}

/// <summary>Untrusted output of a host-supplied summarizer; the orchestrator validates it.</summary>
public sealed record ExperienceSummaryResult(string Text, ExperienceGeneratorIdentity Generator)
{
    public string Text { get; } = Text ?? throw new ArgumentNullException(nameof(Text));

    public ExperienceGeneratorIdentity Generator { get; } =
        Generator ?? throw new ArgumentNullException(nameof(Generator));
}

/// <summary>Embedding input: the source record and the policy version in force.</summary>
public sealed record ExperienceEmbeddingRequest(
    ExperienceEntity Record,
    string PolicyVersion,
    string StableKey = "default")
{
    public ExperienceEntity Record { get; } =
        Record ?? throw new ArgumentNullException(nameof(Record));

    public string PolicyVersion { get; } = ExperienceSummaryRequest.ValidatePolicyVersion(PolicyVersion);

    public string StableKey { get; } = ExperienceSummaryRequest.ValidateStableKey(StableKey);
}

/// <summary>Untrusted output of a host-supplied embedder; the orchestrator validates it.</summary>
public sealed record ExperienceEmbeddingResult(
    IReadOnlyList<float> Vector,
    ExperienceGeneratorIdentity Generator)
{
    public IReadOnlyList<float> Vector { get; } =
        Vector ?? throw new ArgumentNullException(nameof(Vector));

    public ExperienceGeneratorIdentity Generator { get; } =
        Generator ?? throw new ArgumentNullException(nameof(Generator));
}

/// <summary>Host-supplied summarizer. Hongxian takes no model SDK dependency.</summary>
public interface IExperienceSummaryGenerator
{
    ExperienceGeneratorIdentity Identity { get; }

    Task<ExperienceSummaryResult> SummarizeAsync(
        ExperienceSummaryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-supplied embedder. Hongxian takes no model SDK dependency.</summary>
public interface IExperienceEmbeddingGenerator
{
    ExperienceGeneratorIdentity Identity { get; }

    Task<ExperienceEmbeddingResult> EmbedAsync(
        ExperienceEmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A rebuildable summary derived from one experience entity. The summary is
/// disposable: deleting it and re-deriving from the same evidence and
/// generator reproduces the same record. Summaries never become evidence and
/// never weaken the source disclosure classification.
/// </summary>
public sealed record ExperienceSummary
{
    public ExperienceSummary(
        ExperienceDerivationId id,
        ExperienceEntityId entityId,
        ExperienceProjectionId projectionId,
        string text,
        ExperienceGeneratorIdentity generator,
        string policyVersion,
        DateTimeOffset createdAt,
        IReadOnlyList<ExperienceEvidenceReference> sourceEvidence,
        SessionPayloadSensitivity sensitivity,
        string? disclosureScope = null,
        ExperienceDerivationId? supersedes = null)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(id));
        Id = id;
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > ExperienceDerivationLimits.SummaryCharacters)
            throw new ArgumentOutOfRangeException(nameof(text));
        if (text.Any(static c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException(
                "Summary text cannot contain control characters other than line breaks and tabs.",
                nameof(text));
        Text = text;
        Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        PolicyVersion = ExperienceSummaryRequest.ValidatePolicyVersion(policyVersion);
        if (createdAt == default)
            throw new ArgumentException("A creation time is required.", nameof(createdAt));
        CreatedAt = createdAt;
        SourceEvidence = ExperienceEntity.SnapshotEvidence(sourceEvidence);
        if (!Enum.IsDefined(sensitivity))
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
        Sensitivity = sensitivity;
        if (disclosureScope is not null)
        {
            if (string.IsNullOrWhiteSpace(disclosureScope) ||
                disclosureScope.Length > ExperienceDerivationLimits.DisclosureScopeCharacters ||
                disclosureScope.Any(char.IsControl))
                throw new ArgumentException("The disclosure scope is not bounded.", nameof(disclosureScope));
        }

        DisclosureScope = disclosureScope;
        if (supersedes?.Value == Guid.Empty)
            throw new ArgumentException("A superseded derivation ID cannot be empty.", nameof(supersedes));
        Supersedes = supersedes;
        ContentDigest = ExperienceDerivationDigest.ForText(text);
    }

    public ExperienceDerivationId Id { get; }

    public ExperienceEntityId EntityId { get; }

    public ExperienceProjectionId ProjectionId { get; }

    public string Text { get; }

    public ExperienceGeneratorIdentity Generator { get; }

    public string PolicyVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<ExperienceEvidenceReference> SourceEvidence { get; }

    public string ContentDigest { get; }

    public SessionPayloadSensitivity Sensitivity { get; }

    public string? DisclosureScope { get; }

    public ExperienceDerivationId? Supersedes { get; }
}

/// <summary>
/// A rebuildable embedding derived from one experience entity. Vectors must be
/// finite; similarity over them is a retrieval signal, never factual
/// confidence. Like summaries, embeddings are disposable derived data.
/// </summary>
public sealed record ExperienceEmbedding
{
    public ExperienceEmbedding(
        ExperienceDerivationId id,
        ExperienceEntityId entityId,
        ExperienceProjectionId projectionId,
        IReadOnlyList<float> vector,
        ExperienceGeneratorIdentity generator,
        string policyVersion,
        DateTimeOffset createdAt,
        IReadOnlyList<ExperienceEvidenceReference> sourceEvidence,
        SessionPayloadSensitivity sensitivity,
        string? disclosureScope = null,
        ExperienceDerivationId? supersedes = null)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(id));
        Id = id;
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count is < 1 or > ExperienceDerivationLimits.EmbeddingDimensions)
            throw new ArgumentOutOfRangeException(
                nameof(vector),
                $"Embeddings must have between 1 and {ExperienceDerivationLimits.EmbeddingDimensions} dimensions.");
        if (vector.Any(component => float.IsNaN(component) || float.IsInfinity(component)))
            throw new ArgumentException("Embedding vectors must be finite.", nameof(vector));
        Vector = vector.ToArray();
        Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        PolicyVersion = ExperienceSummaryRequest.ValidatePolicyVersion(policyVersion);
        if (createdAt == default)
            throw new ArgumentException("A creation time is required.", nameof(createdAt));
        CreatedAt = createdAt;
        SourceEvidence = ExperienceEntity.SnapshotEvidence(sourceEvidence);
        if (!Enum.IsDefined(sensitivity))
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
        Sensitivity = sensitivity;
        if (disclosureScope is not null)
        {
            if (string.IsNullOrWhiteSpace(disclosureScope) ||
                disclosureScope.Length > ExperienceDerivationLimits.DisclosureScopeCharacters ||
                disclosureScope.Any(char.IsControl))
                throw new ArgumentException("The disclosure scope is not bounded.", nameof(disclosureScope));
        }

        DisclosureScope = disclosureScope;
        if (supersedes?.Value == Guid.Empty)
            throw new ArgumentException("A superseded derivation ID cannot be empty.", nameof(supersedes));
        Supersedes = supersedes;
        ContentDigest = ExperienceDerivationDigest.ForVector(Vector);
    }

    public ExperienceDerivationId Id { get; }

    public ExperienceEntityId EntityId { get; }

    public ExperienceProjectionId ProjectionId { get; }

    public IReadOnlyList<float> Vector { get; }

    public int Dimensions => Vector.Count;

    public ExperienceGeneratorIdentity Generator { get; }

    public string PolicyVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<ExperienceEvidenceReference> SourceEvidence { get; }

    public string ContentDigest { get; }

    public SessionPayloadSensitivity Sensitivity { get; }

    public string? DisclosureScope { get; }

    public ExperienceDerivationId? Supersedes { get; }
}

/// <summary>Versioned content digests for derived data.</summary>
public static class ExperienceDerivationDigest
{
    public const string ContractVersion = "sha256:penghou-hongxian-derivation:v1";

    public static string ForText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, ContractVersion);
        AppendUtf8(hash, "summary");
        AppendUtf8(hash, text);
        return $"{ContractVersion}:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    public static string ForVector(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, ContractVersion);
        AppendUtf8(hash, "embedding");
        AppendInt32(hash, vector.Count);
        Span<byte> component = stackalloc byte[sizeof(float)];
        foreach (var value in vector)
        {
            BinaryPrimitives.WriteSingleLittleEndian(component, value);
            hash.AppendData(component);
        }

        return $"{ContractVersion}:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// Builds validated derivation records from untrusted host-generator output.
/// Derivation assigns rebuild-stable identities, snapshots source evidence,
/// and persists the caller-declared disclosure classification: hosts must not
/// weaken the source classification, and derived content never upgrades it.
/// Re-derivation creates a new record linked through <c>Supersedes</c>; older
/// records and the receipts referencing their evidence stay verifiable.
/// </summary>
public static class ExperienceDerivation
{
    public static async Task<ExperienceSummary> DeriveSummaryAsync(
        ExperienceSummaryRequest request,
        IExperienceSummaryGenerator generator,
        SessionPayloadSensitivity sensitivity,
        string? disclosureScope = null,
        ExperienceDerivationId? supersedes = null,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(generator);
        if (!Enum.IsDefined(sensitivity))
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
        var result = await generator.SummarizeAsync(request, cancellationToken);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Generator.Equals(generator.Identity))
            throw new InvalidOperationException(
                "The generator returned a different generator identity than the requested one.");
        if (string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException("The generator returned no summary text.");
        if (result.Text.Length > request.MaximumCharacters)
            throw new InvalidOperationException(
                $"The generator returned {result.Text.Length} characters against a budget of {request.MaximumCharacters}.");
        var entity = request.Record;
        return new ExperienceSummary(
            ExperienceDerivationId.CreateDeterministic(
                entity.Provenance.Projection.ProjectionId,
                "summary",
                $"{entity.Id}|{generator.Identity.Provider}/{generator.Identity.Model}/" +
                $"{generator.Identity.Version}|{request.PolicyVersion}|{request.StableKey}"),
            entity.Id,
            entity.Provenance.Projection.ProjectionId,
            result.Text,
            generator.Identity,
            request.PolicyVersion,
            createdAt ?? DateTimeOffset.UtcNow,
            entity.Evidence,
            sensitivity,
            disclosureScope,
            supersedes);
    }

    public static async Task<ExperienceEmbedding> DeriveEmbeddingAsync(
        ExperienceEmbeddingRequest request,
        IExperienceEmbeddingGenerator generator,
        SessionPayloadSensitivity sensitivity,
        string? disclosureScope = null,
        ExperienceDerivationId? supersedes = null,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(generator);
        if (!Enum.IsDefined(sensitivity))
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
        var result = await generator.EmbedAsync(request, cancellationToken);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Generator.Equals(generator.Identity))
            throw new InvalidOperationException(
                "The generator returned a different generator identity than the requested one.");
        if (result.Vector.Count == 0)
            throw new InvalidOperationException("The generator returned an empty vector.");
        if (result.Vector.Count > ExperienceDerivationLimits.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"The generator returned {result.Vector.Count} dimensions against a limit of " +
                $"{ExperienceDerivationLimits.EmbeddingDimensions}.");
        if (result.Vector.Any(static component => float.IsNaN(component) || float.IsInfinity(component)))
            throw new InvalidOperationException("The generator returned a non-finite vector.");
        var entity = request.Record;
        return new ExperienceEmbedding(
            ExperienceDerivationId.CreateDeterministic(
                entity.Provenance.Projection.ProjectionId,
                "embedding",
                $"{entity.Id}|{generator.Identity.Provider}/{generator.Identity.Model}/" +
                $"{generator.Identity.Version}|{request.PolicyVersion}|{request.StableKey}"),
            entity.Id,
            entity.Provenance.Projection.ProjectionId,
            result.Vector,
            generator.Identity,
            request.PolicyVersion,
            createdAt ?? DateTimeOffset.UtcNow,
            entity.Evidence,
            sensitivity,
            disclosureScope,
            supersedes);
    }
}
