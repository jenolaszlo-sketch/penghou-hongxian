using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SqliteCrossStoreOperationStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-sqlite-operation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndependentInstances_ReplayOneIdempotentOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteCrossStoreOperationStore(path, pooling: false);
        var second = new SqliteCrossStoreOperationStore(path, pooling: false);
        var externalOperation = new ExternalOperationReference("example-engine", Guid.CreateVersion7());
        var request = new StartCrossStoreOperationRequest(
            SessionId.New(),
            externalOperation,
            "test",
            $"{externalOperation}:test",
            DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(
            first.StartAsync(request, ct),
            second.StartAsync(request, ct));

        results[0].Should().BeEquivalentTo(results[1]);
        (await first.FindByExternalOperationAsync(externalOperation, ct))!.Id.Should().Be(results[0].Id);
    }

    [Fact]
    public async Task ConcurrentParticipantAppends_AreSerializedWithoutLostReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteCrossStoreOperationStore(path, pooling: false);
        var second = new SqliteCrossStoreOperationStore(path, pooling: false);
        var operation = await StartAsync(first, ct);
        var firstReceipt = Receipt(operation, "resource");
        var secondReceipt = Receipt(operation, "participant-b");

        await Task.WhenAll(
            first.RecordParticipantAsync(operation.Id, firstReceipt, ct),
            second.RecordParticipantAsync(operation.Id, secondReceipt, ct));

        var stored = await first.GetAsync(operation.Id, ct);
        stored!.Participants.Select(item => item.Participant)
            .Should().BeEquivalentTo("resource", "participant-b");
        stored.Version.Should().Be(3);
    }

    [Fact]
    public async Task ReceiptsAndTransitions_RemainImmutableAcrossReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var store = new SqliteCrossStoreOperationStore(path, pooling: false);
        var operation = await StartAsync(store, ct);
        var receipt = Receipt(operation, "resource");
        await store.RecordParticipantAsync(operation.Id, receipt, ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Active,
            DateTimeOffset.UtcNow,
            applicationPhase: "resource-committed",
            cancellationToken: ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Active,
            DateTimeOffset.UtcNow,
            applicationPhase: "participants-published",
            cancellationToken: ct);

        var reopened = new SqliteCrossStoreOperationStore(path, pooling: false);
        var stored = await reopened.GetAsync(operation.Id, ct);

        stored!.Participants.Should().ContainSingle().Which.Should().BeEquivalentTo(receipt);
        stored.Transitions.Select(item => item.State).Should().Equal(
            CrossStoreOperationState.Prepared,
            CrossStoreOperationState.Active,
            CrossStoreOperationState.Active);
        stored.Transitions.Select(item => item.ApplicationPhase).Should().Equal(
            null,
            "resource-committed",
            "participants-published");
        var conflict = () => reopened.RecordParticipantAsync(
            operation.Id,
            receipt with { ResultHash = "different" },
            ct);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different immutable receipt*");
    }

    [Fact]
    public async Task OperationMutations_EnqueueAndDispatchImmutableLedgerEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "evidence.db"), pooling: false);
        var operation = await StartAsync(store, ct);
        await store.RecordParticipantAsync(
            operation.Id,
            Receipt(operation, "search-index"),
            ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Active,
            DateTimeOffset.UtcNow,
            applicationPhase: "published",
            cancellationToken: ct);

        var pending = await store.ListPendingAsync(cancellationToken: ct);
        pending.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.OperationPrepared,
            SessionEventTypes.OperationParticipantRecorded,
            SessionEventTypes.OperationTransitioned);

        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"));
        var dispatched = await new SessionEvidenceOutboxDispatcher(store, events)
            .DispatchPendingAsync(cancellationToken: ct);

        dispatched.Should().Be(new SessionEvidenceDispatchResult(3, 3));
        (await store.ListPendingAsync(cancellationToken: ct)).Should().BeEmpty();
        var history = await events.ReadAsync(operation.SessionId, cancellationToken: ct);
        history.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.OperationPrepared,
            SessionEventTypes.OperationParticipantRecorded,
            SessionEventTypes.OperationTransitioned);
        history.Should().OnlyHaveUniqueItems(item => item.IdempotencyKey);
    }

    [Fact]
    public async Task EvidenceDispatch_RetryAfterAcknowledgementFailureDoesNotDuplicateLedgerEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "retry-evidence.db"), pooling: false);
        var operation = await StartAsync(store, ct);
        await using var events = new SimingSessionEventStore(
            Path.Combine(rootPath, "retry-sessions"));
        var failOnce = new FailOnceMarkOutbox(store);
        var dispatcher = new SessionEvidenceOutboxDispatcher(failOnce, events);

        var first = () => dispatcher.DispatchPendingAsync(cancellationToken: ct);
        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acknowledgement unavailable*");
        (await events.ReadAsync(operation.SessionId, cancellationToken: ct))
            .Should().ContainSingle();
        (await store.ListPendingAsync(cancellationToken: ct)).Should().ContainSingle();

        var replay = await dispatcher.DispatchPendingAsync(cancellationToken: ct);
        replay.Delivered.Should().Be(1);
        (await events.ReadAsync(operation.SessionId, cancellationToken: ct))
            .Should().ContainSingle();
        (await store.ListPendingAsync(cancellationToken: ct)).Should().BeEmpty();
    }

    private static Task<CrossStoreOperation> StartAsync(
        ICrossStoreOperationStore store,
        CancellationToken cancellationToken)
    {
        var externalOperation = new ExternalOperationReference("example-engine", Guid.CreateVersion7());
        return store.StartAsync(
            new StartCrossStoreOperationRequest(
                SessionId.New(),
                externalOperation,
                "test",
                $"{externalOperation}:test",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    [Fact]
    public async Task MutationBoundaries_RejectInvalidEnumsAndNonMonotonicTimes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteCrossStoreOperationStore(
            Path.Combine(rootPath, "validation.db"), pooling: false);
        var operation = await StartAsync(store, ct);

        var invalidState = () => store.TransitionAsync(
            operation.Id,
            (CrossStoreOperationState)999,
            operation.UpdatedAt,
            cancellationToken: ct);
        await invalidState.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var earlier = () => store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Active,
            operation.UpdatedAt.AddTicks(-1),
            cancellationToken: ct);
        await earlier.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var defaultId = () => store.GetAsync(default, ct);
        await defaultId.Should().ThrowAsync<ArgumentException>();
    }

    private static CrossStoreParticipantReceipt Receipt(
        CrossStoreOperation operation,
        string participant) => new()
        {
            Participant = participant,
            IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
            State = CrossStoreParticipantState.Applied,
            RecordedAt = DateTimeOffset.UtcNow,
            ResultHash = $"hash:{participant}"
        };

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private sealed class FailOnceMarkOutbox(ISessionEvidenceOutbox inner) :
        ISessionEvidenceOutbox
    {
        private int attempts;

        public Task<IReadOnlyList<SessionEvidenceOutboxRecord>> ListPendingAsync(
            int maximumCount = 100,
            CancellationToken cancellationToken = default) =>
            inner.ListPendingAsync(maximumCount, cancellationToken);

        public Task MarkDeliveredAsync(
            Guid receiptId,
            DateTimeOffset deliveredAt,
            CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException(
                    "evidence acknowledgement unavailable"))
                : inner.MarkDeliveredAsync(receiptId, deliveredAt, cancellationToken);
    }
}
