using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceProjectionCheckpointTests
{
    private static readonly SessionId FirstSession =
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    private static readonly SessionId SecondSession =
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"));

    [Fact]
    public void Position_IsOrderIndependentAndMatchesGoldenDigest()
    {
        var first = Stream(FirstSession, "ledger-a", 1, "aa");
        var second = Stream(SecondSession, "ledger-b", 12, "bb");

        var forward = new ExperienceProjectionPosition([first, second]);
        var reverse = new ExperienceProjectionPosition([second, first]);

        forward.Streams.Should().Equal(first, second);
        reverse.Streams.Should().Equal(first, second);
        reverse.Digest.Should().Be(forward.Digest);
        forward.Digest.Should().Be(
            "sha256:penghou-hongxian-experience-position:v1:" +
            "9765e4405d13a429bae81a87e82d818dfe3de3badf60f617fd21d8d357c74fe1");
        new ExperienceProjectionPosition([second, first], forward.Digest)
            .Digest.Should().Be(forward.Digest);

        var invalidDigest = () => new ExperienceProjectionPosition([first], "sha256:wrong");
        invalidDigest.Should().Throw<ExperienceProjectionPositionValidationException>();
    }

    [Fact]
    public void Position_RejectsEmptyNullDuplicateAndOversizedInputs()
    {
        var stream = Stream(FirstSession, "ledger-a", 1, "aa");
        var empty = () => new ExperienceProjectionPosition([]);
        var containsNull = () => new ExperienceProjectionPosition([null!]);
        var duplicate = () => new ExperienceProjectionPosition([stream, stream]);
        var oversized = () => new ExperienceProjectionPosition(
            Enumerable.Range(1, SessionContractLimits.ProjectionStreamCount + 1)
                .Select(index => Stream(
                    new SessionId(GuidFromIndex(index)),
                    $"ledger-{index}",
                    index,
                    $"hash-{index}")));

        empty.Should().Throw<ExperienceProjectionPositionValidationException>();
        containsNull.Should().Throw<ExperienceProjectionPositionValidationException>();
        duplicate.Should().Throw<ExperienceProjectionPositionValidationException>();
        oversized.Should().Throw<ExperienceProjectionPositionValidationException>();
    }

    [Fact]
    public void Position_LengthPrefixesPreventDelimiterBoundaryAmbiguity()
    {
        var first = new ExperienceProjectionPosition([
            Stream(FirstSession, "a|0|b", 0, "c")
        ]);
        var second = new ExperienceProjectionPosition([
            Stream(FirstSession, "a", 0, "b|0|c")
        ]);

        first.Digest.Should().NotBe(second.Digest);
    }

    [Fact]
    public void ProjectionId_RoundTripsAsUuidString()
    {
        var id = ExperienceProjectionId.New();

        ExperienceProjectionId.Parse(id.ToString()).Should().Be(id);
        ExperienceProjectionId.TryParse(id.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
        JsonSerializer.Deserialize<ExperienceProjectionId>(
            JsonSerializer.Serialize(id)).Should().Be(id);
        ExperienceProjectionId.TryParse(Guid.Empty.ToString(), out _).Should().BeFalse();
    }

    [Fact]
    public void Descriptor_RequiresPortableBoundedIdentityAndPositiveSchema()
    {
        var id = ExperienceProjectionId.New();
        var valid = new ExperienceProjectionDescriptor(
            id,
            "latticedb/local",
            "session-experience",
            1,
            "policy:v1");

        valid.ProjectionId.Should().Be(id);
        var defaultId = () => new ExperienceProjectionDescriptor(
            default,
            "provider",
            "projector",
            1,
            "v1");
        var uppercase = () => new ExperienceProjectionDescriptor(
            id,
            "LatticeDb",
            "projector",
            1,
            "v1");
        var invalidSchema = () => new ExperienceProjectionDescriptor(
            id,
            "provider",
            "projector",
            0,
            "v1");

        defaultId.Should().Throw<ArgumentException>();
        uppercase.Should().Throw<ArgumentException>();
        invalidSchema.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Rules_CreateAdvanceAddStreamAndRetryIdempotently()
    {
        var descriptor = Descriptor();
        var first = Position(Stream(FirstSession, "ledger-a", 1, "a1"));
        var created = ExperienceProjectionCheckpointRules.Validate(
            null,
            new AdvanceExperienceProjectionRequest(descriptor, first, ExpectedVersion: 0));

        created.Version.Should().Be(1);
        var advancedPosition = Position(
            Stream(FirstSession, "ledger-a", 2, "a2"),
            Stream(SecondSession, "ledger-b", 1, "b1"));
        var advanced = ExperienceProjectionCheckpointRules.Validate(
            created,
            new AdvanceExperienceProjectionRequest(
                descriptor,
                advancedPosition,
                ExpectedVersion: 1));

        advanced.Version.Should().Be(2);
        advanced.Position.Should().BeSameAs(advancedPosition);
        ExperienceProjectionCheckpointRules.Validate(
            advanced,
            new AdvanceExperienceProjectionRequest(
                descriptor,
                advancedPosition,
                ExpectedVersion: 1)).Should().BeSameAs(advanced);
    }

    [Fact]
    public void Rules_RejectDescriptorVersionAndHistoryConflicts()
    {
        var descriptor = Descriptor();
        var current = new ExperienceProjectionCheckpoint(
            descriptor,
            Position(
                Stream(FirstSession, "ledger-a", 2, "a2"),
                Stream(SecondSession, "ledger-b", 1, "b1")),
            version: 2);

        AssertFailure(
            current,
            new ExperienceProjectionDescriptor(
                descriptor.ProjectionId,
                descriptor.Provider,
                descriptor.ProjectorName,
                descriptor.SchemaVersion,
                "policy:v2"),
            current.Position,
            2,
            ExperienceProjectionCheckpointFailure.DescriptorMismatch);
        AssertFailure(
            current,
            descriptor,
            Position(Stream(FirstSession, "ledger-a", 2, "a2")),
            2,
            ExperienceProjectionCheckpointFailure.PositionRegression);
        AssertFailure(
            current,
            descriptor,
            Position(
                Stream(FirstSession, "ledger-a", 1, "a1"),
                Stream(SecondSession, "ledger-b", 1, "b1")),
            2,
            ExperienceProjectionCheckpointFailure.StreamRegression);
        AssertFailure(
            current,
            descriptor,
            Position(
                Stream(FirstSession, "replacement", 3, "a3"),
                Stream(SecondSession, "ledger-b", 1, "b1")),
            2,
            ExperienceProjectionCheckpointFailure.LedgerIdentityConflict);
        AssertFailure(
            current,
            descriptor,
            Position(
                Stream(FirstSession, "ledger-a", 2, "different"),
                Stream(SecondSession, "ledger-b", 1, "b1")),
            2,
            ExperienceProjectionCheckpointFailure.SameSequenceDifferentHash);
        AssertFailure(
            current,
            descriptor,
            Position(
                Stream(FirstSession, "ledger-a", 3, "a3"),
                Stream(SecondSession, "ledger-b", 1, "b1")),
            1,
            ExperienceProjectionCheckpointFailure.VersionConflict);
    }

    [Fact]
    public void Rules_RejectNegativeAndExhaustedVersions()
    {
        var descriptor = Descriptor();
        var first = Position(Stream(FirstSession, "ledger-a", 1, "a1"));

        var negative = () => ExperienceProjectionCheckpointRules.Validate(
            null,
            new AdvanceExperienceProjectionRequest(descriptor, first, ExpectedVersion: -1));
        negative.Should().Throw<ExperienceProjectionCheckpointException>()
            .Which.Failure.Should().Be(ExperienceProjectionCheckpointFailure.InvalidRequest);

        var exhausted = new ExperienceProjectionCheckpoint(
            descriptor,
            first,
            long.MaxValue);
        var advance = () => ExperienceProjectionCheckpointRules.Validate(
            exhausted,
            new AdvanceExperienceProjectionRequest(
                descriptor,
                Position(Stream(FirstSession, "ledger-a", 2, "a2")),
                ExpectedVersion: long.MaxValue));

        advance.Should().Throw<ExperienceProjectionCheckpointException>()
            .Which.Failure.Should().Be(ExperienceProjectionCheckpointFailure.VersionExhausted);
    }

    [Fact]
    public async Task InMemoryStore_IsConcurrencySafeAndHonorsCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryExperienceProjectionCheckpointStore();
        var descriptor = Descriptor();
        var request = new AdvanceExperienceProjectionRequest(
            descriptor,
            Position(Stream(FirstSession, "ledger-a", 1, "a1")),
            ExpectedVersion: 0);

        var writes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.AdvanceAsync(request, ct)));

        writes.Should().OnlyContain(checkpoint => checkpoint.Version == 1);
        (await store.GetAsync(descriptor.ProjectionId, ct)).Should().Be(writes[0]);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await cancellation.CancelAsync();
        var cancelledRead = () => store.GetAsync(
            descriptor.ProjectionId,
            cancellation.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();
    }

    private static void AssertFailure(
        ExperienceProjectionCheckpoint current,
        ExperienceProjectionDescriptor descriptor,
        ExperienceProjectionPosition position,
        long expectedVersion,
        ExperienceProjectionCheckpointFailure expectedFailure)
    {
        var validate = () => ExperienceProjectionCheckpointRules.Validate(
            current,
            new AdvanceExperienceProjectionRequest(
                descriptor,
                position,
                expectedVersion));

        validate.Should().Throw<ExperienceProjectionCheckpointException>()
            .Which.Failure.Should().Be(expectedFailure);
    }

    private static ExperienceProjectionDescriptor Descriptor() =>
        new(
            new ExperienceProjectionId(
                Guid.Parse("00000000-0000-0000-0000-000000000100")),
            "reference/local",
            "session-experience",
            1,
            "policy:v1");

    private static ExperienceProjectionPosition Position(
        params SessionEvidencePosition[] streams) =>
        new(streams);

    private static SessionEvidencePosition Stream(
        SessionId sessionId,
        string ledgerIdentity,
        long sequence,
        string hash) =>
        new(sessionId, new SessionLedgerHead(ledgerIdentity, sequence, hash));

    private static Guid GuidFromIndex(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[12..], index);
        return new Guid(bytes);
    }
}
