using FluentAssertions;
using Penghou.Hongxian;

#pragma warning disable xUnit1051 // Small deterministic in-memory test calls are cancellation-free.

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceProjectionExecutionTests
{
    private static readonly SessionId Session = new(
        Guid.Parse("00000000-0000-0000-0000-000000000201"));
    private static readonly SessionId OtherSession = new(
        Guid.Parse("00000000-0000-0000-0000-000000000202"));

    [Fact]
    public async Task Project_ConsumesVerifiedHistoryAndReplayIsIdempotent()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);
        var history = History(Events(2));

        var first = await projector.ProjectAsync(history);
        var retry = await projector.ProjectAsync(history);

        first.AppliedEventCount.Should().Be(2);
        first.AlreadyPresentEventCount.Should().Be(0);
        first.CheckpointCoveredEventCount.Should().Be(0);
        retry.AppliedEventCount.Should().Be(0);
        retry.AlreadyPresentEventCount.Should().Be(0);
        retry.CheckpointCoveredEventCount.Should().Be(2);
        (await writer.ListAsync(descriptor.ProjectionId)).Should().HaveCount(2);
        (await checkpoints.GetAsync(descriptor.ProjectionId))!.Position.Streams
            .Single().SessionLedgerHead.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Project_ResumesAfterCheckpointFailureWithoutDuplicateEffects()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var checkpoints = new FailOnceCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);
        var history = History(Events(2));

        var failed = () => projector.ProjectAsync(history);
        var exception = await failed.Should().ThrowAsync<ExperienceProjectionExecutionException>();
        exception.Which.Failure.Should().Be(ExperienceProjectionExecutionFailure.CheckpointCommit);
        exception.Which.ProjectionWritesMayHaveCommitted.Should().BeTrue();

        var resumed = await projector.ProjectAsync(history);
        resumed.AlreadyPresentEventCount.Should().Be(2);
        resumed.CheckpointCoveredEventCount.Should().Be(0);
        resumed.Checkpoint.Position.Streams.Single().SessionLedgerHead.Sequence.Should().Be(2);
        (await writer.ListAsync(descriptor.ProjectionId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Project_RecoversWhenWriterCommitsThenReportsFailure()
    {
        var descriptor = Descriptor();
        var inner = new InMemoryExperienceProjectionWriter();
        var writer = new CommitThenFailOnceWriter(inner);
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);
        var history = History(Events(2));

        var failed = () => projector.ProjectAsync(history);
        var exception = await failed.Should().ThrowAsync<ExperienceProjectionExecutionException>();
        exception.Which.Failure.Should().Be(ExperienceProjectionExecutionFailure.ProjectionWrite);
        exception.Which.ProjectionWritesMayHaveCommitted.Should().BeTrue();
        (await inner.ListAsync(descriptor.ProjectionId)).Should().ContainSingle();

        var resumed = await projector.ProjectAsync(history);
        resumed.AlreadyPresentEventCount.Should().Be(1);
        resumed.AppliedEventCount.Should().Be(1);
        (await inner.ListAsync(descriptor.ProjectionId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Project_AcceptsAHeadCapturedBeforeSourceGrowth()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);

        await projector.ProjectAsync(History(Events(2)));
        var grown = await projector.ProjectAsync(History(Events(3)));

        grown.AppliedEventCount.Should().Be(1);
        grown.Checkpoint.Position.Streams.Single().SessionLedgerHead.Sequence.Should().Be(3);
        (await writer.ListAsync(descriptor.ProjectionId)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Project_CoversMultipleIndependentlyOrderedSessionHeads()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);
        var first = History(Events(2));
        var otherEvents = Events(1)
            .Select(item => item with
            {
                SessionId = OtherSession,
                EventId = Guid.Parse("00000000-0000-0000-0000-000000000999")
            })
            .ToArray();
        var second = new VerifiedSessionHistory(
            OtherSession,
            new SessionLedgerHead("ledger/other", 1, otherEvents[0].Hash),
            otherEvents);

        var result = await projector.ProjectAsync([second, first]);

        result.AppliedEventCount.Should().Be(3);
        result.Checkpoint.Position.Streams.Select(item => item.SessionId)
            .Should().Equal(Session, OtherSession);
        (await writer.ListAsync(descriptor.ProjectionId)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Rebuild_DeletesAndReplaysEquivalentEffects()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var projector = new ExperienceProjectionProjector(descriptor, writer, checkpoints);
        var history = History(Events(2));

        await projector.ProjectAsync(history);
        var before = await writer.ListAsync(descriptor.ProjectionId);
        var rebuilt = await projector.RebuildAsync([history]);
        var after = await writer.ListAsync(descriptor.ProjectionId);

        after.Should().Equal(before);
        rebuilt.Checkpoint.Position.Digest.Should().Be(
            new ExperienceProjectionPosition([
            new SessionEvidencePosition(Session, history.VerifiedHead)]).Digest);
    }

    [Fact]
    public async Task Rebuild_RetryHealsADeleteThatCommittedBeforeFailure()
    {
        var descriptor = Descriptor();
        var inner = new InMemoryExperienceProjectionWriter();
        var checkpoints = new InMemoryExperienceProjectionCheckpointStore();
        var initial = new ExperienceProjectionProjector(descriptor, inner, checkpoints);
        var history = History(Events(2));
        await initial.ProjectAsync(history);
        var failing = new ExperienceProjectionProjector(
            descriptor,
            new DeleteThenFailOnceWriter(inner),
            checkpoints);

        var failed = () => failing.RebuildAsync([history]);
        var exception = await failed.Should().ThrowAsync<ExperienceProjectionExecutionException>();
        exception.Which.Failure.Should().Be(ExperienceProjectionExecutionFailure.CheckpointReset);
        exception.Which.ProjectionWritesMayHaveCommitted.Should().BeTrue();
        exception.Which.IntendedPosition.Should().NotBeNull();

        await failing.RebuildAsync([history]);
        (await inner.ListAsync(descriptor.ProjectionId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Project_RejectsGapsAndBrokenHashLinks()
    {
        var descriptor = Descriptor();
        var projector = new ExperienceProjectionProjector(
            descriptor,
            new InMemoryExperienceProjectionWriter(),
            new InMemoryExperienceProjectionCheckpointStore());
        var broken = History([
            Event(1, "h1", previousHash: null),
            Event(3, "h3", previousHash: "h1")
        ], headSequence: 2, headHash: "h3");

        var project = () => projector.ProjectAsync(broken);
        var exception = await project.Should().ThrowAsync<ExperienceProjectionExecutionException>();
        exception.Which.Failure.Should().Be(ExperienceProjectionExecutionFailure.InvalidHistory);
    }

    [Fact]
    public async Task Project_RejectsUnsupportedEnvelopeBeforeWriting()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var projector = new ExperienceProjectionProjector(
            descriptor,
            writer,
            new InMemoryExperienceProjectionCheckpointStore());
        var unsupported = Event(1, "h1", previousHash: null) with
        {
            SchemaVersion = SessionEventEnvelopeSchema.CurrentVersion + 1
        };

        var project = () => projector.ProjectAsync(History([unsupported]));
        var exception = await project.Should().ThrowAsync<ExperienceProjectionExecutionException>();

        exception.Which.Failure.Should().Be(ExperienceProjectionExecutionFailure.UnsupportedHistory);
        (await writer.ListAsync(descriptor.ProjectionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Writer_RejectsChangedContentForAnExistingEffectIdentity()
    {
        var descriptor = Descriptor();
        var writer = new InMemoryExperienceProjectionWriter();
        var original = Event(1, "h1", previousHash: null);
        var changed = original with { Hash = "different" };

        (await writer.ApplyAsync(descriptor, original))
            .Should().Be(ExperienceProjectionWriteOutcome.Applied);
        (await writer.ApplyAsync(descriptor, original))
            .Should().Be(ExperienceProjectionWriteOutcome.AlreadyPresent);
        var conflicting = () => writer.ApplyAsync(descriptor, changed);

        await conflicting.Should().ThrowAsync<ExperienceProjectionEffectConflictException>();
    }

    private static ExperienceProjectionDescriptor Descriptor() => new(
        new ExperienceProjectionId(Guid.Parse("00000000-0000-0000-0000-000000000301")),
        "reference/memory",
        "session-experience",
        1,
        "policy:v1");

    private static VerifiedSessionHistory History(
        IReadOnlyList<SessionEvent> events,
        long? headSequence = null,
        string? headHash = null) => new(
        Session,
        new SessionLedgerHead("ledger/memory", headSequence ?? events.Count, headHash ?? events[^1].Hash),
        events);

    private static IReadOnlyList<SessionEvent> Events(int count) =>
        Enumerable.Range(1, count)
            .Select(index => Event(index, $"h{index}", index == 1 ? null : $"h{index - 1}"))
            .ToArray();

    private static SessionEvent Event(long sequence, string hash, string? previousHash) => new()
    {
        SchemaVersion = 2,
        Sequence = sequence,
        EventId = Guid.Parse($"00000000-0000-0000-0000-{sequence + 400:D12}"),
        SessionId = Session,
        Participant = SessionParticipantAttribution.System("test"),
        EventType = SessionEventTypes.UserMessage,
        OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        CommittedAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
        PayloadSensitivity = SessionPayloadSensitivity.Internal,
        PayloadRetention = SessionPayloadRetention.Omit,
        PreviousHash = previousHash,
        Hash = hash
    };

    private sealed class FailOnceCheckpointStore : IExperienceProjectionCheckpointStore,
        IExperienceProjectionCheckpointResetStore
    {
        private readonly InMemoryExperienceProjectionCheckpointStore inner = new();
        private int failed;

        public Task<ExperienceProjectionCheckpoint?> GetAsync(
            ExperienceProjectionId projectionId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(projectionId, cancellationToken);

        public Task<ExperienceProjectionCheckpoint> AdvanceAsync(
            AdvanceExperienceProjectionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
                throw new InvalidOperationException("simulated checkpoint interruption");
            return inner.AdvanceAsync(request, cancellationToken);
        }

        public Task ResetAsync(
            ExperienceProjectionId projectionId,
            CancellationToken cancellationToken = default) =>
            inner.ResetAsync(projectionId, cancellationToken);
    }

    private sealed class CommitThenFailOnceWriter(InMemoryExperienceProjectionWriter inner) :
        IExperienceProjectionWriter
    {
        private int failed;

        public async Task<ExperienceProjectionWriteOutcome> ApplyAsync(
            ExperienceProjectionDescriptor descriptor,
            SessionEvent sessionEvent,
            CancellationToken cancellationToken = default)
        {
            var outcome = await inner.ApplyAsync(descriptor, sessionEvent, cancellationToken);
            if (Interlocked.Exchange(ref failed, 1) == 0)
                throw new InvalidOperationException("simulated post-commit write failure");
            return outcome;
        }

        public Task DeleteAsync(
            ExperienceProjectionDescriptor descriptor,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(descriptor, cancellationToken);
    }

    private sealed class DeleteThenFailOnceWriter(InMemoryExperienceProjectionWriter inner) :
        IExperienceProjectionWriter
    {
        private int failed;

        public Task<ExperienceProjectionWriteOutcome> ApplyAsync(
            ExperienceProjectionDescriptor descriptor,
            SessionEvent sessionEvent,
            CancellationToken cancellationToken = default) =>
            inner.ApplyAsync(descriptor, sessionEvent, cancellationToken);

        public async Task DeleteAsync(
            ExperienceProjectionDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(descriptor, cancellationToken);
            if (Interlocked.Exchange(ref failed, 1) == 0)
                throw new InvalidOperationException("simulated post-delete failure");
        }
    }
}
