using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SqliteSessionProjectionStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "hongxian-session-projection-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_UpdatesRebuildableCurrentStateProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var sessionId = SessionId.New();
        var workflowRun = Guid.CreateVersion7();
        var requested = await events.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.InputRequested,
            DateTimeOffset.UtcNow, CorrelationId: workflowRun), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.InputProvided,
            DateTimeOffset.UtcNow, CausationId: requested.EventId,
            CorrelationId: workflowRun), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.RevisionAccepted,
            DateTimeOffset.UtcNow, CrossSystemRefs: new Dictionary<string, string>
            {
                ["toRevision"] = "workspace:2"
            }), ct);

        var snapshot = await projections.GetAsync(sessionId, ct);

        snapshot.Should().NotBeNull();
        snapshot!.AppliedSequence.Should().Be(3);
        snapshot.HeadHash.Should().Be((await events.VerifyChainAsync(sessionId, ct))!.Hash);
        snapshot.State.TotalEvents.Should().Be(3);
        snapshot.State.PendingInputEventIds.Should().BeEmpty();
        snapshot.State.CurrentRevision.Should().Be("workspace:2");
        snapshot.State.LastCommittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rebuild_RepairsMissingOrDeletedProjectionFromLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(Path.Combine(rootPath, "sessions"));
        var sessionId = SessionId.New();
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "hongxian", SessionEventTypes.ExecutionStarted,
            DateTimeOffset.UtcNow, CorrelationId: Guid.CreateVersion7()), ct);
        var history = await events.ReadVerifiedHistoryAsync(sessionId, ct);

        var rebuilt = await projections.RebuildAsync(history, ct);

        rebuilt!.AppliedSequence.Should().Be(2);
        rebuilt.HeadHash.Should().Be(history.VerifiedHead.Hash);
        rebuilt.State.TotalEvents.Should().Be(2);
        (await projections.ListAsync(ct)).Should().ContainSingle(item => item.SessionId == sessionId);
    }

    [Fact]
    public async Task Rebuild_RejectsBrokenHashContinuityAndExistingHeadConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "verified-rebuild.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "verified-sessions"));
        var sessionId = SessionId.New();
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId, "assistant", SessionEventTypes.AssistantMessage, DateTimeOffset.UtcNow), ct);
        var history = await events.ReadVerifiedHistoryAsync(sessionId, ct);

        var broken = history with
        {
            Events = [history.Events[0], history.Events[1] with { PreviousHash = new string('0', 64) }]
        };
        var brokenAction = () => projections.RebuildAsync(broken, ct);
        (await brokenAction.Should()
            .ThrowAsync<SessionProjectionConsistencyException>()).Which.Failure
            .Should().Be(SessionProjectionConsistencyFailure.HashChainContinuity);

        await projections.RebuildAsync(history, ct);
        var conflictingHash = new string('f', 64);
        var conflicting = history with
        {
            VerifiedHead = history.VerifiedHead with { Hash = conflictingHash },
            Events = [history.Events[0], history.Events[1] with { Hash = conflictingHash }]
        };
        var conflictAction = () => projections.RebuildAsync(conflicting, ct);
        (await conflictAction.Should()
            .ThrowAsync<SessionProjectionConsistencyException>()).Which.Failure
            .Should().Be(SessionProjectionConsistencyFailure.HeadConflict);
    }

    [Fact]
    public async Task Apply_PersistsStructuredDecisionAndIncidentState()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogPath = Path.Combine(rootPath, "catalog.db");
        var projections = new SqliteSessionProjectionStore(catalogPath, pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var sessionId = SessionId.New();
        var workflowRun = Guid.CreateVersion7();
        var proposalId = Guid.CreateVersion7();
        var incidentId = Guid.CreateVersion7();
        var input = await events.AppendAsync(new SessionEventRequest(
            sessionId,
            "hongxian",
            SessionEventTypes.InputRequested,
            DateTimeOffset.UtcNow,
            CorrelationId: workflowRun,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["signalName"] = "clarification"
            }), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId,
            "hongxian",
            SessionEventTypes.DecisionProposed,
            DateTimeOffset.UtcNow,
            CorrelationId: workflowRun,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["proposalId"] = proposalId.ToString("D"),
                ["externalOperationId"] = workflowRun.ToString("D"),
                ["targetId"] = "generation/task-1",
                ["revision"] = "workspace:7"
            }), ct);
        await events.AppendAsync(new SessionEventRequest(
            sessionId,
            "hongxian",
            SessionEventTypes.IncidentDetected,
            DateTimeOffset.UtcNow,
            CorrelationId: workflowRun,
            CrossSystemRefs: new Dictionary<string, string>
            {
                ["incidentId"] = incidentId.ToString("D"),
                ["reasonCode"] = "NeedsReconciliation",
                ["severity"] = SessionIncidentSeverity.Error.ToString()
            }), ct);

        // Read through a fresh store instance so the assertion covers the JSON
        // persistence contract rather than only the in-memory projection.
        var reopened = new SqliteSessionProjectionStore(catalogPath, pooling: false);
        var snapshot = await reopened.GetAsync(sessionId, ct);

        snapshot!.State.PendingInputs.Should().ContainSingle(item =>
            item.RequestEventId == input.EventId && item.SignalName == "clarification");
        snapshot.State.PendingApprovals.Should().ContainSingle(item =>
            item.ProposalId == proposalId && item.Revision == "workspace:7");
        snapshot.State.ActiveIncidents.Should().ContainSingle(item =>
            item.IncidentId == incidentId && item.ReasonCode == "NeedsReconciliation");
        snapshot.State.OperatorState.Should().Be(SessionOperatorState.AwaitingInput,
            "an unresolved user input request takes precedence over a recoverable incident");
    }

    [Fact]
    public async Task Apply_SameSequenceFromDifferentChain_RejectsHeadConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(Path.Combine(rootPath, "sessions"));
        var sessionId = SessionId.New();
        var committed = await events.AppendAsync(new SessionEventRequest(
            sessionId, "user", SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        await projections.ApplyAsync(committed, ct);

        var conflict = () => projections.ApplyAsync(committed with { Hash = new string('0', 64) }, ct);

        (await conflict.Should()
            .ThrowAsync<SessionProjectionConsistencyException>()).Which.Failure
            .Should().Be(SessionProjectionConsistencyFailure.HeadConflict);
    }

    [Fact]
    public async Task DeliveryCursor_ExposesLagAndClearsItAtomicallyWithProjectionApply()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var sessionId = SessionId.New();
        var committed = await events.AppendAsync(new SessionEventRequest(
            sessionId,
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);

        await projections.RecordCommittedAsync(committed, ct);
        var lagging = await projections.GetDeliveryStatusAsync(sessionId, ct);

        lagging!.IsLagging.Should().BeTrue();
        lagging.CommittedSequence.Should().Be(1);
        lagging.AppliedSequence.Should().Be(0);
        (await projections.ListLaggingAsync(cancellationToken: ct))
            .Should().ContainSingle();

        await projections.ApplyAsync(committed, ct);
        var current = await projections.GetDeliveryStatusAsync(sessionId, ct);
        current!.IsLagging.Should().BeFalse();
        current.AppliedSequence.Should().Be(1);
        current.AppliedHeadHash.Should().Be(committed.Hash);
        current.LastFailureType.Should().BeNull();
    }

    [Fact]
    public async Task DeliveryCursor_PersistsBoundedFailureDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "catalog.db"), pooling: false);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var committed = await events.AppendAsync(new SessionEventRequest(
            SessionId.New(),
            "user",
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);
        await projections.RecordCommittedAsync(committed, ct);

        await projections.RecordFailureAsync(
            committed,
            new InvalidOperationException(new string('x', 3000)),
            ct);

        var status = await projections.GetDeliveryStatusAsync(committed.SessionId, ct);
        status!.IsLagging.Should().BeTrue();
        status.LastFailureType.Should().Be(typeof(InvalidOperationException).FullName);
        status.LastFailureDetail.Should().HaveLength(2048);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
