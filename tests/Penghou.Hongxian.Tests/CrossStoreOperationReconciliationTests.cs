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
    public async Task Inspect_ReportsForwardRecoveryByCommitBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "operations.db"), pooling: false);
        var sessionId = SessionId.New();
        var prepared = await StartAsync(store, sessionId, "prepared", ct);
        var committed = await StartAsync(store, sessionId, "committed", ct);
        await store.TransitionAsync(
            committed.Id,
            CrossStoreOperationState.RevisionCommitted,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        var published = await StartAsync(store, sessionId, "published", ct);
        await store.TransitionAsync(
            published.Id,
            CrossStoreOperationState.RevisionCommitted,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        await store.TransitionAsync(
            published.Id,
            CrossStoreOperationState.Published,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);

        var report = await new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System).InspectAsync(sessionId, ct);

        report.IsHealthy.Should().BeFalse();
        report.Operations.Single(item => item.OperationId == prepared.Id)
            .OperatorAction.Should().Contain("Resume the external execution");
        report.Operations.Single(item => item.OperationId == committed.Id)
            .OperatorAction.Should().Contain("Do not roll back");
        report.Operations.Single(item => item.OperationId == published.Id)
            .OperatorAction.Should().Contain("final checkpoint");
    }

    [Fact]
    public async Task Inspect_UsesApplicationProvidedParticipantRecoveryInstruction()
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
                RecoveryAction = "Rebuild the index from the accepted revision."
            },
            ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            "Index publication failed.",
            ct);

        var report = await new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System).InspectAsync(sessionId, ct);

        var item = report.Operations.Should().ContainSingle().Subject;
        item.Health.Should().Be(CrossStoreOperationHealth.ReconciliationRequired);
        item.FailedParticipants.Should().Equal(participant);
        item.OperatorAction.Should().Be("Rebuild the index from the accepted revision.");
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
