using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "hongxian-example");
await using var sessions = new SimingSessionEventStore(root);

var sessionId = SessionId.New();
var started = await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Actor: "example-user",
    EventType: SessionEventTypes.SessionCreated,
    OccurredAt: DateTimeOffset.UtcNow,
    IdempotencyKey: $"session:{sessionId}:created"));

await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Actor: "example-worker",
    EventType: SessionEventTypes.ExecutionStarted,
    OccurredAt: DateTimeOffset.UtcNow,
    CausationId: started.EventId,
    CorrelationId: Guid.CreateVersion7(),
    CrossSystemRefs: new Dictionary<string, string>
    {
        ["provider"] = "example"
    }));

var events = await sessions.ReadAsync(sessionId);
var head = await sessions.VerifyChainAsync(sessionId);

Console.WriteLine($"Session {sessionId} contains {events.Count} verified events.");
Console.WriteLine($"Ledger head: {head?.Hash}");
