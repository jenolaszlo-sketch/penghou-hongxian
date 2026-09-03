using FluentAssertions;
using Penghou.Hongxian;

namespace Penghou.Hongxian.Tests;

/// <summary>
/// Contract tests deliberately depend only on Hongxian interfaces. A provider
/// fixture can be substituted without changing these behavioral guarantees.
/// </summary>
public sealed class ProviderConformanceTests
{
    [Fact]
    public async Task EventStore_AppendReadPageAndVerifyAreOrderedAndRetrySafe()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        var request = Request(session.Id, SessionEventTypes.UserMessage) with
        {
            IdempotencyKey = "event-conformance-1"
        };

        var delivery = await fixture.EventDelivery.AppendWithDeliveryAsync(request, ct);
        var first = delivery.Event;
        delivery.ProjectionDelivery.Outcome.Should().Be(SessionProjectionDeliveryOutcome.Applied);
        var replay = await fixture.Events.AppendAsync(request with
        {
            OccurredAt = request.OccurredAt.AddMinutes(1)
        }, ct);
        var second = await fixture.Events.AppendAsync(
            Request(session.Id, SessionEventTypes.ExecutionStarted) with
            {
                CausationId = first.EventId
            }, ct);

        replay.Should().BeEquivalentTo(first);
        second.Sequence.Should().Be(first.Sequence + 1);
        second.PreviousHash.Should().Be(first.Hash);

        var page = await fixture.Events.ReadPageAsync(
            new SessionEventPageRequest(session.Id, Limit: 1), ct);
        page.Events.Should().ContainSingle().Which.Should().BeEquivalentTo(first);
        page.HasMore.Should().BeTrue();
        page.NextSequence.Should().Be(first.Sequence);
        (await fixture.Events.ReadAsync(session.Id, cancellationToken: ct)).Should().HaveCount(2);
        (await fixture.Events.VerifyChainAsync(session.Id, ct)).Should().BeEquivalentTo(second);
    }

    [Fact]
    public async Task ProjectionStore_AppliesContiguousEventsAndRebuildsVerifiedHistory()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        var first = await fixture.Events.AppendAsync(
            Request(session.Id, SessionEventTypes.InputRequested), ct);
        var second = await fixture.Events.AppendAsync(
            Request(session.Id, SessionEventTypes.InputProvided) with
            {
                CausationId = first.EventId
            }, ct);
        var history = await fixture.Events.ReadVerifiedHistoryAsync(session.Id, ct);

        var snapshot = await fixture.Projections.GetAsync(session.Id, ct);
        snapshot.Should().NotBeNull();
        snapshot!.AppliedSequence.Should().Be(second.Sequence);
        snapshot.State.PendingInputs.Should().BeEmpty();

        var rebuilt = await fixture.Projections.RebuildAsync(history, ct);
        rebuilt.Should().NotBeNull();
        rebuilt!.HeadHash.Should().Be(history.VerifiedHead.Hash);
        rebuilt.State.TotalEvents.Should().Be(2);
    }

    [Fact]
    public async Task Catalog_ProvidesSessionRoutingAndOptimisticRevisionContract()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        var external = new ExternalOperationReference("conformance-engine", Guid.CreateVersion7());

        var attached = await fixture.Catalog.AttachExternalOperationAsync(session.Id, external, ct);
        attached.ExternalOperations.Should().ContainSingle().Which.Should().Be(external);
        (await fixture.Catalog.FindByExternalOperationAsync(external, ct))!.Id.Should().Be(session.Id);
        (await fixture.Catalog.ListAsync(ct)).Should().ContainSingle().Which.Id.Should().Be(session.Id);

        var updated = await fixture.Catalog.UpdateRevisionAsync(
            session.Id, expectedRevision: null, replacementRevision: "revision-1", cancellationToken: ct);
        updated.CurrentRevision.Should().Be("revision-1");
        var conflict = () => fixture.Catalog.UpdateRevisionAsync(
            session.Id, expectedRevision: null, replacementRevision: "revision-2", cancellationToken: ct);
        await conflict.Should().ThrowAsync<SessionRevisionConflictException>();
    }

    [Fact]
    public async Task DecisionLease_ExposesOwnershipAndFencingContract()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        await using var lease = await fixture.Leases.AcquireAsync(
            session.Id, Guid.CreateVersion7(), ct);

        lease.SessionId.Should().Be(session.Id);
        lease.OperationId.Should().NotBe(Guid.Empty);
        lease.FencingToken.Should().BePositive();
        lease.ExpiresAt.Should().BeAfter(lease.AcquiredAt);
        lease.LeaseLost.IsCancellationRequested.Should().BeFalse();
        await lease.AssertOwnershipAsync(ct);
    }

    [Fact]
    public async Task LifecycleOutbox_IsDurableAndDispatcherIsIdempotent()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        var pending = await fixture.CatalogOutbox.ListPendingAsync(cancellationToken: ct);
        pending.Should().ContainSingle();
        pending[0].EventType.Should().Be(SessionEventTypes.SessionCreated);

        var dispatch = await fixture.CatalogDispatcher.DispatchPendingAsync(cancellationToken: ct);
        dispatch.Should().Be(new SessionEvidenceDispatchResult(1, 1));
        (await fixture.CatalogOutbox.ListPendingAsync(cancellationToken: ct)).Should().BeEmpty();
        (await fixture.Events.ReadAsync(session.Id, cancellationToken: ct)).Should().ContainSingle()
            .Which.EventType.Should().Be(SessionEventTypes.SessionCreated);
        (await fixture.CatalogDispatcher.DispatchPendingAsync(cancellationToken: ct))
            .Should().Be(new SessionEvidenceDispatchResult(0, 0));
    }

    [Fact]
    public async Task OperationStore_TracksParticipantsTransitionsAndEvidence()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        var external = new ExternalOperationReference("conformance-engine", Guid.CreateVersion7());
        var started = DateTimeOffset.UtcNow;
        var operation = await fixture.Operations.StartAsync(new StartCrossStoreOperationRequest(
            session.Id, external, "batch", "operation-conformance-1", started), ct);
        var receipt = new CrossStoreParticipantReceipt
        {
            Participant = "resource",
            IdempotencyKey = operation.ParticipantIdempotencyKey("resource"),
            State = CrossStoreParticipantState.Applied,
            RecordedAt = started.AddTicks(1),
            ResultHash = "result-hash"
        };
        await fixture.Operations.RecordParticipantAsync(operation.Id, receipt, ct);
        var transitioned = await fixture.Operations.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Completed,
            started.AddTicks(2),
            applicationPhase: "done",
            cancellationToken: ct);

        transitioned.State.Should().Be(CrossStoreOperationState.Completed);
        transitioned.Participants.Should().ContainSingle().Which.Should().BeEquivalentTo(receipt);
        transitioned.Transitions.Should().HaveCount(2);
        (await fixture.Operations.FindByExternalOperationAsync(external, ct))!.Id.Should().Be(operation.Id);
    }

    [Fact]
    public async Task Inspection_ReturnsTypedHealthyContractAfterEvidenceDelivery()
    {
        using var fixture = new ProviderConformanceFixture();
        var ct = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(cancellationToken: ct);
        await fixture.CatalogDispatcher.DispatchPendingAsync(cancellationToken: ct);

        var result = await fixture.Inspection.InspectAsync(session.Id, ct);

        result.Health.Should().Be(SessionConsistencyHealth.Healthy);
        result.Ledger.Health.Should().Be(SessionLedgerAuditHealth.Verified);
        result.CatalogEntryExists.Should().BeTrue();
        result.ProjectionIsLagging.Should().BeFalse();
        result.PendingEvidenceCount.Should().Be(0);
        result.IncompleteOperationCount.Should().Be(0);
    }

    private static SessionEventRequest Request(SessionId sessionId, string eventType) =>
        new(
            sessionId,
            SessionParticipantAttribution.System("provider-conformance", "tests"),
            eventType,
            DateTimeOffset.UtcNow);
}
