using System.Text;
using System.Text.Json;
using LatticeDbSharp;

namespace Penghou.Hongxian.LatticeDb;

/// <summary>
/// Disposable LatticeDB-backed experience projection and recall provider.
/// The provider owns the native database it opens.
/// </summary>
public sealed class LatticeDbExperienceProvider :
    IExperienceProjectionModelWriter,
    IExperienceRecallReader,
    IExperienceProjectionCheckpointStore,
    IExperienceProjectionCheckpointResetStore,
    IDisposable
{
    private const string EntityLabel = "HxEntity";
    private const string RelationType = "HxRelation";
    private const string CheckpointLabel = "HxCheckpoint";
    private const string SchemaLabel = "HxSchema";
    private const string KeyProperty = "hx_key";
    private const string IdProperty = "hx_id";
    private const string ProjectionProperty = "hx_projection";
    private const string KindProperty = "hx_kind";
    private const string PayloadProperty = "hx_payload";
    private const string TextProperty = "hx_text";
    private const string VersionProperty = "hx_version";
    private const string SchemaKey = "penghou-hongxian-latticedb/v1";
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object gate = new();
    private readonly LatticeDatabase database;
    private readonly bool readOnly;
    private bool disposed;

    public LatticeDbExperienceProvider(LatticeDbExperienceProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
            readOnly = options.OpenMode == LatticeDbOpenMode.ReadOnly;
            var nativeOptions = new LatticeDatabaseOptions
            {
                Create = options.OpenMode == LatticeDbOpenMode.CreateOrOpen,
                ReadOnly = readOnly,
                EnableWal = options.Durability == LatticeDbDurability.DurableWal,
                Lock = options.Lock
            };
            database = options.Location == LatticeDbLocationKind.Memory
                ? LatticeDatabase.OpenMemory(nativeOptions)
                : LatticeDatabase.Open(options.DatabasePath!, nativeOptions);
            if (!readOnly)
                EnsureSchema();
            else
                ValidateSchema();
        }
        catch (Exception exception) when (exception is not LatticeDbProviderException)
        {
            throw Translate("database/open", exception);
        }
    }

    public ExperienceProviderCapabilities Capabilities { get; } = new(
        "latticedb",
        ExperienceProviderCapability.ExactLookup |
        ExperienceProviderCapability.BoundedTraversal |
        ExperienceProviderCapability.LexicalSearch |
        ExperienceProviderCapability.CheckpointConsistency);

    public Task<ExperienceModelWriteResult> UpsertEntityAsync(
        ExperienceEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var result = ExecuteWrite("entity/upsert", cancellationToken, transaction =>
        {
            EnsureProvider(entity.Provenance.Projection);
            var key = EntityKey(entity.Provenance.Projection.ProjectionId, entity.Id);
            var payload = Serialize(entity);
            var existing = transaction.FindNodesByLabelProperty(
                EntityLabel, KeyProperty, LatticeValue.From(key), 2);
            if (existing.Count > 1)
                throw Corrupt("entity/upsert", $"Multiple entities use key '{key}'.");
            if (existing.Count == 1)
                return ExistingResult(transaction.GetProperty(existing[0], PayloadProperty), payload);

            var node = transaction.CreateNode(EntityLabel);
            transaction.AddLabel(node, ScopedEntityLabel(entity.Provenance.Projection.ProjectionId));
            transaction.SetProperty(node, KeyProperty, LatticeValue.From(key));
            transaction.SetProperty(node, IdProperty, LatticeValue.From(entity.Id.ToString()));
            transaction.SetProperty(node, ProjectionProperty,
                LatticeValue.From(entity.Provenance.Projection.ProjectionId.ToString()));
            transaction.SetProperty(node, KindProperty, LatticeValue.From(entity.Kind));
            transaction.SetProperty(node, PayloadProperty, LatticeValue.From(payload));
            transaction.SetProperty(node, TextProperty, LatticeValue.From(BuildSearchText(entity)));
            return new ExperienceModelWriteResult(ExperienceModelWriteOutcome.Applied);
        });
        EnsureProjectionFts(entity.Provenance.Projection);
        return result;
    }

    public Task<ExperienceModelWriteResult> UpsertRelationAsync(
        ExperienceRelation relation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return ExecuteWrite("relation/upsert", cancellationToken, transaction =>
        {
            EnsureProvider(relation.Provenance.Projection);
            var projectionId = relation.Provenance.Projection.ProjectionId;
            var key = RelationKey(projectionId, relation.Id);
            var payload = Serialize(relation);
            var existing = transaction.FindEdgesByTypeProperty(
                RelationType, KeyProperty, LatticeValue.From(key), 2);
            if (existing.Count > 1)
                throw Corrupt("relation/upsert", $"Multiple relations use key '{key}'.");
            if (existing.Count == 1)
                return ExistingResult(transaction.GetEdgeProperty(existing[0], PayloadProperty), payload);

            var source = FindEntityNode(transaction, projectionId, relation.From);
            var target = FindEntityNode(transaction, projectionId, relation.To);
            if (source is null || target is null)
                return new ExperienceModelWriteResult(
                    ExperienceModelWriteOutcome.Conflict,
                    "Both relation endpoints must exist in the same projection.");
            var edge = transaction.CreateEdge(source.Value, target.Value, RelationType);
            transaction.SetEdgeProperty(edge, KeyProperty, LatticeValue.From(key));
            transaction.SetEdgeProperty(edge, IdProperty, LatticeValue.From(relation.Id.ToString()));
            transaction.SetEdgeProperty(edge, ProjectionProperty, LatticeValue.From(projectionId.ToString()));
            transaction.SetEdgeProperty(edge, KindProperty, LatticeValue.From(relation.Kind));
            transaction.SetEdgeProperty(edge, PayloadProperty, LatticeValue.From(payload));
            return new ExperienceModelWriteResult(ExperienceModelWriteOutcome.Applied);
        });
    }

    public Task DeleteProjectionAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ExecuteWrite("projection/delete", cancellationToken, transaction =>
        {
            EnsureProvider(descriptor);
            var projection = descriptor.ProjectionId.ToString();
            var entities = transaction.GetNodesByLabel(EntityLabel);
            var deletedEdges = new HashSet<(LatticeNodeId Source, LatticeNodeId Target, string Type)>();
            foreach (var node in entities)
            {
                foreach (var edge in transaction.GetOutgoingEdges(node, RelationType))
                {
                    if (HasStringEdgeProperty(
                            transaction, edge.Id, ProjectionProperty, projection) &&
                        deletedEdges.Add((edge.Source, edge.Target, edge.Type)))
                        transaction.DeleteEdge(edge.Source, edge.Target, edge.Type);
                }
            }
            foreach (var node in entities)
            {
                if (HasStringProperty(transaction, node, ProjectionProperty, projection))
                    transaction.DeleteNode(node);
            }
            foreach (var node in transaction.GetNodesByLabel(CheckpointLabel))
            {
                if (HasStringProperty(transaction, node, ProjectionProperty, projection))
                    transaction.DeleteNode(node);
            }
            return true;
        });
    }

    public Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
        ExperienceEntityLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceEntity>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The LatticeDB provider does not retain historical projection snapshots."));
        return ExecuteRead("entity/get", cancellationToken, transaction =>
        {
            var node = FindEntityNode(transaction, request.ProjectionId, request.EntityId);
            var items = node is null
                ? Array.Empty<ExperienceEntity>()
                : [ReadEntity(transaction, node.Value, request.ProjectionId)];
            return ExperienceRecallResult<ExperienceEntity>.Success(
                items, ReadCheckpoint(transaction, request.ProjectionId));
        });
    }

    public Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
        ExperienceRelationTraversalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceRelation>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The LatticeDB provider does not retain historical projection snapshots."));
        return ExecuteRead("relation/traverse", cancellationToken, transaction =>
        {
            var start = FindEntityNode(transaction, request.ProjectionId, request.Start);
            if (start is null)
                return ExperienceRecallResult<ExperienceRelation>.Success(
                    [], ReadCheckpoint(transaction, request.ProjectionId));
            var queue = new Queue<(LatticeNodeId Node, int Depth)>();
            var visitedNodes = new HashSet<LatticeNodeId> { start.Value };
            var visitedRelations = new HashSet<ExperienceRelationId>();
            var result = new List<ExperienceRelation>();
            var truncated = false;
            queue.Enqueue((start.Value, 0));
            while (queue.Count > 0 && !truncated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (node, depth) = queue.Dequeue();
                if (depth >= request.MaximumDepth) continue;
                var edges = transaction.GetOutgoingEdges(node, RelationType, request.MaximumItems + 1);
                var relations = edges
                    .Select(edge => TryReadRelation(
                        transaction, edge.Id, request.ProjectionId, out var relation)
                        ? (Edge: edge, Relation: relation)
                        : ((LatticeEdgeInfo Edge, ExperienceRelation Relation)?)null)
                    .Where(item => item is not null)
                    .Select(item => item!.Value)
                    .Where(item => request.RelationKind is null ||
                        string.Equals(item.Relation.Kind, request.RelationKind, StringComparison.Ordinal))
                    .OrderBy(item => item.Relation.Id.ToString(), StringComparer.Ordinal);
                foreach (var item in relations)
                {
                    var relation = item.Relation;
                    if (!visitedRelations.Add(relation.Id)) continue;
                    if (result.Count == request.MaximumItems)
                    {
                        truncated = true;
                        break;
                    }
                    result.Add(relation);
                    if (visitedNodes.Add(item.Edge.Target)) queue.Enqueue((item.Edge.Target, depth + 1));
                }
            }
            var checkpoint = ReadCheckpoint(transaction, request.ProjectionId);
            return truncated
                ? ExperienceRecallResult<ExperienceRelation>.Truncated(
                    result, "Traversal reached the requested item bound.", checkpoint)
                : ExperienceRecallResult<ExperienceRelation>.Success(result, checkpoint);
        });
    }

    public Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
        ExperienceLexicalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AsOf is not null)
            return Task.FromResult(ExperienceRecallResult<ExperienceEntity>.Unsupported(
                ExperienceProviderCapability.AsOf,
                "The LatticeDB provider does not retain historical projection snapshots."));
        return ExecuteRead("entity/search-lexical", cancellationToken, transaction =>
        {
            var hits = transaction.FtsSearch(
                ScopedEntityLabel(request.ProjectionId), TextProperty,
                request.Query, request.MaximumItems + 1);
            var candidates = hits.Select(hit =>
                {
                    var payload = transaction.GetProperty(hit.NodeId, PayloadProperty).AsString();
                    return (hit.Score, Payload: payload,
                        Entity: Deserialize<ExperienceEntity>(payload, "entity/search-lexical"));
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Entity.Id.ToString(), StringComparer.Ordinal)
                .ToArray();
            var result = new List<ExperienceEntity>();
            var bytes = 0;
            var truncated = candidates.Length > request.MaximumItems;
            foreach (var candidate in candidates.Take(request.MaximumItems))
            {
                var payloadBytes = Encoding.UTF8.GetByteCount(candidate.Payload);
                if (bytes + payloadBytes > request.MaximumBytes)
                {
                    truncated = true;
                    break;
                }
                result.Add(candidate.Entity);
                bytes += payloadBytes;
            }
            var checkpoint = ReadCheckpoint(transaction, request.ProjectionId);
            return truncated
                ? ExperienceRecallResult<ExperienceEntity>.Truncated(
                    result, "Lexical recall reached an item or byte bound.", checkpoint)
                : ExperienceRecallResult<ExperienceEntity>.Success(result, checkpoint);
        });
    }

    public Task<ExperienceProjectionCheckpoint?> GetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default) =>
        ExecuteRead("checkpoint/get", cancellationToken,
            transaction => ReadCheckpoint(transaction, projectionId));

    public Task<ExperienceProjectionCheckpoint> AdvanceAsync(
        AdvanceExperienceProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteWrite("checkpoint/advance", cancellationToken, transaction =>
        {
            EnsureProvider(request.Descriptor);
            var current = ReadCheckpoint(transaction, request.Descriptor.ProjectionId);
            var next = ExperienceProjectionCheckpointRules.Validate(current, request);
            if (ReferenceEquals(current, next)) return next;
            var key = CheckpointKey(request.Descriptor.ProjectionId);
            var nodes = transaction.FindNodesByLabelProperty(
                CheckpointLabel, KeyProperty, LatticeValue.From(key), 2);
            if (nodes.Count > 1)
                throw Corrupt("checkpoint/advance", $"Multiple checkpoints use key '{key}'.");
            var node = nodes.Count == 0 ? transaction.CreateNode(CheckpointLabel) : nodes[0];
            transaction.SetProperty(node, KeyProperty, LatticeValue.From(key));
            transaction.SetProperty(node, ProjectionProperty,
                LatticeValue.From(request.Descriptor.ProjectionId.ToString()));
            transaction.SetProperty(node, VersionProperty, LatticeValue.From(next.Version));
            transaction.SetProperty(node, PayloadProperty, LatticeValue.From(SerializeCheckpoint(next)));
            return next;
        });
    }

    public Task ResetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default) =>
        ExecuteWrite("checkpoint/reset", cancellationToken, transaction =>
        {
            foreach (var node in FindCheckpointNodes(transaction, projectionId))
                transaction.DeleteNode(node);
            return true;
        });

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            database.Dispose();
        }
    }

    private void EnsureSchema()
    {
        lock (gate)
        {
            using (var transaction = database.BeginReadTransaction())
            {
                var markers = transaction.GetNodesByLabel(SchemaLabel);
                if (markers.Count > 0)
                {
                    ValidateSchemaMarker(transaction, markers);
                    return;
                }
                transaction.Commit();
            }
            using var write = database.BeginWriteTransaction();
            var marker = write.CreateNode(SchemaLabel);
            write.SetProperty(marker, KeyProperty, LatticeValue.From(SchemaKey));
            write.SetProperty(marker, VersionProperty, LatticeValue.From((long)SchemaVersion));
            write.Commit();
            database.CreateNodePropertyIndex(EntityLabel, KeyProperty);
            database.CreateEdgePropertyIndex(RelationType, KeyProperty);
            database.CreateNodePropertyIndex(CheckpointLabel, KeyProperty);
        }
    }

    private void EnsureProjectionFts(ExperienceProjectionDescriptor descriptor)
    {
        EnsureProvider(descriptor);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (readOnly)
                throw new LatticeDbProviderException(
                    LatticeDbProviderFailure.InvalidConfiguration,
                    "schema/create-lexical-index",
                    "The provider was opened read-only.");
            var label = ScopedEntityLabel(descriptor.ProjectionId);
            try
            {
                if (!database.NodeFtsIndexExists(label, TextProperty))
                    database.CreateNodeFtsIndex(label, TextProperty);
            }
            catch (Exception exception) when (exception is not LatticeDbProviderException)
            {
                throw Translate("schema/create-lexical-index", exception);
            }
        }
    }

    private void ValidateSchema()
    {
        lock (gate)
        {
            using var transaction = database.BeginReadTransaction();
            ValidateSchemaMarker(transaction, transaction.GetNodesByLabel(SchemaLabel));
        }
    }

    private static void ValidateSchemaMarker(
        LatticeTransaction transaction,
        IReadOnlyList<LatticeNodeId> markers)
    {
        if (markers.Count != 1 ||
            !HasStringProperty(transaction, markers[0], KeyProperty, SchemaKey) ||
            !transaction.TryGetProperty(markers[0], VersionProperty, out var version) ||
            version.Type != LatticeValueType.Integer || version.AsInt64() != SchemaVersion)
            throw Corrupt("schema/validate", "The LatticeDB provider schema is missing or unsupported.");
    }

    private Task<T> ExecuteRead<T>(
        string operation,
        CancellationToken cancellationToken,
        Func<LatticeTransaction, T> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                using var transaction = database.BeginReadTransaction();
                return Task.FromResult(action(transaction));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not LatticeDbProviderException)
            {
                throw Translate(operation, exception);
            }
        }
    }

    private Task<T> ExecuteWrite<T>(
        string operation,
        CancellationToken cancellationToken,
        Func<LatticeTransaction, T> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (readOnly)
                throw new LatticeDbProviderException(
                    LatticeDbProviderFailure.InvalidConfiguration, operation,
                    "The provider was opened read-only.");
            try
            {
                using var transaction = database.BeginWriteTransaction();
                var result = action(transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return Task.FromResult(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not LatticeDbProviderException)
            {
                throw Translate(operation, exception);
            }
        }
    }

    private static ExperienceModelWriteResult ExistingResult(LatticeValue stored, string attempted)
    {
        if (stored.Type != LatticeValueType.String)
            throw Corrupt("record/read", "A stored payload is not a string.");
        return string.Equals(stored.AsString(), attempted, StringComparison.Ordinal)
            ? new ExperienceModelWriteResult(ExperienceModelWriteOutcome.AlreadyPresent)
            : new ExperienceModelWriteResult(
                ExperienceModelWriteOutcome.Conflict,
                "The stable record identity already contains different content.");
    }

    private static LatticeNodeId? FindEntityNode(
        LatticeTransaction transaction,
        ExperienceProjectionId projectionId,
        ExperienceEntityId entityId)
    {
        var key = EntityKey(projectionId, entityId);
        var nodes = transaction.FindNodesByLabelProperty(
            EntityLabel, KeyProperty, LatticeValue.From(key), 2);
        if (nodes.Count > 1)
            throw Corrupt("entity/find", $"Multiple entities use key '{key}'.");
        return nodes.Count == 0 ? null : nodes[0];
    }

    private static ExperienceEntity ReadEntity(
        LatticeTransaction transaction,
        LatticeNodeId node,
        ExperienceProjectionId projectionId)
    {
        var entity = Deserialize<ExperienceEntity>(
            transaction.GetProperty(node, PayloadProperty).AsString(), "entity/read");
        if (entity.Provenance.Projection.ProjectionId != projectionId ||
            !HasStringProperty(transaction, node, KeyProperty, EntityKey(projectionId, entity.Id)))
            throw Corrupt("entity/read", "A stored entity does not match its lookup key.");
        return entity;
    }

    private static bool TryReadRelation(
        LatticeTransaction transaction,
        LatticeEdgeId edge,
        ExperienceProjectionId projectionId,
        out ExperienceRelation relation)
    {
        if (!HasStringEdgeProperty(transaction, edge, ProjectionProperty, projectionId.ToString()))
        {
            relation = null!;
            return false;
        }
        relation = Deserialize<ExperienceRelation>(
            transaction.GetEdgeProperty(edge, PayloadProperty).AsString(), "relation/read");
        if (relation.Provenance.Projection.ProjectionId != projectionId ||
            !HasStringEdgeProperty(transaction, edge, KeyProperty, RelationKey(projectionId, relation.Id)))
            throw Corrupt("relation/read", "A stored relation does not match its lookup key.");
        return true;
    }

    private static ExperienceProjectionCheckpoint? ReadCheckpoint(
        LatticeTransaction transaction,
        ExperienceProjectionId projectionId)
    {
        var nodes = FindCheckpointNodes(transaction, projectionId);
        if (nodes.Count == 0) return null;
        if (nodes.Count > 1)
            throw Corrupt("checkpoint/read", "Multiple checkpoints exist for one projection.");
        var checkpoint = DeserializeCheckpoint(
            transaction.GetProperty(nodes[0], PayloadProperty).AsString());
        if (checkpoint.Descriptor.ProjectionId != projectionId)
            throw Corrupt("checkpoint/read", "The checkpoint payload does not match its lookup key.");
        return checkpoint;
    }

    private static IReadOnlyList<LatticeNodeId> FindCheckpointNodes(
        LatticeTransaction transaction,
        ExperienceProjectionId projectionId) =>
        transaction.FindNodesByLabelProperty(
            CheckpointLabel, KeyProperty,
            LatticeValue.From(CheckpointKey(projectionId)), 2);

    private static bool HasStringProperty(
        LatticeTransaction transaction, LatticeNodeId node, string property, string expected) =>
        transaction.TryGetProperty(node, property, out var value) &&
        value.Type == LatticeValueType.String &&
        string.Equals(value.AsString(), expected, StringComparison.Ordinal);

    private static bool HasStringEdgeProperty(
        LatticeTransaction transaction, LatticeEdgeId edge, string property, string expected) =>
        transaction.TryGetEdgeProperty(edge, property, out var value) &&
        value.Type == LatticeValueType.String &&
        string.Equals(value.AsString(), expected, StringComparison.Ordinal);

    private static string BuildSearchText(ExperienceEntity entity) =>
        string.Join('\n', entity.Properties
            .Where(pair => pair.Value.ValueKind == JsonValueKind.String)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.GetString()));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string SerializeCheckpoint(ExperienceProjectionCheckpoint checkpoint) =>
        Serialize(new CheckpointEnvelope(
            SchemaVersion,
            checkpoint.Descriptor.ProjectionId.Value,
            checkpoint.Descriptor.Provider,
            checkpoint.Descriptor.ProjectorName,
            checkpoint.Descriptor.SchemaVersion,
            checkpoint.Descriptor.PolicyVersion,
            checkpoint.Position.Digest,
            checkpoint.Version,
            checkpoint.Position.Streams.Select(stream => new CheckpointStreamEnvelope(
                stream.SessionId.Value,
                stream.SessionLedgerHead.LedgerIdentity,
                stream.SessionLedgerHead.Sequence,
                stream.SessionLedgerHead.Hash)).ToArray()));

    private static ExperienceProjectionCheckpoint DeserializeCheckpoint(string payload)
    {
        var stored = Deserialize<CheckpointEnvelope>(payload, "checkpoint/read");
        if (stored.StorageSchemaVersion != SchemaVersion)
            throw Corrupt("checkpoint/read", "The checkpoint storage schema is unsupported.");
        var descriptor = new ExperienceProjectionDescriptor(
            new ExperienceProjectionId(stored.ProjectionId),
            stored.Provider,
            stored.ProjectorName,
            stored.ProjectorSchemaVersion,
            stored.PolicyVersion);
        var position = new ExperienceProjectionPosition(
            stored.Streams.Select(stream => new SessionEvidencePosition(
                new SessionId(stream.SessionId),
                new SessionLedgerHead(
                    stream.LedgerIdentity,
                    stream.Sequence,
                    stream.HeadHash))),
            stored.PositionDigest);
        return new ExperienceProjectionCheckpoint(descriptor, position, stored.Version);
    }

    private static T Deserialize<T>(string payload, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
                throw Corrupt(operation, "A stored payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new LatticeDbProviderException(
                LatticeDbProviderFailure.NativeOperation, operation,
                "A stored portable payload is invalid.", exception);
        }
    }

    private static string EntityKey(ExperienceProjectionId projectionId, ExperienceEntityId id) =>
        $"{projectionId}/{id}";
    private static string RelationKey(ExperienceProjectionId projectionId, ExperienceRelationId id) =>
        $"{projectionId}/{id}";
    private static string CheckpointKey(ExperienceProjectionId projectionId) =>
        $"{projectionId}/checkpoint";
    private static string ScopedEntityLabel(ExperienceProjectionId projectionId) =>
        $"HxEntity{projectionId.Value:N}";

    private static void EnsureProvider(ExperienceProjectionDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Provider, "latticedb", StringComparison.Ordinal))
            throw new ArgumentException(
                "The projection descriptor provider must be 'latticedb'.",
                nameof(descriptor));
    }

    private static LatticeDbProviderException Corrupt(string operation, string message) =>
        new(LatticeDbProviderFailure.NativeOperation, operation, message);

    private static LatticeDbProviderException Translate(string operation, Exception exception) =>
        exception switch
        {
            LatticeNativeException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException =>
                new LatticeDbProviderException(
                    LatticeDbProviderFailure.NativeRuntime, operation,
                    "The LatticeDB native runtime is unavailable or incompatible.", exception),
            ArgumentException => new LatticeDbProviderException(
                LatticeDbProviderFailure.InvalidConfiguration, operation,
                exception.Message, exception),
            _ => new LatticeDbProviderException(
                LatticeDbProviderFailure.NativeOperation, operation,
                "The LatticeDB operation failed.", exception)
        };

    private sealed record CheckpointEnvelope(
        int StorageSchemaVersion,
        Guid ProjectionId,
        string Provider,
        string ProjectorName,
        int ProjectorSchemaVersion,
        string PolicyVersion,
        string PositionDigest,
        long Version,
        IReadOnlyList<CheckpointStreamEnvelope> Streams);

    private sealed record CheckpointStreamEnvelope(
        Guid SessionId,
        string LedgerIdentity,
        long Sequence,
        string HeadHash);
}
