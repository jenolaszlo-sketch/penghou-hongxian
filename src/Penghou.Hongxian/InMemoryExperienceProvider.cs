namespace Penghou.Hongxian;

/// <summary>
/// Reference provider for contract tests. It uses only process memory and has
/// no transaction, filesystem, or provider-native query dependency.
/// </summary>
public sealed class InMemoryExperienceProvider : IExperienceProjectionModelWriter, IExperienceRecallReader
{
    private readonly object gate = new();
    private readonly Dictionary<(ExperienceProjectionId ProjectionId, ExperienceEntityId Id), ExperienceEntity> entities = [];
    private readonly Dictionary<(ExperienceProjectionId ProjectionId, ExperienceRelationId Id), ExperienceRelation> relations = [];

    public InMemoryExperienceProvider(string providerName = "memory")
    {
        Capabilities = new ExperienceProviderCapabilities(
            providerName,
            ExperienceProviderCapability.ExactLookup |
            ExperienceProviderCapability.BoundedTraversal |
            ExperienceProviderCapability.LexicalSearch);
    }

    public ExperienceProviderCapabilities Capabilities { get; }

    public Task<ExperienceModelWriteResult> UpsertEntityAsync(
        ExperienceEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                entity.Provenance.Projection.Provider,
                Capabilities.ProviderName,
                StringComparison.Ordinal))
            return Task.FromResult(new ExperienceModelWriteResult(
                ExperienceModelWriteOutcome.Conflict,
                "The entity projection is assigned to a different provider."));
        var key = (entity.Provenance.Projection.ProjectionId, entity.Id);
        lock (gate)
        {
            if (!entities.TryGetValue(key, out var existing))
            {
                entities.Add(key, entity);
                return Task.FromResult(new ExperienceModelWriteResult(ExperienceModelWriteOutcome.Applied));
            }

            return Task.FromResult(ExperienceModelSemanticEquality.Entity(existing, entity)
                ? new ExperienceModelWriteResult(ExperienceModelWriteOutcome.AlreadyPresent)
                : new ExperienceModelWriteResult(
                    ExperienceModelWriteOutcome.Conflict,
                    "The entity identity is already present with different content."));
        }
    }

    public Task<ExperienceModelWriteResult> UpsertRelationAsync(
        ExperienceRelation relation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                relation.Provenance.Projection.Provider,
                Capabilities.ProviderName,
                StringComparison.Ordinal))
            return Task.FromResult(new ExperienceModelWriteResult(
                ExperienceModelWriteOutcome.Conflict,
                "The relation projection is assigned to a different provider."));
        var key = (relation.Provenance.Projection.ProjectionId, relation.Id);
        lock (gate)
        {
            if (!relations.TryGetValue(key, out var existing))
            {
                relations.Add(key, relation);
                return Task.FromResult(new ExperienceModelWriteResult(ExperienceModelWriteOutcome.Applied));
            }

            return Task.FromResult(ExperienceModelSemanticEquality.Relation(existing, relation)
                ? new ExperienceModelWriteResult(ExperienceModelWriteOutcome.AlreadyPresent)
                : new ExperienceModelWriteResult(
                    ExperienceModelWriteOutcome.Conflict,
                    "The relation identity is already present with different content."));
        }
    }

    public Task DeleteProjectionAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                descriptor.Provider,
                Capabilities.ProviderName,
                StringComparison.Ordinal))
            throw new ArgumentException(
                "The projection is assigned to a different provider.",
                nameof(descriptor));
        lock (gate)
        {
            foreach (var key in entities.Keys.Where(key => key.ProjectionId == descriptor.ProjectionId).ToArray())
                entities.Remove(key);
            foreach (var key in relations.Keys.Where(key => key.ProjectionId == descriptor.ProjectionId).ToArray())
                relations.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
        ExperienceEntityLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceEntity>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The in-memory reference provider does not retain historical versions."));
        lock (gate)
        {
            var items = entities.TryGetValue((request.ProjectionId, request.EntityId), out var entity)
                ? new[] { entity }
                : Array.Empty<ExperienceEntity>();
            return Task.FromResult(ExperienceRecallResult<ExperienceEntity>.Success(items));
        }
    }

    public Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
        ExperienceRelationTraversalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceRelation>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The in-memory reference provider does not retain historical versions."));

        lock (gate)
        {
            var visited = new HashSet<ExperienceEntityId> { request.Start };
            var frontier = new[] { request.Start };
            var found = new List<ExperienceRelation>();
            var limitReached = false;
            for (var depth = 0; depth < request.MaximumDepth && frontier.Length > 0; depth++)
            {
                var next = new List<ExperienceEntityId>();
                foreach (var relation in relations.Values
                             .Where(item => item.Provenance.Projection.ProjectionId == request.ProjectionId)
                             .Where(item => request.RelationKind is null || item.Kind == request.RelationKind)
                             .Where(item => frontier.Contains(item.From))
                             .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
                {
                    if (found.Count == request.MaximumItems)
                    {
                        limitReached = true;
                        break;
                    }
                    found.Add(relation);
                    if (visited.Add(relation.To)) next.Add(relation.To);
                }
                frontier = next.ToArray();
            }

            return Task.FromResult(limitReached
                ? ExperienceRecallResult<ExperienceRelation>.Truncated(found, "Traversal reached its item bound.")
                : ExperienceRecallResult<ExperienceRelation>.Success(found));
        }
    }

    public Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
        ExperienceLexicalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceEntity>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The in-memory reference provider does not retain historical versions."));
        var terms = request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (gate)
        {
            var matches = entities.Values
                .Where(item => item.Provenance.Projection.ProjectionId == request.ProjectionId)
                .Select(item => (Entity: item, Text: SearchText(item)))
                .Where(item => terms.All(term => item.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.Entity.Id.ToString(), StringComparer.Ordinal)
                .Select(item => item.Entity)
                .ToArray();
            var selected = new List<ExperienceEntity>();
            var bytes = 0;
            foreach (var item in matches.Take(request.MaximumItems))
            {
                var itemBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item).Length;
                if (bytes + itemBytes > request.MaximumBytes) break;
                selected.Add(item);
                bytes += itemBytes;
            }
            var truncated = selected.Count < matches.Length;
            return Task.FromResult(truncated
                ? ExperienceRecallResult<ExperienceEntity>.Truncated(selected, "Lexical results reached an item or byte bound.")
                : ExperienceRecallResult<ExperienceEntity>.Success(selected));
        }
    }

    private static string SearchText(ExperienceEntity entity) =>
        entity.Kind + " " + string.Join(' ', entity.Properties.Select(item => item.Key + " " + item.Value.GetRawText()));
}

internal static class ExperienceModelSemanticEquality
{
    public static bool Entity(ExperienceEntity left, ExperienceEntity right) =>
        left.Id == right.Id && left.Kind == right.Kind && left.Provenance == right.Provenance &&
        left.Validity == right.Validity && Properties(left.Properties, right.Properties) && Evidence(left.Evidence, right.Evidence);

    public static bool Relation(ExperienceRelation left, ExperienceRelation right) =>
        left.Id == right.Id && left.From == right.From && left.To == right.To && left.Kind == right.Kind &&
        left.Provenance == right.Provenance && left.Validity == right.Validity &&
        Properties(left.Properties, right.Properties) && Evidence(left.Evidence, right.Evidence);

    private static bool Properties(IReadOnlyDictionary<string, System.Text.Json.JsonElement> left, IReadOnlyDictionary<string, System.Text.Json.JsonElement> right) =>
        left.Count == right.Count && left.All(item =>
            right.TryGetValue(item.Key, out var value) &&
            System.Text.Json.JsonElement.DeepEquals(value, item.Value));

    private static bool Evidence(IReadOnlyList<ExperienceEvidenceReference> left, IReadOnlyList<ExperienceEvidenceReference> right) =>
        left.Count == right.Count && left.SequenceEqual(right);
}
