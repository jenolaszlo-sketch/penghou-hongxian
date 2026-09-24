using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Hongxian;

/// <summary>Bounded limits for portable evidence-bound recall.</summary>
public static class ExperienceRecallLimits
{
    public const int LexicalQueryCharacters = 2_048;
    public const int ScopeKindCount = 64;
    public const int RetrievalPolicyNameCharacters = 200;
    public const int RetrievalPolicyVersionCharacters = 128;
    public const int RecallItemLimit = 1_000;
    public const int RecallByteLimit = 1_048_576;
    public const int RecallFingerprintCharacters = 256;
}

/// <summary>Freshness demanded by a bounded recall request.</summary>
public enum ExperienceRecallFreshnessRequirement
{
    Any = 0,
    Current = 1
}

/// <summary>Completion of one portable bounded recall execution.</summary>
public enum ExperienceBoundedRecallCompletion
{
    Completed = 0,
    Unsupported = 1
}

/// <summary>
/// Versioned retrieval-policy identity. The policy names the ranking and
/// selection behavior a consumer asked for; Hongxian never executes
/// provider-specific ranking through it.
/// </summary>
public sealed record ExperienceRetrievalPolicy(string Name, string Version)
{
    public string Name { get; } = ValidateIdentifier(
        Name,
        ExperienceRecallLimits.RetrievalPolicyNameCharacters,
        nameof(Name));

    public string Version { get; } = ValidateVersion(Version);

    private static string ValidateIdentifier(string value, int maximum, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty policy name is required.", parameterName);
        if (value.Length > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Policy names cannot exceed {maximum} characters.");
        if (!IsPortableIdentifier(value))
            throw new ArgumentException(
                "Policy names must use lowercase portable identifier characters.", parameterName);
        return value;
    }

    private static string ValidateVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty policy version is required.", nameof(value));
        if (value.Length > ExperienceRecallLimits.RetrievalPolicyVersionCharacters)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Policy versions cannot exceed {ExperienceRecallLimits.RetrievalPolicyVersionCharacters} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Policy versions cannot contain control characters.", nameof(value));
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

/// <summary>
/// Portable score semantics. Ranks are provider-specific orderings and are
/// never comparable across providers, nor are they confidence that recalled
/// evidence is correct.
/// </summary>
public static class ExperienceRecallScoreSemantics
{
    public const string ProviderRank = "provider-rank";
}

/// <summary>
/// One bounded, evidence-bound recall request over a single experience
/// projection. Lexical search is the ranked pass; traversal and vector
/// retrieval remain later extensions.
/// </summary>
public sealed record ExperienceBoundedRecallRequest
{
    public ExperienceBoundedRecallRequest(
        ExperienceProjectionId projectionId,
        ExperienceRetrievalPolicy policy,
        string lexicalQuery,
        IReadOnlyList<string>? entityKinds = null,
        ExperienceProjectionPosition? asOf = null,
        int maximumItems = 20,
        int maximumBytes = 65_536,
        int? maximumTokens = null,
        int minimumEvidenceReferences = 1,
        ExperienceRecallFreshnessRequirement freshnessRequirement = ExperienceRecallFreshnessRequirement.Current)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        LexicalQuery = ValidateQuery(lexicalQuery);
        EntityKinds = SnapshotKinds(entityKinds);
        AsOf = asOf;
        if (maximumItems is < 1 or > ExperienceRecallLimits.RecallItemLimit)
            throw new ArgumentOutOfRangeException(
                nameof(maximumItems),
                $"Maximum items must be between 1 and {ExperienceRecallLimits.RecallItemLimit}.");
        MaximumItems = maximumItems;
        if (maximumBytes is < 1 or > ExperienceRecallLimits.RecallByteLimit)
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                $"Maximum bytes must be between 1 and {ExperienceRecallLimits.RecallByteLimit}.");
        MaximumBytes = maximumBytes;
        if (maximumTokens is < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTokens), "Maximum tokens must be positive.");
        MaximumTokens = maximumTokens;
        if (minimumEvidenceReferences is < 1 or > ExperienceContractLimits.EvidenceReferenceCount)
            throw new ArgumentOutOfRangeException(
                nameof(minimumEvidenceReferences),
                $"Minimum evidence references must be between 1 and {ExperienceContractLimits.EvidenceReferenceCount}.");
        MinimumEvidenceReferences = minimumEvidenceReferences;
        if (!Enum.IsDefined(freshnessRequirement))
            throw new ArgumentOutOfRangeException(nameof(freshnessRequirement));
        FreshnessRequirement = freshnessRequirement;
    }

    public ExperienceProjectionId ProjectionId { get; }

    public ExperienceRetrievalPolicy Policy { get; }

    public string LexicalQuery { get; }

    /// <summary>Distinct entity kinds in ordinal order; empty means unfiltered.</summary>
    public IReadOnlyList<string> EntityKinds { get; }

    public ExperienceProjectionPosition? AsOf { get; }

    public int MaximumItems { get; }

    public int MaximumBytes { get; }

    public int? MaximumTokens { get; }

    public int MinimumEvidenceReferences { get; }

    public ExperienceRecallFreshnessRequirement FreshnessRequirement { get; }

    private static string ValidateQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty lexical query is required.", nameof(value));
        if (value.Length > ExperienceRecallLimits.LexicalQueryCharacters)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Lexical queries cannot exceed {ExperienceRecallLimits.LexicalQueryCharacters} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Lexical queries cannot contain control characters.", nameof(value));
        return value;
    }

    private static IReadOnlyList<string> SnapshotKinds(IReadOnlyList<string>? kinds)
    {
        if (kinds is null || kinds.Count == 0)
            return Array.AsReadOnly(Array.Empty<string>());
        if (kinds.Count > ExperienceRecallLimits.ScopeKindCount)
            throw new ArgumentOutOfRangeException(
                nameof(kinds),
                $"Scope cannot exceed {ExperienceRecallLimits.ScopeKindCount} entity kinds.");
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Entity kinds cannot be empty.", nameof(kinds));
            if (kind.Length > ExperienceContractLimits.KindCharacters)
                throw new ArgumentOutOfRangeException(nameof(kinds));
            if (kind.Any(char.IsControl))
                throw new ArgumentException("Entity kinds cannot contain control characters.", nameof(kinds));
            distinct.Add(kind);
        }

        return new ReadOnlyCollection<string>(distinct.ToArray());
    }
}

/// <summary>One recalled entity with its portable rank and token estimate.</summary>
public sealed record ExperienceRecalledEntity(
    ExperienceEntity Record,
    int Rank,
    string ScoreSemantics,
    long EstimatedTokens)
{
    public ExperienceEntity Record { get; } =
        Record ?? throw new ArgumentNullException(nameof(Record));

    public int Rank { get; } = Rank < 1
        ? throw new ArgumentOutOfRangeException(nameof(Rank), "Ranks start at 1.")
        : Rank;

    public string ScoreSemantics { get; } =
        string.Equals(ScoreSemantics, ExperienceRecallScoreSemantics.ProviderRank, StringComparison.Ordinal)
            ? ScoreSemantics
            : throw new ArgumentException(
                $"Score semantics must be '{ExperienceRecallScoreSemantics.ProviderRank}'.",
                nameof(ScoreSemantics));

    public long EstimatedTokens { get; } = EstimatedTokens < 0
        ? throw new ArgumentOutOfRangeException(nameof(EstimatedTokens))
        : EstimatedTokens;
}

/// <summary>
/// Portable result of one bounded recall execution. Ranks preserve provider
/// ordering with record-id ordinal tie-breaking; they are never confidence.
/// </summary>
public sealed record ExperienceBoundedRecallResult
{
    public ExperienceBoundedRecallResult(
        string queryFingerprint,
        ExperienceProjectionId projectionId,
        string providerName,
        ExperienceRetrievalPolicy policy,
        ExperienceProjectionCheckpoint? coveredCheckpoint,
        IReadOnlyList<ExperienceRecalledEntity> items,
        long estimatedTokensTotal,
        ExperienceProviderFreshness freshness,
        bool isTruncated,
        ExperienceBoundedRecallCompletion completion = ExperienceBoundedRecallCompletion.Completed,
        ExperienceProviderCapability unsupportedCapabilities = ExperienceProviderCapability.None,
        string? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        if (queryFingerprint.Length > ExperienceRecallLimits.RecallFingerprintCharacters)
            throw new ArgumentOutOfRangeException(nameof(queryFingerprint));
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        QueryFingerprint = queryFingerprint;
        ProjectionId = projectionId;
        ProviderName = providerName;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        CoveredCheckpoint = coveredCheckpoint;
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToArray();
        if (snapshot.Any(item => item is null))
            throw new ArgumentException("Recalled items cannot be null.", nameof(items));
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index].Rank != index + 1)
                throw new ArgumentException(
                    "Recalled ranks must be sequential starting at 1.",
                    nameof(items));
        }

        Items = Array.AsReadOnly(snapshot);
        if (estimatedTokensTotal != snapshot.Sum(item => item.EstimatedTokens))
            throw new ArgumentException("The token total must equal the sum of item estimates.", nameof(estimatedTokensTotal));
        EstimatedTokensTotal = estimatedTokensTotal;
        if (!Enum.IsDefined(freshness))
            throw new ArgumentOutOfRangeException(nameof(freshness));
        Freshness = freshness;
        IsTruncated = isTruncated;
        if (!Enum.IsDefined(completion))
            throw new ArgumentOutOfRangeException(nameof(completion));
        Completion = completion;
        if (!ExperienceProviderCapabilities.IsValidSet(unsupportedCapabilities))
            throw new ArgumentOutOfRangeException(nameof(unsupportedCapabilities));
        if (completion == ExperienceBoundedRecallCompletion.Completed &&
            unsupportedCapabilities != ExperienceProviderCapability.None)
            throw new ArgumentException(
                "Completed results cannot report unsupported capabilities.",
                nameof(unsupportedCapabilities));
        UnsupportedCapabilities = unsupportedCapabilities;
        var incomplete = completion == ExperienceBoundedRecallCompletion.Unsupported ||
            isTruncated || freshness != ExperienceProviderFreshness.Current;
        if (incomplete && string.IsNullOrWhiteSpace(diagnostic))
            throw new ArgumentException(
                "Incomplete or non-current results require a diagnostic.",
                nameof(diagnostic));
        if (diagnostic?.Length > 2_048 || diagnostic?.Any(char.IsControl) == true)
            throw new ArgumentException(
                "Diagnostics must be bounded and free of control characters.",
                nameof(diagnostic));
        Diagnostic = diagnostic;
    }

    public string QueryFingerprint { get; }

    public ExperienceProjectionId ProjectionId { get; }

    public string ProviderName { get; }

    public ExperienceRetrievalPolicy Policy { get; }

    public ExperienceProjectionCheckpoint? CoveredCheckpoint { get; }

    public IReadOnlyList<ExperienceRecalledEntity> Items { get; }

    public long EstimatedTokensTotal { get; }

    public ExperienceProviderFreshness Freshness { get; }

    public bool IsTruncated { get; }

    public ExperienceBoundedRecallCompletion Completion { get; }

    public ExperienceProviderCapability UnsupportedCapabilities { get; }

    public string? Diagnostic { get; }

    /// <summary>Source evidence identities across all recalled items, in item order.</summary>
    public IReadOnlyList<ExperienceEvidenceReferenceId> SelectedEvidence =>
        Items.SelectMany(item => item.Record.Evidence)
            .Select(reference => reference.Id)
            .Distinct()
            .ToArray();
}

/// <summary>
/// Canonical fingerprint of a bounded recall request. The digest covers an
/// explicit field sequence with length-prefixed UTF-8, so it is independent of
/// CLR serialization details: two equivalent requests fingerprint identically
/// regardless of construction order.
/// </summary>
public static class ExperienceRecallFingerprint
{
    public const string ContractVersion = "sha256:penghou-hongxian-recall:v1";

    public static string Compute(ExperienceBoundedRecallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, ContractVersion);
        AppendUtf8(hash, request.ProjectionId.ToString());
        AppendUtf8(hash, request.Policy.Name);
        AppendUtf8(hash, request.Policy.Version);
        AppendUtf8(hash, request.LexicalQuery);
        AppendInt32(hash, request.EntityKinds.Count);
        foreach (var kind in request.EntityKinds)
            AppendUtf8(hash, kind);
        AppendUtf8(hash, request.AsOf?.Digest ?? string.Empty);
        AppendInt32(hash, request.MaximumItems);
        AppendInt64(hash, request.MaximumBytes);
        AppendInt32(hash, request.MaximumTokens.HasValue ? 1 : 0);
        if (request.MaximumTokens.HasValue)
            AppendInt32(hash, request.MaximumTokens.Value);
        AppendInt32(hash, request.MinimumEvidenceReferences);
        AppendInt32(hash, (int)request.FreshnessRequirement);
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

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

/// <summary>One recalled record inside a recall receipt.</summary>
public sealed record ExperienceRecallReceiptItem
{
    public ExperienceRecallReceiptItem(
        ExperienceEntityId entityId,
        ExperienceDerivationId derivationId,
        IReadOnlyList<ExperienceEvidenceReferenceId> evidenceIds,
        long estimatedTokens)
    {
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        if (derivationId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(derivationId));
        DerivationId = derivationId;
        EvidenceIds = SnapshotEvidence(evidenceIds);
        if (estimatedTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        EstimatedTokens = estimatedTokens;
    }

    public ExperienceEntityId EntityId { get; }

    public ExperienceDerivationId DerivationId { get; }

    public IReadOnlyList<ExperienceEvidenceReferenceId> EvidenceIds { get; }

    public long EstimatedTokens { get; }

    private static IReadOnlyList<ExperienceEvidenceReferenceId> SnapshotEvidence(
        IReadOnlyList<ExperienceEvidenceReferenceId> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
            throw new ArgumentException("At least one evidence identity is required.", nameof(evidence));
        if (evidence.Count > ExperienceContractLimits.EvidenceReferenceCount)
            throw new ArgumentOutOfRangeException(nameof(evidence));
        if (evidence.Any(item => item.Value == Guid.Empty))
            throw new ArgumentException("Evidence identities cannot be empty.", nameof(evidence));
        var ordered = evidence.Select(item => item.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (ordered.Distinct().Count() != ordered.Length)
            throw new ArgumentException("Evidence identities cannot repeat.", nameof(evidence));
        return Array.AsReadOnly(ordered.Select(ExperienceEvidenceReferenceId.Parse).ToArray());
    }
}

/// <summary>
/// Compact, ledger-fittable record of what one bounded recall supplied to a
/// consequential consumer. The receipt explains the evidence behind a decision;
/// the decision itself remains the consumer's responsibility.
/// </summary>
public sealed record ExperienceRecallReceipt
{
    public ExperienceRecallReceipt(
        string queryFingerprint,
        ExperienceProjectionId projectionId,
        ExperienceRetrievalPolicy policy,
        string? checkpointPositionDigest,
        long? checkpointVersion,
        IReadOnlyList<ExperienceRecallReceiptItem> items,
        string suppliedContextDigest,
        long estimatedTokensTotal,
        bool sourceTruncated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        if (!queryFingerprint.StartsWith(ExperienceRecallFingerprint.ContractVersion, StringComparison.Ordinal) ||
            queryFingerprint.Length > ExperienceRecallLimits.RecallFingerprintCharacters)
            throw new ArgumentException("The query fingerprint is not a recall fingerprint.", nameof(queryFingerprint));
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        QueryFingerprint = queryFingerprint;
        ProjectionId = projectionId;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if ((checkpointPositionDigest is null) != (checkpointVersion is null))
            throw new ArgumentException("Checkpoint digest and version must be supplied together.");
        if (checkpointPositionDigest is not null)
        {
            if (string.IsNullOrWhiteSpace(checkpointPositionDigest) ||
                checkpointPositionDigest.Length > SessionContractLimits.DigestCharacters)
                throw new ArgumentException("The checkpoint digest is not bounded.", nameof(checkpointPositionDigest));
            if (checkpointVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(checkpointVersion));
        }

        CheckpointPositionDigest = checkpointPositionDigest;
        CheckpointVersion = checkpointVersion;
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToArray();
        if (snapshot.Length > ExperienceRecallLimits.RecallItemLimit)
            throw new ArgumentOutOfRangeException(nameof(items));
        if (snapshot.Any(item => item is null))
            throw new ArgumentException("Receipt items cannot be null.", nameof(items));
        Items = Array.AsReadOnly(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedContextDigest);
        if (suppliedContextDigest.Length > SessionContractLimits.DigestCharacters ||
            suppliedContextDigest.Any(char.IsControl))
            throw new ArgumentException("The supplied-context digest is not bounded.", nameof(suppliedContextDigest));
        SuppliedContextDigest = suppliedContextDigest;
        if (estimatedTokensTotal != snapshot.Sum(item => item.EstimatedTokens))
            throw new ArgumentException("The token total must equal the sum of item estimates.", nameof(estimatedTokensTotal));
        EstimatedTokensTotal = estimatedTokensTotal;
        SourceTruncated = sourceTruncated;
    }

    public string QueryFingerprint { get; }

    public ExperienceProjectionId ProjectionId { get; }

    public ExperienceRetrievalPolicy Policy { get; }

    public string? CheckpointPositionDigest { get; }

    public long? CheckpointVersion { get; }

    public IReadOnlyList<ExperienceRecallReceiptItem> Items { get; }

    public string SuppliedContextDigest { get; }

    public long EstimatedTokensTotal { get; }

    public bool SourceTruncated { get; }

    /// <summary>
    /// Builds a receipt from a completed recall result. Unsupported recalls
    /// cannot produce receipts because there is no supplied context to explain.
    /// An empty completed recall produces an empty receipt, recording that the
    /// decision used no recalled evidence.
    /// </summary>
    public static ExperienceRecallReceipt Create(
        ExperienceBoundedRecallResult result,
        string suppliedContextDigest)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Completion != ExperienceBoundedRecallCompletion.Completed)
            throw new ArgumentException("Only a completed recall can produce a receipt.", nameof(result));
        return new ExperienceRecallReceipt(
            result.QueryFingerprint,
            result.ProjectionId,
            result.Policy,
            result.CoveredCheckpoint?.Position.Digest,
            result.CoveredCheckpoint?.Version,
            result.Items.Select(item => new ExperienceRecallReceiptItem(
                item.Record.Id,
                item.Record.Provenance.DerivationId,
                item.Record.Evidence.Select(reference => reference.Id).ToArray(),
                item.EstimatedTokens)).ToArray(),
            suppliedContextDigest,
            result.EstimatedTokensTotal,
            result.IsTruncated);
    }
}

/// <summary>Payload schema identity for persisted recall receipts.</summary>
public static class ExperienceRecallReceiptSchema
{
    public const string Name = "penghou.recall-receipt";

    public const int Version = 1;

    public static SessionPayloadSchema Schema { get; } = new(Name, Version);
}

/// <summary>Helpers that persist and read recall receipts as session evidence.</summary>
public static class ExperienceRecallReceipts
{
    private static readonly JsonSerializerOptions PayloadOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hashes supplied context bytes into the digest form receipts carry.
    /// Consumers hash exactly the bytes they supplied so a later audit can
    /// reproduce the digest.
    /// </summary>
    public static string HashSuppliedContext(string context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HashSuppliedContext(Encoding.UTF8.GetBytes(context));
    }

    /// <summary>Hashes supplied context bytes into the digest form receipts carry.</summary>
    public static string HashSuppliedContext(byte[] context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(context));
    }

    /// <summary>
    /// Builds the session event request for a recall receipt. The default
    /// idempotency key derives from the query fingerprint, so retrying the same
    /// recall append is safe while a different recall is never conflated.
    /// </summary>
    public static SessionEventRequest CreateRequest(
        SessionId sessionId,
        SessionParticipantAttribution participant,
        ExperienceRecallReceipt receipt,
        DateTimeOffset? occurredAt = null,
        Guid? causationId = null,
        Guid? correlationId = null,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(receipt);
        SessionContractValidation.ValidateSessionId(sessionId, nameof(sessionId));
        var payload = JsonSerializer.SerializeToElement(receipt, PayloadOptions);
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > SessionContractLimits.PayloadUtf8Bytes)
            throw new ArgumentOutOfRangeException(
                nameof(receipt),
                "The serialized receipt exceeds the session event payload bound; " +
                "recall with smaller budgets for receiptable use.");
        return new SessionEventRequest(
            sessionId,
            participant,
            SessionEventTypes.ExperienceRecallRecorded,
            occurredAt ?? DateTimeOffset.UtcNow,
            CausationId: causationId,
            CorrelationId: correlationId,
            IdempotencyKey: idempotencyKey ?? $"recall:{receipt.QueryFingerprint}",
            PayloadSchema: ExperienceRecallReceiptSchema.Schema,
            Payload: payload,
            Evidence: new SessionEvidenceDescriptor(
                SessionEvidenceNatures.Receipt,
                SessionEvidenceBases.Derived,
                SessionEvidenceDispositions.Unassessed));
    }

    /// <summary>Appends a recall receipt to a session ledger.</summary>
    public static Task<SessionEvent> AppendRecallReceiptAsync(
        this ISessionEventStore store,
        SessionId sessionId,
        SessionParticipantAttribution participant,
        ExperienceRecallReceipt receipt,
        DateTimeOffset? occurredAt = null,
        Guid? causationId = null,
        Guid? correlationId = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.AppendAsync(
            CreateRequest(
                sessionId,
                participant,
                receipt,
                occurredAt,
                causationId,
                correlationId,
                idempotencyKey),
            cancellationToken);
    }

    /// <summary>Reads a persisted recall receipt without requiring its CLR type at append time.</summary>
    public static ExperienceRecallReceipt ReadRecallReceipt(
        this SessionEvent sessionEvent,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var schema = sessionEvent.PayloadSchema;
        if (schema is null ||
            !string.Equals(schema.Name, ExperienceRecallReceiptSchema.Name, StringComparison.Ordinal) ||
            schema.Version != ExperienceRecallReceiptSchema.Version)
            throw new UnsupportedSessionPayloadSchemaException(
                schema ?? new SessionPayloadSchema("unknown", 1),
                ExperienceRecallReceiptSchema.Version,
                schema?.Version ?? 1);
        return sessionEvent.ReadPayload<ExperienceRecallReceipt>(serializerOptions ?? PayloadOptions);
    }
}

/// <summary>
/// Portable executor for bounded recall requests over any recall reader.
/// Budget accounting is the executor's authority: providers rank, the executor
/// bounds. Budgets are hard caps; items that do not fit are skipped and the
/// result reports truncation explicitly.
/// </summary>
public static class ExperienceBoundedRecall
{
    /// <summary>
    /// Portable token-estimate model: ceiling of UTF-8 bytes divided by four.
    /// Estimates are reproducible upper bounds for budgeting, never a claim
    /// about any model's real tokenization.
    /// </summary>
    public const string TokenEstimateModel = "ceiling(utf8-bytes/4)";

    private static readonly JsonSerializerOptions BudgetOptions =
        new(JsonSerializerDefaults.Web);

    public static long EstimateTokens(long utf8Bytes) =>
        utf8Bytes < 0
            ? throw new ArgumentOutOfRangeException(nameof(utf8Bytes))
            : (utf8Bytes + 3) / 4;

    public static async Task<ExperienceBoundedRecallResult> ExecuteAsync(
        IExperienceRecallReader reader,
        ExperienceBoundedRecallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = ExperienceRecallFingerprint.Compute(request);
        if (!reader.Capabilities.Supports(ExperienceProviderCapability.LexicalSearch))
            return Unsupported(
                fingerprint, request, reader.Capabilities.ProviderName,
                ExperienceProviderCapability.LexicalSearch,
                "The provider does not support lexical search.");
        if (request.AsOf is not null && !reader.Capabilities.Supports(ExperienceProviderCapability.AsOf))
            return Unsupported(
                fingerprint, request, reader.Capabilities.ProviderName,
                ExperienceProviderCapability.AsOf,
                "The provider does not retain historical projection snapshots; " +
                "the AsOf recall was not approximated with current data.");

        var providerResult = await reader.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(
                request.ProjectionId,
                request.LexicalQuery,
                request.MaximumItems,
                ExperienceRecallLimits.RecallByteLimit,
                request.AsOf),
            cancellationToken);

        if (request.FreshnessRequirement == ExperienceRecallFreshnessRequirement.Current &&
            providerResult.Freshness != ExperienceProviderFreshness.Current)
            return Unsupported(
                fingerprint, request, reader.Capabilities.ProviderName,
                ExperienceProviderCapability.None,
                $"Freshness requirement 'current' was not satisfied; " +
                $"the provider reported '{providerResult.Freshness}'.");

        var candidates = providerResult.Items
            .Where(item => request.EntityKinds.Count == 0 || request.EntityKinds.Contains(item.Kind))
            .Where(item => item.Evidence.Count >= request.MinimumEvidenceReferences)
            .ToArray();

        var selected = new List<ExperienceRecalledEntity>();
        var bytes = 0L;
        var tokens = 0L;
        var budgetTruncated = false;
        foreach (var candidate in candidates)
        {
            if (selected.Count == request.MaximumItems)
            {
                budgetTruncated = true;
                break;
            }

            var itemBytes = JsonSerializer.SerializeToUtf8Bytes(candidate, BudgetOptions).Length;
            var itemTokens = EstimateTokens(itemBytes);
            if (bytes + itemBytes > request.MaximumBytes ||
                (request.MaximumTokens.HasValue && tokens + itemTokens > request.MaximumTokens.Value))
            {
                budgetTruncated = true;
                continue;
            }

            bytes += itemBytes;
            tokens += itemTokens;
            selected.Add(new ExperienceRecalledEntity(
                candidate,
                selected.Count + 1,
                ExperienceRecallScoreSemantics.ProviderRank,
                itemTokens));
        }

        var truncated = providerResult.IsTruncated || budgetTruncated;
        var filtered = request.EntityKinds.Count > 0 || request.MinimumEvidenceReferences > 1;
        string? diagnostic = truncated
            ? providerResult.IsTruncated && budgetTruncated
                ? "Recall reached the provider bound and the portable item, byte, or token budget."
                : providerResult.IsTruncated && filtered
                    ? "Recall reached the provider result bound before kind/evidence filtering; " +
                      "matching records may exist beyond the bound."
                    : providerResult.IsTruncated
                        ? "Recall reached the provider result bound."
                        : "Recall reached the portable item, byte, or token budget."
            : null;
        if (!truncated && providerResult.Freshness != ExperienceProviderFreshness.Current)
            diagnostic = $"The provider reported '{providerResult.Freshness}' freshness.";

        return new ExperienceBoundedRecallResult(
            fingerprint,
            request.ProjectionId,
            reader.Capabilities.ProviderName,
            request.Policy,
            providerResult.Checkpoint,
            selected,
            tokens,
            providerResult.Freshness,
            truncated,
            diagnostic: diagnostic);
    }

    private static ExperienceBoundedRecallResult Unsupported(
        string fingerprint,
        ExperienceBoundedRecallRequest request,
        string providerName,
        ExperienceProviderCapability missing,
        string diagnostic) =>
        new(
            fingerprint,
            request.ProjectionId,
            providerName,
            request.Policy,
            null,
            [],
            0,
            ExperienceProviderFreshness.Unknown,
            false,
            ExperienceBoundedRecallCompletion.Unsupported,
            missing,
            diagnostic);
}
