using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Microsoft.Data.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SessionRecoveryCoordinatorTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "hongxian-recovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedThenSuccessfulRecovery_PreservesEveryAttemptAndReturnsReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var projections = new SqliteSessionProjectionStore(Path.Combine(rootPath, "catalog.db"));
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projections);
        var coordinator = new SessionRecoveryCoordinator(events);
        var sessionId = SessionId.New();
        var incidentId = Guid.CreateVersion7();
        var planId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var action = new SessionRecoveryActionReference(
            "retry-publication",
            "search-index",
            "publication-7");
        var detected = await coordinator.DetectAsync(new SessionIncident(
            incidentId,
            sessionId,
            "ParticipantUnavailable",
            SessionIncidentSeverity.Error,
            "Hetu publication was temporarily unavailable.",
            now), ct);
        var plan = new SessionRecoveryPlan(
            planId,
            incidentId,
            sessionId,
            action,
            "Retry the idempotent Hetu publication.",
            Automatic: true,
            PlannedAt: now);
        var planned = await coordinator.PlanAsync(plan, detected.EventId, ct);
        var executor = new SessionRecoveryExecutor(coordinator);
        var first = await executor.ExecuteAsync(
            plan,
            planned.EventId,
            1,
            (_, _) => Task.FromException<SessionRecoveryActionReceipt>(
                new InvalidOperationException("Hetu is temporarily unavailable.")),
            ct);
        first.Outcome.Should().Be(SessionRecoveryOutcome.ReconciliationRequired);

        var failedState = await projections.GetAsync(sessionId, ct);
        failedState!.State.OperatorState.Should().Be(SessionOperatorState.ReconciliationRequired);
        failedState.State.OpenIncidentIds.Should().ContainSingle(incidentId.ToString("D"));

        var receiptId = Guid.CreateVersion7();
        var second = await executor.ExecuteAsync(
            plan,
            planned.EventId,
            2,
            (_, _) => Task.FromResult(new SessionRecoveryActionReceipt(
                receiptId,
                action,
                "The publication was verified and can be opened by identity.",
                now.AddSeconds(1),
                Verified: true,
                ResultIdentity: "index-publication-7")),
            ct);
        second.Outcome.Should().Be(SessionRecoveryOutcome.Recovered);
        second.Receipt!.ReceiptId.Should().Be(receiptId);

        var recoveredState = await projections.GetAsync(sessionId, ct);
        recoveredState!.State.OperatorState.Should().Be(SessionOperatorState.Ready);
        recoveredState.State.OpenIncidentIds.Should().BeEmpty();
        recoveredState.State.ResolvedIncidentCount.Should().Be(1);
        var history = await events.ReadAsync(sessionId, cancellationToken: ct);
        history.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.IncidentDetected,
            SessionEventTypes.RecoveryPlanned,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.RecoveryFailed,
            SessionEventTypes.RecoveryAttempted,
            SessionEventTypes.RecoverySucceeded);
        history.Select(item => item.CausationId).Skip(1).Should().OnlyContain(id => id.HasValue);
        history[^1].CrossSystemRefs!["recoveryReceiptId"].Should().Be(receiptId.ToString("D"));
    }

    [Fact]
    public async Task CompleteRecovered_WithoutVerifiedReceipt_IsRejectedBeforeLedgerAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new SimingSessionEventStore(Path.Combine(rootPath, "sessions"));
        var coordinator = new SessionRecoveryCoordinator(events);
        var sessionId = SessionId.New();
        var incidentId = Guid.CreateVersion7();
        var planId = Guid.CreateVersion7();

        var act = () => coordinator.CompleteAsync(new SessionRecoveryResolution(
            planId,
            incidentId,
            sessionId,
            SessionRecoveryOutcome.Recovered,
            1,
            "Claimed success without evidence.",
            DateTimeOffset.UtcNow), Guid.CreateVersion7(), ct);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*verified action receipt*");
        (await events.ReadAsync(sessionId, cancellationToken: ct)).Should().BeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
