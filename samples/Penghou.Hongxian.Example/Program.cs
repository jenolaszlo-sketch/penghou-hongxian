using System.Text.Json;
using Penghou.Hongxian;
using Penghou.Hongxian.LatticeDb;
using Penghou.Hongxian.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "hongxian-example");
await using var sessions = new SimingSessionEventStore(root);

// 1. Append evidence: one session with three message events.
var sessionId = SessionId.New();
var participant = SessionParticipantAttribution.Human("example-user", "example");
await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    participant,
    SessionEventTypes.SessionCreated,
    DateTimeOffset.UtcNow,
    IdempotencyKey: $"session:{sessionId}:created"));
var index = 0;
foreach (var (eventType, text) in new[]
         {
             (SessionEventTypes.UserMessage, "durable sessions survive process restarts"),
             (SessionEventTypes.AssistantMessage, "durable projections rebuild from verified ledgers"),
             (SessionEventTypes.UserMessage, "durable recall stays bounded for audits")
         })
{
    await sessions.AppendAsync(
        new SessionEventRequest(
            sessionId,
            participant,
            eventType,
            DateTimeOffset.UtcNow,
            IdempotencyKey: $"session:{sessionId}:message:{index++}"),
        new { text });
}

var history = await sessions.ReadVerifiedHistoryAsync(sessionId);
Console.WriteLine($"Session {sessionId} contains {history.Events.Count} verified events.");

// 2. Project the verified history into LatticeDB with a sample-local mapping.
//    The mapping is application policy: message events become message entities
//    anchored to their immutable ledger location.
using var provider = new LatticeDbExperienceProvider(
    new LatticeDbExperienceProviderOptions
    {
        Location = LatticeDbLocationKind.Memory,
        Lock = false
    });
var projection = new ExperienceProjectionDescriptor(
    ExperienceProjectionId.New(), "latticedb", "example-projector", 1, "policy-1");
await ProjectAsync(provider, projection, history);

// 3. Bounded recall over the disposable projection.
var request = new ExperienceBoundedRecallRequest(
    projection.ProjectionId,
    new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
    "durable",
    maximumItems: 2,
    maximumBytes: 65_536,
    maximumTokens: 1_000);
var result = await ExperienceBoundedRecall.ExecuteAsync(provider, request);
if (result.Completion != ExperienceBoundedRecallCompletion.Completed || result.Items.Count == 0)
    throw new InvalidOperationException("The example recall returned no evidence.");

// 4. Record what the recall actually supplied as an auditable receipt.
var context = string.Join(
    '\n',
    result.Items.Select(item => item.Record.Properties["text"].GetString()));
var contextDigest = ExperienceRecallReceipts.HashSuppliedContext(context);
var receipt = ExperienceRecallReceipt.Create(result, contextDigest);
var receiptEvent = await sessions.AppendRecallReceiptAsync(
    sessionId,
    SessionParticipantAttribution.System("example-recall", "example"),
    receipt,
    DateTimeOffset.UtcNow);

// 5. Rebuild the disposable projection and prove equivalent recall.
await provider.DeleteProjectionAsync(projection);
await ProjectAsync(provider, projection, history);
var replayed = await ExperienceBoundedRecall.ExecuteAsync(provider, request);
var equivalent = replayed.Items.Select(item => item.Record.Id)
    .SequenceEqual(result.Items.Select(item => item.Record.Id));

// 6. Explain exactly what evidence influenced the sample decision.
Console.WriteLine($"Projected {result.Items.Count} recalled entities (projection {projection.ProjectionId}).");
Console.WriteLine($"Recall fingerprint: {result.QueryFingerprint}");
Console.WriteLine($"Policy: {result.Policy.Name}/{result.Policy.Version}");
foreach (var item in result.Items)
{
    var evidence = string.Join(
        ", ",
        item.Record.Evidence.Select(reference =>
            $"{reference.SessionId}:{reference.Sequence}:{reference.EventHash[..8]}"));
    Console.WriteLine(
        $"  rank {item.Rank} {item.Record.Id} kind={item.Record.Kind} " +
        $"evidence=[{evidence}] tokens~{item.EstimatedTokens}");
}
Console.WriteLine($"Truncated: {result.IsTruncated} ({result.Diagnostic})");
Console.WriteLine($"Supplied context digest: {contextDigest}");
Console.WriteLine($"Recall receipt appended as event {receiptEvent.Sequence} ({receiptEvent.EventId}).");
Console.WriteLine($"Rebuild produced equivalent recall: {equivalent}.");
if (!equivalent)
    throw new InvalidOperationException("The rebuilt projection did not reproduce the recall.");

static async Task ProjectAsync(
    LatticeDbExperienceProvider provider,
    ExperienceProjectionDescriptor projection,
    VerifiedSessionHistory history)
{
    foreach (var sessionEvent in history.Events)
    {
        if (sessionEvent is not
            {
                EventType: SessionEventTypes.UserMessage or SessionEventTypes.AssistantMessage,
                Sequence: > 0
            })
            continue;
        string? text;
        try
        {
            text = sessionEvent.ReadPayload<JsonElement>().GetProperty("text").GetString();
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or
                InvalidOperationException or
                JsonException or
                SessionPayloadUnavailableException or
                SessionPayloadFormatException)
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(text))
            continue;
        await provider.UpsertEntityAsync(new ExperienceEntity(
            ExperienceEntityId.CreateDeterministic(
                projection.ProjectionId, "message", $"event:{sessionEvent.Sequence}"),
            "message",
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(
                    projection.ProjectionId, "example-mapping", "v1")),
            new Dictionary<string, JsonElement>
            {
                ["text"] = JsonSerializer.SerializeToElement(text)
            },
            evidence:
            [
                new ExperienceEvidenceReference(
                    history.SessionId,
                    history.VerifiedHead.LedgerIdentity,
                    sessionEvent.Sequence,
                    sessionEvent.EventId,
                    sessionEvent.Hash)
            ]));
    }
}
