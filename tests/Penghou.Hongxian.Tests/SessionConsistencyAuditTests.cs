using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SessionConsistencyAuditTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-consistency-audit-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Audit_ExplainsPendingEvidenceLeaseAndReconciliationState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stores = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = rootPath,
            Pooling = false
        });
        var session = await stores.SessionStore.CreateAsync(
            "example",
            "resource/1",
            cancellationToken: ct);

        var pendingCreation = await stores.ConsistencyAudit.InspectAsync(session.Id, ct);
        pendingCreation.Health.Should().Be(SessionConsistencyHealth.Warning);
        pendingCreation.CatalogVersion.Should().Be(0);
        pendingCreation.PendingEvidenceCount.Should().Be(1);
        pendingCreation.Ledger.Health.Should().Be(SessionLedgerAuditHealth.Missing);
        File.Exists(stores.Events.GetLedgerPath(session.Id)).Should().BeFalse();

        await stores.CatalogEvidence.DispatchPendingAsync(cancellationToken: ct);
        var healthy = await stores.ConsistencyAudit.InspectAsync(session.Id, ct);
        healthy.Health.Should().Be(SessionConsistencyHealth.Healthy);
        healthy.ProjectionIsLagging.Should().BeFalse();
        healthy.PendingEvidenceCount.Should().Be(0);

        await using var lease = await stores.DecisionLeases.AcquireAsync(
            session.Id,
            Guid.CreateVersion7(),
            ct);
        await stores.CatalogEvidence.DispatchPendingAsync(cancellationToken: ct);
        var coordinated = await stores.ConsistencyAudit.InspectAsync(session.Id, ct);
        coordinated.DecisionLease.Should().NotBeNull();
        coordinated.DecisionLease!.FencingToken.Should().Be(lease.FencingToken);
        coordinated.DecisionLease.IsExpired.Should().BeFalse();
        coordinated.Health.Should().Be(SessionConsistencyHealth.Healthy);

        var startedAt = DateTimeOffset.UtcNow;
        var operation = await stores.OperationStore.StartAsync(
            new StartCrossStoreOperationRequest(
                session.Id,
                new ExternalOperationReference("zhinu", "run/1"),
                "generation",
                "operation:run/1",
                startedAt),
            ct);
        await stores.OperationEvidence.DispatchPendingAsync(cancellationToken: ct);
        var incomplete = await stores.ConsistencyAudit.InspectAsync(session.Id, ct);
        incomplete.Health.Should().Be(SessionConsistencyHealth.Warning);
        incomplete.IncompleteOperationCount.Should().Be(1);

        await stores.OperationStore.RecordParticipantAsync(
            operation.Id,
            new CrossStoreParticipantReceipt
            {
                Participant = "workspace",
                IdempotencyKey = operation.ParticipantIdempotencyKey("workspace"),
                State = CrossStoreParticipantState.Failed,
                RecordedAt = startedAt.AddSeconds(1),
                SuggestedActionCode = "reconcile-forward"
            },
            ct);
        var participantFailure = await stores.ConsistencyAudit.InspectAsync(
            session.Id,
            ct);
        participantFailure.Health.Should().Be(
            SessionConsistencyHealth.ReconciliationRequired);
        participantFailure.FailedParticipantCount.Should().Be(1);

        await stores.OperationStore.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            startedAt.AddSeconds(2),
            reasonCode: "participant-diverged",
            cancellationToken: ct);
        var reconciliation = await stores.ConsistencyAudit.InspectAsync(session.Id, ct);
        reconciliation.Health.Should().Be(
            SessionConsistencyHealth.ReconciliationRequired);
        reconciliation.Operations.Should().ContainSingle(item =>
            item.StatusReasonCode == "participant-diverged");
    }

    [Fact]
    public async Task Audit_RejectsAmbiguousOutboxSourceNames()
    {
        await using var stores = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = rootPath,
            Pooling = false
        });
        var create = () => new SessionConsistencyAuditService(
            stores.Events,
            stores.Projections,
            stores.Catalog,
            stores.Operations,
            [
                new SessionEvidenceOutboxAuditSource("same", stores.Catalog),
                new SessionEvidenceOutboxAuditSource("same", stores.Operations)
            ]);

        create.Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
