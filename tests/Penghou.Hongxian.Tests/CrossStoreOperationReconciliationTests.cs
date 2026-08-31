using FluentAssertions;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class CrossStoreOperationReconciliationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-reconciliation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Inspect_ReturnsProviderNeutralActionCodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "operations.db"), pooling: false);
        var sessionId = SessionId.New();
        var prepared = await StartAsync(store, sessionId, "prepared", ct);
        var active = await StartAsync(store, sessionId, "active", ct);
        await store.TransitionAsync(
            active.Id,
            CrossStoreOperationState.Active,
            DateTimeOffset.UtcNow,
            applicationPhase: "application-phase-one",
            cancellationToken: ct);

        var report = await new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System).InspectAsync(sessionId, ct);

        report.IsHealthy.Should().BeFalse();
        report.Operations.Single(item => item.OperationId == prepared.Id)
            .SuggestedActionCodes.Should().Equal(
                CrossStoreSuggestedActions.ResumeIncompleteParticipants);
        report.Operations.Single(item => item.OperationId == active.Id)
            .SuggestedActionCodes.Should().Equal(
                CrossStoreSuggestedActions.ResumeIncompleteParticipants);
    }

    [Fact]
    public async Task Inspect_PreservesApplicationProvidedParticipantActionCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "failed.db"), pooling: false);
        var sessionId = SessionId.New();
        var operation = await StartAsync(store, sessionId, "failed", ct);
        const string participant = "search-index";
        await store.RecordParticipantAsync(
            operation.Id,
            new CrossStoreParticipantReceipt
            {
                Participant = participant,
                IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
                State = CrossStoreParticipantState.Failed,
                RecordedAt = DateTimeOffset.UtcNow,
                SuggestedActionCode = "rebuild-search-index"
            },
            ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            reasonCode: "participant-publication-failed",
            cancellationToken: ct);

        var report = await new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System).InspectAsync(sessionId, ct);

        var item = report.Operations.Should().ContainSingle().Subject;
        item.Health.Should().Be(CrossStoreOperationHealth.ReconciliationRequired);
        item.FailedParticipants.Should().Equal(participant);
        item.SuggestedActionCodes.Should().Equal("rebuild-search-index");
    }

    private static Task<CrossStoreOperation> StartAsync(
        ICrossStoreOperationStore store,
        SessionId sessionId,
        string key,
        CancellationToken cancellationToken)
    {
        var externalOperation = new ExternalOperationReference(
            "example-engine",
            Guid.CreateVersion7());
        return store.StartAsync(
            new StartCrossStoreOperationRequest(
                sessionId,
                externalOperation,
                "test",
                $"{externalOperation}:{key}",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
