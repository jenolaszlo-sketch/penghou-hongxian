namespace Penghou.Hongxian;

/// <summary>
/// Portable write surface for derived summaries and embeddings. Records are
/// scoped by projection: a store serves one provider, and descriptor-bearing
/// operations validate the provider match while record writes rely on
/// projection identity.
/// </summary>
public interface IExperienceDerivationWriter
{
    ExperienceProviderCapabilities Capabilities { get; }
    Task<ExperienceModelWriteResult> UpsertSummaryAsync(
        ExperienceSummary summary,
        CancellationToken cancellationToken = default);

    Task<ExperienceModelWriteResult> UpsertEmbeddingAsync(
        ExperienceEmbedding embedding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes derived summaries and embeddings for one projection. Call
    /// alongside model-projection deletion for a full disposable reset; Siming
    /// evidence and session ledgers are never touched.
    /// </summary>
    Task DeleteDerivationsAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>Portable read surface for derivations and vector/hybrid recall.</summary>
public interface IExperienceDerivationReader
{
    ExperienceProviderCapabilities Capabilities { get; }

    Task<ExperienceRecallResult<ExperienceSummary>> GetSummaryAsync(
        ExperienceDerivationLookupRequest request,
        CancellationToken cancellationToken = default);

    Task<ExperienceRecallResult<ExperienceEmbedding>> GetEmbeddingAsync(
        ExperienceDerivationLookupRequest request,
        CancellationToken cancellationToken = default);

    Task<ExperienceRecallResult<ExperienceVectorMatch>> SearchVectorAsync(
        ExperienceVectorSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ExperienceRecallResult<ExperienceHybridMatch>> SearchHybridAsync(
        ExperienceHybridSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the derivation identities attached to one entity. The listing is
    /// how consumers discover which summaries and embeddings to fetch after
    /// recall returns their entity.
    /// </summary>
    Task<ExperienceRecallResult<ExperienceEntityDerivations>> ListDerivationsAsync(
        ExperienceEntityLookupRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Lookup of one derivation record by its stable identity.</summary>
public sealed record ExperienceDerivationLookupRequest
{
    public ExperienceDerivationLookupRequest(
        ExperienceProjectionId projectionId,
        ExperienceDerivationId derivationId,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        if (derivationId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(derivationId));
        ProjectionId = projectionId;
        DerivationId = derivationId;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }

    public ExperienceDerivationId DerivationId { get; }

    public ExperienceProjectionPosition? AsOf { get; }
}

/// <summary>
/// Cosine-similarity recall over stored embeddings. Cosine is mathematically
/// defined and therefore portable across providers; ranks remain primary and
/// record-id order breaks ties. Embeddings with different dimensions than the
/// query are skipped so one store can hold several generator outputs.
/// </summary>
public sealed record ExperienceVectorSearchRequest
{
    public ExperienceVectorSearchRequest(
        ExperienceProjectionId projectionId,
        IReadOnlyList<float> queryVector,
        int maximumItems = 20,
        double? minimumSimilarity = null,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        QueryVector = SnapshotVector(queryVector);
        if (maximumItems is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        MaximumItems = maximumItems;
        if (minimumSimilarity.HasValue)
        {
            if (double.IsNaN(minimumSimilarity.Value) || double.IsInfinity(minimumSimilarity.Value))
                throw new ArgumentException("The minimum similarity must be finite.", nameof(minimumSimilarity));
            if (minimumSimilarity.Value is < -1.0 or > 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(minimumSimilarity), "Cosine similarity must be between -1 and 1.");
        }

        MinimumSimilarity = minimumSimilarity;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }

    public IReadOnlyList<float> QueryVector { get; }

    public int MaximumItems { get; }

    public double? MinimumSimilarity { get; }

    public ExperienceProjectionPosition? AsOf { get; }

    internal static IReadOnlyList<float> SnapshotVector(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count is < 1 or > ExperienceDerivationLimits.EmbeddingDimensions)
            throw new ArgumentOutOfRangeException(
                nameof(vector),
                $"Query vectors must have between 1 and {ExperienceDerivationLimits.EmbeddingDimensions} dimensions.");
        if (vector.Any(static component => float.IsNaN(component) || float.IsInfinity(component)))
            throw new ArgumentException("Query vectors must be finite.", nameof(vector));
        return vector.ToArray();
    }
}

/// <summary>
/// Hybrid recall fusing one lexical and one vector pass with reciprocal-rank
/// fusion. Both sides contribute at most <c>MaximumItems</c> candidates; the
/// fused ranking keeps at most <c>MaximumItems</c> matches.
/// </summary>
public sealed record ExperienceHybridSearchRequest
{
    public ExperienceHybridSearchRequest(
        ExperienceProjectionId projectionId,
        string lexicalQuery,
        IReadOnlyList<float> queryVector,
        int maximumItems = 20,
        ExperienceProjectionPosition? asOf = null)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        if (string.IsNullOrWhiteSpace(lexicalQuery))
            throw new ArgumentException("A non-empty lexical query is required.", nameof(lexicalQuery));
        if (lexicalQuery.Length > ExperienceRecallLimits.LexicalQueryCharacters)
            throw new ArgumentOutOfRangeException(nameof(lexicalQuery));
        if (lexicalQuery.Any(char.IsControl))
            throw new ArgumentException("Lexical queries cannot contain control characters.", nameof(lexicalQuery));
        LexicalQuery = lexicalQuery;
        QueryVector = ExperienceVectorSearchRequest.SnapshotVector(queryVector);
        if (maximumItems is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        MaximumItems = maximumItems;
        AsOf = asOf;
    }

    public ExperienceProjectionId ProjectionId { get; }

    public string LexicalQuery { get; }

    public IReadOnlyList<float> QueryVector { get; }

    public int MaximumItems { get; }

    public ExperienceProjectionPosition? AsOf { get; }
}

/// <summary>Best embedding match for one entity. Similarity is cosine, never confidence.</summary>
public sealed record ExperienceVectorMatch
{
    public ExperienceVectorMatch(
        ExperienceEntityId entityId,
        double similarity,
        ExperienceDerivationId derivationId)
    {
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        if (double.IsNaN(similarity) || similarity is < -1.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(similarity), "Cosine similarity must be a finite value between -1 and 1.");
        Similarity = similarity;
        if (derivationId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(derivationId));
        DerivationId = derivationId;
    }

    public ExperienceEntityId EntityId { get; }

    public double Similarity { get; }

    public ExperienceDerivationId DerivationId { get; }
}

/// <summary>One reciprocal-rank-fusion match. Ranks are 1-based side positions.</summary>
public sealed record ExperienceHybridMatch
{
    public ExperienceHybridMatch(
        ExperienceEntityId entityId,
        double score,
        int? lexicalRank,
        int? vectorRank,
        ExperienceDerivationId? vectorDerivationId = null)
    {
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        if (double.IsNaN(score) || double.IsInfinity(score) || score < 0.0)
            throw new ArgumentOutOfRangeException(nameof(score), "Fusion scores must be finite and non-negative.");
        Score = score;
        if (lexicalRank is < 1)
            throw new ArgumentOutOfRangeException(nameof(lexicalRank));
        if (vectorRank is < 1)
            throw new ArgumentOutOfRangeException(nameof(vectorRank));
        if (lexicalRank is null && vectorRank is null)
            throw new ArgumentException("A match must rank on at least one side.");
        if (vectorRank.HasValue != vectorDerivationId.HasValue)
            throw new ArgumentException(
                "A vector rank requires the matched embedding derivation, and vice versa.",
                nameof(vectorDerivationId));
        if (vectorDerivationId?.Value == Guid.Empty)
            throw new ArgumentException("The vector derivation ID cannot be empty.", nameof(vectorDerivationId));
        LexicalRank = lexicalRank;
        VectorRank = vectorRank;
        VectorDerivationId = vectorDerivationId;
    }

    public ExperienceEntityId EntityId { get; }

    public double Score { get; }

    public int? LexicalRank { get; }

    public int? VectorRank { get; }

    /// <summary>The embedding derivation behind the vector rank, when ranked on that side.</summary>
    public ExperienceDerivationId? VectorDerivationId { get; }
}

/// <summary>Derivation identities attached to one entity, in stable order.</summary>
public sealed record ExperienceEntityDerivations
{
    public ExperienceEntityDerivations(
        ExperienceProjectionId projectionId,
        ExperienceEntityId entityId,
        IReadOnlyList<ExperienceDerivationId> summaryIds,
        IReadOnlyList<ExperienceDerivationId> embeddingIds)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ProjectionId = projectionId;
        if (entityId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty entity ID is required.", nameof(entityId));
        EntityId = entityId;
        SummaryIds = SnapshotIds(summaryIds, nameof(summaryIds));
        EmbeddingIds = SnapshotIds(embeddingIds, nameof(embeddingIds));
    }

    public ExperienceProjectionId ProjectionId { get; }

    public ExperienceEntityId EntityId { get; }

    public IReadOnlyList<ExperienceDerivationId> SummaryIds { get; }

    public IReadOnlyList<ExperienceDerivationId> EmbeddingIds { get; }

    private static IReadOnlyList<ExperienceDerivationId> SnapshotIds(
        IReadOnlyList<ExperienceDerivationId> ids,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(ids, parameterName);
        if (ids.Any(static item => item.Value == Guid.Empty))
            throw new ArgumentException("Derivation identities cannot be empty.", parameterName);
        var ordered = ids.Select(static item => item.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Distinct().Count() != ordered.Length)
            throw new ArgumentException("Derivation identities cannot repeat.", parameterName);
        return Array.AsReadOnly(ordered.Select(ExperienceDerivationId.Parse).ToArray());
    }
}

/// <summary>Portable cosine similarity. Zero-norm inputs score 0.0 by definition.</summary>
public static class ExperienceVectorSimilarity
{
    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count == 0 || right.Count == 0)
            throw new ArgumentException("Similarity inputs cannot be empty.");
        if (left.Count != right.Count)
            throw new ArgumentException("Similarity inputs must share their dimensions.");
        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            leftNorm += (double)left[index] * left[index];
            rightNorm += (double)right[index] * right[index];
        }

        if (leftNorm == 0.0 || rightNorm == 0.0)
            return 0.0;
        var similarity = dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        return Math.Clamp(similarity, -1.0, 1.0);
    }
}

/// <summary>
/// Portable reciprocal-rank fusion. Every provider fuses identically:
/// score 1/(k+rank) per side with k=60, ordered by score then record-id.
/// The vector side passes full matches so fused results keep the derivation
/// behind each vector rank; callers pass best-per-entity matches.
/// </summary>
public static class ExperienceHybridRankFusion
{
    public const int RrfK = 60;

    public static IReadOnlyList<ExperienceHybridMatch> Fuse(
        IReadOnlyList<ExperienceEntityId> lexicalOrder,
        IReadOnlyList<ExperienceVectorMatch> vectorOrder)
    {
        ArgumentNullException.ThrowIfNull(lexicalOrder);
        ArgumentNullException.ThrowIfNull(vectorOrder);
        if (vectorOrder.Any(static match => match is null))
            throw new ArgumentException("Vector matches cannot be null.", nameof(vectorOrder));
        var lexicalRanks = Rank(lexicalOrder);
        var vectorRanks = Rank(vectorOrder.Select(match => match.EntityId).ToArray());
        var derivations = vectorOrder
            .GroupBy(match => match.EntityId)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(match => match.Similarity)
                .ThenBy(match => match.DerivationId.ToString(), StringComparer.Ordinal)
                .First().DerivationId);
        return lexicalRanks.Keys.Union(vectorRanks.Keys)
            .Select(id => new ExperienceHybridMatch(
                id,
                Score(lexicalRanks, id) + Score(vectorRanks, id),
                lexicalRanks.TryGetValue(id, out var lexical) ? lexical : null,
                vectorRanks.TryGetValue(id, out var vector) ? vector : null,
                vectorRanks.ContainsKey(id) ? derivations[id] : null))
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<ExperienceEntityId, int> Rank(IReadOnlyList<ExperienceEntityId> order)
    {
        var ranks = new Dictionary<ExperienceEntityId, int>();
        for (var index = 0; index < order.Count; index++)
            ranks.TryAdd(order[index], index + 1);
        return ranks;
    }

    private static double Score(Dictionary<ExperienceEntityId, int> ranks, ExperienceEntityId id) =>
        ranks.TryGetValue(id, out var rank) ? 1.0 / (RrfK + rank) : 0.0;
}
