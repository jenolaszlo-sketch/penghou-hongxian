using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SessionEventStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-session-event-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_OrdersSequencesAndHashChains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var correlation = Guid.NewGuid();

        var first = await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
            CorrelationId: correlation, PayloadJson: "{\"prompt\":\"hello\"}"), ct);
        var second = await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.ExecutionStarted, DateTimeOffset.UtcNow,
            CorrelationId: correlation, CausationId: first.EventId), ct);

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(2);
        first.PreviousHash.Should().BeNull();
        second.PreviousHash.Should().Be(first.Hash);
        first.Hash.Should().NotBe(second.Hash);

        var all = await store.ReadAsync(sessionId, cancellationToken: ct);
        all.Should().HaveCount(2);
        all[0].Sequence.Should().Be(1);
        all[1].Sequence.Should().Be(2);
        all[1].CausationId.Should().Be(first.EventId);

        var last = await store.VerifyChainAsync(sessionId, ct);
        last!.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Append_WithIdempotencyKey_IsRetrySafeAndRejectsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            "hongxian",
            SessionEventTypes.OperationPrepared,
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.CreateVersion7(),
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["operationId"] = Guid.CreateVersion7().ToString("D")
            },
            IdempotencyKey: "operation:prepared");

        var first = await store.AppendAsync(request, ct);
        var replay = await store.AppendAsync(
            request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);
        replay.Should().BeEquivalentTo(first);
        (await store.ReadAsync(sessionId, cancellationToken: ct))
            .Should().ContainSingle();

        var conflict = () => store.AppendAsync(
            request with { EventType = SessionEventTypes.OperationTransitioned },
            ct);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already used by a different event*");
    }

    [Fact]
    public async Task VerifyChain_DetectsTampering()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.New();
        var sessionPath = Path.Combine(rootPath, sessionId.ToString(), "session.db");
        await using (var store = new SimingSessionEventStore(rootPath))
        {
            await store.AppendAsync(new SessionEventRequest(
                sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
            await store.AppendAsync(new SessionEventRequest(
                sessionId, "hongxian", SessionEventTypes.ExecutionStarted, DateTimeOffset.UtcNow), ct);
        }

        await using (var connection = new SqliteConnection($"Data Source={sessionPath}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER ledger_entries_no_update; UPDATE ledger_entries SET event_type = 'changed' WHERE sequence = 1;";
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var verifier = new SimingSessionEventStore(rootPath);
        var act = () => verifier.VerifyChainAsync(sessionId, ct);
        await act.Should().ThrowAsync<Penghou.Siming.Sqlite.SimingSchemaCompatibilityException>();
    }

    [Fact]
    public async Task Projection_TracksPendingInputsRevisionAndLastWorkflow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var workflowRun = Guid.NewGuid();
        var inputEvent = await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.InputRequested, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.ExecutionStarted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.RevisionPromoted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["toRevision"] = "rev-abc"
            }), ct);

        var projection = SessionTimelineProjection.Project(
            await store.ReadAsync(sessionId, cancellationToken: ct));
        projection.TotalEvents.Should().Be(3);
        projection.PendingInputEventIds.Should().Equal(inputEvent.EventId.ToString("D"));
        projection.CurrentRevision.Should().Be("rev-abc");
        projection.LastExternalOperationId.Should().Be(workflowRun);

        // Provide the input: pending resolves
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.InputProvided, DateTimeOffset.UtcNow,
            CausationId: inputEvent.EventId, CorrelationId: workflowRun), ct);
        var projectionAfter = SessionTimelineProjection.Project(
            await store.ReadAsync(sessionId, cancellationToken: ct));
        projectionAfter.PendingInputEventIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconstructsWhoWhatWhenWhyAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var workflowRun = Guid.NewGuid();

        var userMessage = await store.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, PayloadJson: "{\"prompt\":\"add auth\"}"), ct);
        var started = await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.ExecutionStarted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: userMessage.EventId), ct);
        var approval = await store.AppendAsync(new SessionEventRequest(
            sessionId, "tester", SessionEventTypes.ApprovalGranted, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: started.EventId,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["targetId"] = "generation/task-1/leaf-1",
                ["externalOperationId"] = workflowRun.ToString("D")
            }), ct);
        await store.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.ExecutionResumed, DateTimeOffset.UtcNow,
            CorrelationId: workflowRun, CausationId: approval.EventId,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["targetId"] = "generation/task-1/leaf-1"
            }), ct);

        var events = await store.ReadAsync(sessionId, cancellationToken: ct);
        var timeline = SessionTimelineProjection.RenderTimeline(events);
        timeline.Should().HaveCount(4);
        timeline[3].Should().Contain("execution-resumed by hongxian");
        timeline[3].Should().Contain($"caused-by {approval.EventId:D}");
        timeline[3].Should().Contain("targetId=generation/task-1/leaf-1");

        // Why: the restart was caused by tester's approval, which was caused by workflow start, caused by user message
        var restart = events[3];
        var whyApproval = events.Single(e => e.EventId == restart.CausationId);
        whyApproval.Actor.Should().Be("tester");
        whyApproval.EventType.Should().Be(SessionEventTypes.ApprovalGranted);
        var whyStart = events.Single(e => e.EventId == whyApproval.CausationId);
        whyStart.EventType.Should().Be(SessionEventTypes.ExecutionStarted);
        var whyUser = events.Single(e => e.EventId == whyStart.CausationId);
        whyUser.Actor.Should().Be("user");
    }

    [Fact]
    public void Projection_UsesStructuredPendingStateAndExplicitOperatorPrecedence()
    {
        var sessionId = SessionId.New();
        var workflowRun = Guid.CreateVersion7();
        var inputId = Guid.CreateVersion7();
        var proposalId = Guid.CreateVersion7();
        var criticalIncidentId = Guid.CreateVersion7();
        var warningIncidentId = Guid.CreateVersion7();
        var committed = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var claimed = committed.AddDays(-1);
        var sequence = 0L;

        SessionEvent Next(
            string eventType,
            Guid? eventId = null,
            Guid? causationId = null,
            IReadOnlyDictionary<string, string>? refs = null) => new()
            {
                SchemaVersion = 1,
                Sequence = ++sequence,
                EventId = eventId ?? Guid.CreateVersion7(),
                SessionId = sessionId,
                Actor = "test",
                EventType = eventType,
                OccurredAt = claimed.AddMinutes(sequence),
                CommittedAt = committed.AddMinutes(sequence),
                CausationId = causationId,
                CorrelationId = workflowRun,
                CrossSystemRefs = refs,
                PayloadSensitivity = SessionPayloadSensitivity.Internal,
                PayloadRetention = SessionPayloadRetention.Omit,
                Hash = $"hash-{sequence}"
            };

        var events = new List<SessionEvent>
        {
            Next(SessionEventTypes.InputRequested, inputId, refs: new Dictionary<string, string>
            {
                ["signalName"] = "clarification"
            }),
            Next(SessionEventTypes.DecisionProposed, refs: new Dictionary<string, string>
            {
                ["proposalId"] = proposalId.ToString("D"),
                ["externalOperationId"] = workflowRun.ToString("D"),
                ["targetId"] = "generation/task-1",
                ["revision"] = "workspace:2"
            }),
            Next(SessionEventTypes.IncidentDetected, refs: new Dictionary<string, string>
            {
                ["incidentId"] = criticalIncidentId.ToString("D"),
                ["reasonCode"] = "LedgerMismatch",
                ["severity"] = SessionIncidentSeverity.Critical.ToString()
            }),
            Next(SessionEventTypes.IncidentDetected, refs: new Dictionary<string, string>
            {
                ["incidentId"] = warningIncidentId.ToString("D"),
                ["reasonCode"] = "Retryable",
                ["severity"] = SessionIncidentSeverity.Warning.ToString()
            }),
            Next(SessionEventTypes.RecoverySucceeded, refs: new Dictionary<string, string>
            {
                ["incidentId"] = warningIncidentId.ToString("D"),
                ["outcome"] = SessionRecoveryOutcome.Recovered.ToString()
            })
        };

        var corrupt = SessionTimelineProjection.Project(events);
        corrupt.OperatorState.Should().Be(SessionOperatorState.Corrupt,
            "resolving a lesser incident must not hide an active critical incident");
        corrupt.PendingInputs.Should().ContainSingle(item =>
            item.RequestEventId == inputId && item.SignalName == "clarification" &&
            item.RequestedAt == events[0].CommittedAt &&
            item.ClaimedOccurredAt == events[0].OccurredAt);
        corrupt.PendingApprovals.Should().ContainSingle(item =>
            item.ProposalId == proposalId && item.TargetId == "generation/task-1");
        corrupt.ActiveIncidents.Should().ContainSingle(item =>
            item.IncidentId == criticalIncidentId);
        corrupt.LastEventAt.Should().Be(events[^1].CommittedAt);
        corrupt.SessionCreatedAt.Should().Be(events[0].CommittedAt);

        events.Add(Next(SessionEventTypes.RecoverySucceeded, refs: new Dictionary<string, string>
        {
            ["incidentId"] = criticalIncidentId.ToString("D"),
            ["outcome"] = SessionRecoveryOutcome.Recovered.ToString()
        }));
        SessionTimelineProjection.Project(events).OperatorState.Should()
            .Be(SessionOperatorState.AwaitingInput);

        events.Add(Next(SessionEventTypes.InputProvided, causationId: inputId));
        SessionTimelineProjection.Project(events).OperatorState.Should()
            .Be(SessionOperatorState.AwaitingApproval);

        events.Add(Next(SessionEventTypes.ApprovalDenied, refs: new Dictionary<string, string>
        {
            ["proposalId"] = proposalId.ToString("D")
        }));
        var ready = SessionTimelineProjection.Project(events);
        ready.OperatorState.Should().Be(SessionOperatorState.Ready);
        ready.PendingInputs.Should().BeEmpty();
        ready.PendingApprovals.Should().BeEmpty();
        ready.ActiveIncidents.Should().BeEmpty();
        ready.ResolvedIncidentCount.Should().Be(2);

        SessionTimelineProjection.RenderTimeline(events)[0]
            .Should().StartWith($"[{events[0].CommittedAt:O}]")
            .And.Contain($"claimed-at {events[0].OccurredAt:O}");
    }

    [Fact]
    public async Task Append_RejectsImplausibleFutureOccurrenceClaimsButAllowsHistoricalClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        await using var store = new SimingSessionEventStore(
            rootPath,
            timePolicy: new SessionEventTimePolicy(TimeSpan.FromMinutes(2)),
            timeProvider: new FixedTimeProvider(now));
        var sessionId = SessionId.New();

        var historical = await store.AppendAsync(new SessionEventRequest(
            sessionId,
            "mirror",
            SessionEventTypes.ExternalEventMirrored,
            now.AddYears(-1)), ct);
        historical.OccurredAt.Should().Be(now.AddYears(-1));
        historical.CommittedAt.Should().NotBe(default);

        var future = () => store.AppendAsync(new SessionEventRequest(
            sessionId,
            "user",
            SessionEventTypes.UserMessage,
            now.AddMinutes(3)), ct);
        await future.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*Occurrence-time claim*");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
