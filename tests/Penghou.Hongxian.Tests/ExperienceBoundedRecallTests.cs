using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Hongxian.LatticeDb;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceBoundedRecallTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "hongxian-recall-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Executor_ReturnsRankedBoundedResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);

        var request = RecallRequest(projection, maximumItems: 2);
        var result = await ExperienceBoundedRecall.ExecuteAsync(provider, request, ct);

        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        result.IsTruncated.Should().BeTrue();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
        result.Items.Should().HaveCount(2);
        result.Items.Select(item => item.Rank).Should().Equal(1, 2);
        result.Items.Should().OnlyContain(item =>
            item.ScoreSemantics == ExperienceRecallScoreSemantics.ProviderRank);
        result.EstimatedTokensTotal.Should()
            .Be(result.Items.Sum(item => item.EstimatedTokens));
        result.QueryFingerprint.Should().Be(ExperienceRecallFingerprint.Compute(request));
        result.Policy.Should().Be(request.Policy);
        result.Freshness.Should().Be(ExperienceProviderFreshness.Current);
    }

    [Fact]
    public async Task Executor_AppliesKindAndEvidenceFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);
        var note = Entity(projection, "note:1", "durable note", kind: "note");
        (await provider.UpsertEntityAsync(note, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var kindFiltered = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, entityKinds: ["note"]), ct);
        kindFiltered.Items.Should().ContainSingle().Which.Record.Kind.Should().Be("note");

        var evidenceFiltered = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, minimumEvidenceReferences: 2), ct);
        evidenceFiltered.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_EnforcesTokenItemAndByteBudgets()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);

        var tokenCapped = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, maximumTokens: 1), ct);
        tokenCapped.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        tokenCapped.Items.Should().BeEmpty();
        tokenCapped.IsTruncated.Should().BeTrue();
        tokenCapped.Diagnostic.Should().NotBeNullOrWhiteSpace();

        var byteCapped = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, maximumBytes: 1), ct);
        byteCapped.Items.Should().BeEmpty();
        byteCapped.IsTruncated.Should().BeTrue();
        byteCapped.Diagnostic.Should().Contain("portable");
    }

    [Fact]
    public async Task Executor_ReturnsUnsupportedForAsOfWithoutFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(memory, projection, ct);
        var asOf = new ExperienceProjectionPosition([
            new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
        ]);

        var result = await ExperienceBoundedRecall.ExecuteAsync(
            memory, RecallRequest(projection, asOf: asOf), ct);

        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Unsupported);
        result.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.AsOf);
        result.Items.Should().BeEmpty();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Executor_ReturnsUnsupportedWithoutLexicalCapability()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new LexicalLessReader();
        var result = await ExperienceBoundedRecall.ExecuteAsync(
            reader, RecallRequest(Descriptor("stub")), ct);

        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Unsupported);
        result.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.LexicalSearch);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_HonorsFreshnessRequirement()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(inner, projection, ct);
        var stale = new StaleReader(inner);

        var rejected = await ExperienceBoundedRecall.ExecuteAsync(
            stale, RecallRequest(projection), ct);
        rejected.Completion.Should().Be(ExperienceBoundedRecallCompletion.Unsupported);
        rejected.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.None);
        rejected.Items.Should().BeEmpty();

        var accepted = await ExperienceBoundedRecall.ExecuteAsync(
            stale, RecallRequest(projection, freshness: ExperienceRecallFreshnessRequirement.Any), ct);
        accepted.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        accepted.Items.Should().NotBeEmpty();
        accepted.Freshness.Should().Be(ExperienceProviderFreshness.Stale);
        accepted.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Executor_RunsAgainstLatticeDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new LatticeDbExperienceProvider(
            new LatticeDbExperienceProviderOptions
            {
                Location = LatticeDbLocationKind.Memory,
                Lock = false
            });
        var projection = Descriptor("latticedb");
        await SeedAsync(provider, projection, ct);

        var result = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection), ct);

        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        result.ProviderName.Should().Be("latticedb");
        result.Items.Should().HaveCount(3);
        result.Items.Select(item => item.Rank).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Recall_IsDeterministicAcrossRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(memory, projection, ct);
        var request = RecallRequest(projection);

        var first = await ExperienceBoundedRecall.ExecuteAsync(memory, request, ct);
        var second = await ExperienceBoundedRecall.ExecuteAsync(memory, request, ct);

        second.Items.Select(item => item.Record.Id)
            .Should().Equal(first.Items.Select(item => item.Record.Id));
        second.QueryFingerprint.Should().Be(first.QueryFingerprint);
    }

    [Fact]
    public void Fingerprint_IsStableAndOrderIndependent()
    {
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "memory", "conformance", 1, "policy-1");
        var policy = new ExperienceRetrievalPolicy("lexical-baseline", "policy-1");
        var first = new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "durable", ["task", "note"]);
        var reordered = new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "durable", ["note", "task"]);
        var changed = new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "fragile", ["task", "note"]);

        var fingerprint = ExperienceRecallFingerprint.Compute(first);
        fingerprint.Should().StartWith(ExperienceRecallFingerprint.ContractVersion);
        ExperienceRecallFingerprint.Compute(reordered).Should().Be(fingerprint);
        ExperienceRecallFingerprint.Compute(changed).Should().NotBe(fingerprint);
    }

    [Fact]
    public void EstimateTokens_UsesDocumentedModel()
    {
        ExperienceBoundedRecall.TokenEstimateModel.Should().NotBeNullOrWhiteSpace();
        ExperienceBoundedRecall.EstimateTokens(0).Should().Be(0);
        ExperienceBoundedRecall.EstimateTokens(1).Should().Be(1);
        ExperienceBoundedRecall.EstimateTokens(4).Should().Be(1);
        ExperienceBoundedRecall.EstimateTokens(5).Should().Be(2);
        var negative = () => ExperienceBoundedRecall.EstimateTokens(-1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Receipt_CreatesFromCompletedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);
        var result = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, maximumItems: 2), ct);

        var receipt = ExperienceRecallReceipt.Create(result, "sha256:context:abc");

        receipt.QueryFingerprint.Should().Be(result.QueryFingerprint);
        receipt.ProjectionId.Should().Be(projection.ProjectionId);
        receipt.Policy.Should().Be(result.Policy);
        receipt.CheckpointPositionDigest.Should().BeNull();
        receipt.CheckpointVersion.Should().BeNull();
        receipt.Items.Should().HaveCount(2);
        receipt.Items.Select(item => item.EntityId)
            .Should().Equal(result.Items.Select(item => item.Record.Id));
        receipt.Items.Should().OnlyContain(item => item.EvidenceIds.Count >= 1);
        receipt.EstimatedTokensTotal.Should().Be(result.EstimatedTokensTotal);
        receipt.SourceTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Receipt_PreservesCheckpointWhenCovered()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);
        var entity = Entity(projection, "task:1", "first durable lesson");
        var position = new ExperienceProjectionPosition([
            new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
        ]);
        var checkpoint = new ExperienceProjectionCheckpoint(projection, position, 1);
        var recalled = new ExperienceRecalledEntity(entity, 1, ExperienceRecallScoreSemantics.ProviderRank, 7);
        var result = new ExperienceBoundedRecallResult(
            ExperienceRecallFingerprint.Compute(RecallRequest(projection)),
            projection.ProjectionId,
            "memory",
            new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
            checkpoint,
            [recalled],
            7,
            ExperienceProviderFreshness.Current,
            false);

        var receipt = ExperienceRecallReceipt.Create(result, "sha256:context:abc");

        receipt.CheckpointPositionDigest.Should().Be(position.Digest);
        receipt.CheckpointVersion.Should().Be(1);
    }

    [Fact]
    public async Task Receipt_RequiresCompletedResultWithItems()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var unsupported = await ExperienceBoundedRecall.ExecuteAsync(
            provider,
            RecallRequest(projection, asOf: new ExperienceProjectionPosition([
                new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
            ])),
            ct);

        var createUnsupported = () => ExperienceRecallReceipt.Create(unsupported, "sha256:context:abc");
        createUnsupported.Should().Throw<ArgumentException>();
        var badFingerprint = () => new ExperienceRecallReceipt(
            "not-a-recall-fingerprint",
            projection.ProjectionId,
            new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
            null,
            null,
            [new ExperienceRecallReceiptItem(
                ExperienceEntityId.New(),
                ExperienceDerivationId.New(),
                [new ExperienceEvidenceReference(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash").Id],
                1)],
            "sha256:context:abc",
            1,
            false);
        badFingerprint.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Receipt_RoundTripsThroughLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);
        var result = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, maximumItems: 1), ct);
        var receipt = ExperienceRecallReceipt.Create(result, "sha256:context:abc");

        var first = await store.AppendRecallReceiptAsync(
            sessionId,
            SessionParticipantAttribution.System("recall-test", "tests"),
            receipt,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);

        first.EventType.Should().Be(SessionEventTypes.ExperienceRecallRecorded);
        first.PayloadSchema.Should().Be(new SessionPayloadSchema("penghou.recall-receipt", 1));
        first.Evidence.Should().Be(new SessionEvidenceDescriptor("receipt", "derived", "unassessed"));
        first.IdempotencyKey.Should().StartWith("recall:");
        first.ReadRecallReceipt().Should().BeEquivalentTo(receipt);

        var replay = await store.AppendRecallReceiptAsync(
            sessionId,
            SessionParticipantAttribution.System("recall-test", "tests"),
            receipt,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        replay.EventId.Should().Be(first.EventId);
        replay.Sequence.Should().Be(first.Sequence);
        replay.Hash.Should().Be(first.Hash);
    }

    [Fact]
    public async Task Receipt_AllowsEmptyResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);

        var result = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, entityKinds: ["nonexistent-kind"]), ct);
        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        result.Items.Should().BeEmpty();

        var receipt = ExperienceRecallReceipt.Create(
            result, ExperienceRecallReceipts.HashSuppliedContext("nothing"));
        receipt.Items.Should().BeEmpty();
        receipt.EstimatedTokensTotal.Should().Be(0);
        receipt.QueryFingerprint.Should().Be(result.QueryFingerprint);
    }

    [Fact]
    public void HashSuppliedContext_UsesStableVectors()
    {
        ExperienceRecallReceipts.HashSuppliedContext("abc").Should().Be(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        ExperienceRecallReceipts.HashSuppliedContext([]).Should().Be(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task FilteredRecall_ExplainsProviderBoundInteraction()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        await SeedAsync(provider, projection, ct);
        var note = Entity(projection, "note:1", "durable note", kind: "note");
        (await provider.UpsertEntityAsync(note, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var result = await ExperienceBoundedRecall.ExecuteAsync(
            provider, RecallRequest(projection, entityKinds: ["note"], maximumItems: 1), ct);

        result.IsTruncated.Should().BeTrue();
        result.Diagnostic.Should().Contain("filtering");
    }

    [Fact]
    public void Result_RejectsCompletedWithCapabilities()
    {
        var projection = Descriptor("memory");
        var inconsistent = () => new ExperienceBoundedRecallResult(
            "sha256:penghou-hongxian-recall:v1:00",
            projection.ProjectionId,
            "memory",
            new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
            null,
            [],
            0,
            ExperienceProviderFreshness.Current,
            false,
            ExperienceBoundedRecallCompletion.Completed,
            ExperienceProviderCapability.AsOf,
            "diagnostic");

        inconsistent.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_RejectsInvalidBounds()
    {
        var projection = Descriptor("memory");
        var policy = new ExperienceRetrievalPolicy("lexical-baseline", "policy-1");
        var emptyQuery = () => new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, " ");
        var noItems = () => new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "durable", maximumItems: 0);
        var noEvidence = () => new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "durable", minimumEvidenceReferences: 0);
        var badPolicy = () => new ExperienceRetrievalPolicy("Lexical Baseline", "policy-1");
        var badFreshness = () => new ExperienceBoundedRecallRequest(
            projection.ProjectionId, policy, "durable",
            freshnessRequirement: (ExperienceRecallFreshnessRequirement)42);

        emptyQuery.Should().Throw<ArgumentException>();
        noItems.Should().Throw<ArgumentOutOfRangeException>();
        noEvidence.Should().Throw<ArgumentOutOfRangeException>();
        badPolicy.Should().Throw<ArgumentException>();
        badFreshness.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static ExperienceBoundedRecallRequest RecallRequest(
        ExperienceProjectionDescriptor projection,
        IReadOnlyList<string>? entityKinds = null,
        ExperienceProjectionPosition? asOf = null,
        int maximumItems = 20,
        int maximumBytes = 65_536,
        int? maximumTokens = null,
        int minimumEvidenceReferences = 1,
        ExperienceRecallFreshnessRequirement freshness = ExperienceRecallFreshnessRequirement.Current) =>
        new(
            projection.ProjectionId,
            new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
            "durable",
            entityKinds,
            asOf,
            maximumItems,
            maximumBytes,
            maximumTokens,
            minimumEvidenceReferences,
            freshness);

    private static ExperienceProjectionDescriptor Descriptor(string provider) =>
        new(ExperienceProjectionId.New(), provider, "conformance", 1, "policy-1");

    private static async Task SeedAsync(
        IExperienceProjectionModelWriter writer,
        ExperienceProjectionDescriptor projection,
        CancellationToken cancellationToken)
    {
        foreach (var (key, title) in new[]
                 {
                     ("task:1", "first durable lesson"),
                     ("task:2", "second durable lesson"),
                     ("task:3", "third durable lesson")
                 })
        {
            (await writer.UpsertEntityAsync(Entity(projection, key, title), cancellationToken)).Outcome
                .Should().Be(ExperienceModelWriteOutcome.Applied);
        }
    }

    private static ExperienceEntity Entity(
        ExperienceProjectionDescriptor projection,
        string stableKey,
        string title,
        string kind = "task") =>
        new(
            ExperienceEntityId.CreateDeterministic(projection.ProjectionId, kind, stableKey),
            kind,
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(projection.ProjectionId, "recall-test", stableKey)),
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement(title)
            },
            evidence: [Evidence()]);

    private static ExperienceEvidenceReference Evidence() =>
        new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash");

    private sealed class LexicalLessReader : IExperienceRecallReader
    {
        public ExperienceProviderCapabilities Capabilities { get; } = new(
            "stub",
            ExperienceProviderCapability.ExactLookup);

        public Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
            ExperienceEntityLookupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
            ExperienceRelationTraversalRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
            ExperienceLexicalSearchRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaleReader(IExperienceRecallReader inner) : IExperienceRecallReader
    {
        public ExperienceProviderCapabilities Capabilities => inner.Capabilities;

        public async Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
            ExperienceEntityLookupRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.GetEntityAsync(request, cancellationToken);
            return ExperienceRecallResult<ExperienceEntity>.Stale(result.Items, "Stale test double.");
        }

        public async Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
            ExperienceRelationTraversalRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.TraverseAsync(request, cancellationToken);
            return ExperienceRecallResult<ExperienceRelation>.Stale(result.Items, "Stale test double.");
        }

        public async Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
            ExperienceLexicalSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.SearchLexicalAsync(request, cancellationToken);
            return ExperienceRecallResult<ExperienceEntity>.Stale(result.Items, "Stale test double.");
        }
    }
}
