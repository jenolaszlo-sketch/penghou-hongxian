using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceDerivationStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "hongxian-derivation-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DerivationStorage_IsReplaySafe()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        await provider.UpsertEntityAsync(entity, ct);
        var summary = await ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("a summary"),
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: ct);
        var embedding = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            new FixedEmbeddingGenerator([0.5f, 0.5f]),
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: ct);

        (await provider.UpsertSummaryAsync(summary, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertSummaryAsync(summary, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.AlreadyPresent);
        (await provider.UpsertEmbeddingAsync(embedding, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEmbeddingAsync(embedding, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.AlreadyPresent);

        var stored = await provider.GetSummaryAsync(
            new ExperienceDerivationLookupRequest(projection.ProjectionId, summary.Id), ct);
        stored.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(summary);
        var storedVector = await provider.GetEmbeddingAsync(
            new ExperienceDerivationLookupRequest(projection.ProjectionId, embedding.Id), ct);
        storedVector.Items.Should().ContainSingle().Which.Id.Should().Be(embedding.Id);
        var missing = await provider.GetSummaryAsync(
            new ExperienceDerivationLookupRequest(
                projection.ProjectionId, ExperienceDerivationId.New()), ct);
        missing.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task DerivationStorage_RejectsConflictsAndDeletesPerProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var other = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        await provider.UpsertEntityAsync(entity, ct);
        var createdAt = FixedTime;
        var summary = await ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("original"),
            SessionPayloadSensitivity.Internal,
            createdAt: createdAt,
            cancellationToken: ct);

        var changed = new ExperienceSummary(
            summary.Id,
            summary.EntityId,
            summary.ProjectionId,
            "changed",
            summary.Generator,
            summary.PolicyVersion,
            summary.CreatedAt,
            summary.SourceEvidence,
            summary.Sensitivity,
            summary.DisclosureScope,
            summary.Supersedes);
        (await provider.UpsertSummaryAsync(summary, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertSummaryAsync(changed, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Conflict);

        var otherEntity = Entity(other, "task:1", "Other projection");
        await provider.UpsertEntityAsync(otherEntity, ct);
        var otherSummary = await ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(otherEntity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("other"),
            SessionPayloadSensitivity.Internal,
            createdAt: createdAt,
            cancellationToken: ct);
        (await provider.UpsertSummaryAsync(otherSummary, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        await provider.DeleteDerivationsAsync(projection, ct);
        (await provider.GetSummaryAsync(
            new ExperienceDerivationLookupRequest(projection.ProjectionId, summary.Id), ct))
            .Items.Should().BeEmpty();
        (await provider.GetSummaryAsync(
            new ExperienceDerivationLookupRequest(other.ProjectionId, otherSummary.Id), ct))
            .Items.Should().ContainSingle();
    }

    [Fact]
    public async Task VectorSearch_RanksByCosineSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var first = Entity(projection, "task:1", "first");
        var second = Entity(projection, "task:2", "second");
        var third = Entity(projection, "task:3", "third");
        await provider.UpsertEntityAsync(first, ct);
        await provider.UpsertEntityAsync(second, ct);
        await provider.UpsertEntityAsync(third, ct);
        await EmbedAsync(provider, first, [1.0f, 0.0f], ct);
        await EmbedAsync(provider, second, [0.0f, 1.0f], ct);
        await EmbedAsync(provider, third, [1.0f, 1.0f], ct);

        var result = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f, 0.0f]), ct);

        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        result.IsTruncated.Should().BeFalse();
        result.Items.Select(match => match.EntityId).Should().Equal([first.Id, third.Id, second.Id]);
        result.Items[0].Similarity.Should().BeApproximately(1.0, 1e-6);
        result.Items[1].Similarity.Should().BeApproximately(1.0 / Math.Sqrt(2.0), 1e-6);
        result.Items[2].Similarity.Should().BeApproximately(0.0, 1e-6);
        result.Items.Should().OnlyContain(match => match.DerivationId.Value != Guid.Empty);
    }

    [Fact]
    public async Task VectorSearch_AppliesFloorSkipsDimsAndBounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var first = Entity(projection, "task:1", "first");
        var second = Entity(projection, "task:2", "second");
        var third = Entity(projection, "task:3", "third");
        await provider.UpsertEntityAsync(first, ct);
        await provider.UpsertEntityAsync(second, ct);
        await provider.UpsertEntityAsync(third, ct);
        await EmbedAsync(provider, first, [1.0f, 0.0f], ct);
        await EmbedAsync(provider, second, [0.0f, 1.0f, 0.0f], ct);
        await EmbedAsync(provider, third, [0.0f, 1.0f], ct);

        var floored = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(
                projection.ProjectionId, [1.0f, 0.0f], minimumSimilarity: 0.9), ct);
        floored.Items.Should().ContainSingle().Which.EntityId.Should().Be(first.Id);

        var mismatched = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f, 0.0f, 0.0f]), ct);
        mismatched.Items.Should().ContainSingle().Which.EntityId.Should().Be(second.Id);

        var bounded = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f, 1.0f], maximumItems: 1), ct);
        bounded.Items.Should().ContainSingle();
        bounded.IsTruncated.Should().BeTrue();
        bounded.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task VectorSearch_ReturnsBestMatchPerEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "first");
        await provider.UpsertEntityAsync(entity, ct);
        var stale = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "old"),
            new FixedEmbeddingGenerator([0.0f, 1.0f]),
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: ct);
        var current = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "new"),
            new FixedEmbeddingGenerator([1.0f, 0.0f]),
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: ct);
        (await provider.UpsertEmbeddingAsync(stale, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEmbeddingAsync(current, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var result = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f, 0.0f]), ct);

        result.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ExperienceVectorMatch(entity.Id, 1.0, current.Id));
    }

    [Fact]
    public async Task VectorAndHybridSearch_ReportUnsupportedAsOf()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var asOf = new ExperienceProjectionPosition([
            new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
        ]);

        var vector = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f], asOf: asOf), ct);
        vector.Completion.Should().Be(ExperienceRecallCompletion.Unsupported);
        vector.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.AsOf);
        vector.Items.Should().BeEmpty();

        var hybrid = await provider.SearchHybridAsync(
            new ExperienceHybridSearchRequest(projection.ProjectionId, "query", [1.0f], asOf: asOf), ct);
        hybrid.Completion.Should().Be(ExperienceRecallCompletion.Unsupported);
        hybrid.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.AsOf);
        hybrid.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task HybridSearch_FusesLexicalAndVectorRanks()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var first = Entity(projection, "task:1", "shared alpha");
        var second = Entity(projection, "task:2", "shared beta");
        var third = Entity(projection, "task:3", "shared gamma");
        await provider.UpsertEntityAsync(first, ct);
        await provider.UpsertEntityAsync(second, ct);
        await provider.UpsertEntityAsync(third, ct);
        await EmbedAsync(provider, second, [1.0f], ct);

        var result = await provider.SearchHybridAsync(
            new ExperienceHybridSearchRequest(projection.ProjectionId, "shared", [1.0f]), ct);

        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        var lexicalOrder = new[] { first.Id, second.Id, third.Id }
            .OrderBy(id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var lexicalRank = Array.IndexOf(lexicalOrder, second.Id) + 1;
        result.Items.Select(match => match.EntityId).First().Should().Be(second.Id);
        result.Items.Select(match => match.EntityId).Skip(1).Should().Equal(
            lexicalOrder.Where(id => id != second.Id));
        var head = result.Items[0];
        head.LexicalRank.Should().Be(lexicalRank);
        head.VectorRank.Should().Be(1);
        head.Score.Should().BeApproximately(
            1.0 / (ExperienceHybridRankFusion.RrfK + lexicalRank) +
            1.0 / (ExperienceHybridRankFusion.RrfK + 1),
            1e-9);
        result.Items.Skip(1).Should().OnlyContain(match => match.VectorRank == null);
    }

    [Fact]
    public void Fusion_IsDeterministicWithIdTieBreak()
    {
        var first = ExperienceEntityId.New();
        var second = ExperienceEntityId.New();
        var (earlier, later) = first.ToString().CompareTo(second.ToString()) < 0
            ? (first, second)
            : (second, first);

        var fused = ExperienceHybridRankFusion.Fuse([earlier], [later]);

        fused.Select(match => match.EntityId).Should().Equal([earlier, later]);
        fused[0].Score.Should().Be(fused[1].Score);
        fused.Should().OnlyContain(match =>
            match.LexicalRank != null || match.VectorRank != null);
        ExperienceHybridRankFusion.Fuse([], []).Should().BeEmpty();
    }

    [Fact]
    public void Cosine_HandlesEdges()
    {
        ExperienceVectorSimilarity.Cosine([1.0f, 0.0f], [1.0f, 0.0f]).Should().BeApproximately(1.0, 1e-9);
        ExperienceVectorSimilarity.Cosine([0.0f, 0.0f], [1.0f, 0.0f]).Should().Be(0.0);
        var empty = () => ExperienceVectorSimilarity.Cosine([], [1.0f]);
        var mismatched = () => ExperienceVectorSimilarity.Cosine([1.0f], [1.0f, 0.0f]);
        empty.Should().Throw<ArgumentException>();
        mismatched.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Capabilities_AdvertiseVectorAndHybrid()
    {
        var provider = new InMemoryExperienceProvider();

        provider.Capabilities.Supports(ExperienceProviderCapability.VectorSearch).Should().BeTrue();
        provider.Capabilities.Supports(ExperienceProviderCapability.HybridRanking).Should().BeTrue();
        provider.Capabilities.Supports(ExperienceProviderCapability.AsOf).Should().BeFalse();
    }

    [Fact]
    public async Task ProviderSwap_RebuildsOnlyDerivedData()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "durable lesson");
        await provider.UpsertEntityAsync(entity, ct);

        // Derive with the first generator and prove recall works.
        var firstGen = new FixedEmbeddingGenerator([1.0f, 0.0f]);
        var firstEmbedding = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            firstGen,
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: ct);
        (await provider.UpsertEmbeddingAsync(firstEmbedding, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        var before = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [1.0f, 0.0f]), ct);
        before.Items.Should().ContainSingle().Which.DerivationId.Should().Be(firstEmbedding.Id);

        // Keep a bounded-recall receipt as the durable audit trail.
        var recall = await ExperienceBoundedRecall.ExecuteAsync(
            provider,
            new ExperienceBoundedRecallRequest(
                projection.ProjectionId,
                new ExperienceRetrievalPolicy("lexical-baseline", "policy-1"),
                "durable"),
            ct);
        var receipt = ExperienceRecallReceipt.Create(recall, "sha256:context:swap");
        var sessionId = SessionId.New();
        await using (var store = new SimingSessionEventStore(rootPath))
        {
            await store.AppendRecallReceiptAsync(
                sessionId,
                SessionParticipantAttribution.System("swap-test", "tests"),
                receipt,
                DateTimeOffset.UtcNow,
                cancellationToken: ct);
        }

        // Swap the generator: delete derived data only, then re-derive.
        await provider.DeleteDerivationsAsync(projection, ct);
        var secondGen = new FixedEmbeddingGenerator(
            [0.0f, 1.0f],
            new ExperienceGeneratorIdentity("other-provider", "other-model", "v2"));
        var secondEmbedding = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            secondGen,
            SessionPayloadSensitivity.Internal,
            supersedes: firstEmbedding.Id,
            createdAt: FixedTime,
            cancellationToken: ct);
        (await provider.UpsertEmbeddingAsync(secondEmbedding, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var after = await provider.SearchVectorAsync(
            new ExperienceVectorSearchRequest(projection.ProjectionId, [0.0f, 1.0f]), ct);
        after.Items.Should().ContainSingle().Which.DerivationId.Should().Be(secondEmbedding.Id);

        // Evidence and the earlier receipt remain verifiable.
        await using (var store = new SimingSessionEventStore(rootPath))
        {
            var events = await store.ReadAsync(sessionId, cancellationToken: ct);
            events.Should().ContainSingle().Which.ReadRecallReceipt()
                .Should().BeEquivalentTo(receipt);
            (await store.VerifyChainAsync(sessionId, ct)).Should().NotBeNull();
        }

        var entities = await provider.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, entity.Id), ct);
        entities.Items.Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static readonly DateTimeOffset FixedTime =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static ExperienceProjectionDescriptor Descriptor(string provider) =>
        new(ExperienceProjectionId.New(), provider, "conformance", 1, "policy-1");

    private static ExperienceEntity Entity(
        ExperienceProjectionDescriptor projection,
        string key,
        string title) =>
        new(
            ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", key),
            "task",
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(projection.ProjectionId, "derivation-test", key)),
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement(title)
            },
            evidence: [new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash")]);

    private static async Task EmbedAsync(
        IExperienceDerivationWriter writer,
        ExperienceEntity entity,
        IReadOnlyList<float> vector,
        CancellationToken cancellationToken)
    {
        var embedding = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            new FixedEmbeddingGenerator(vector),
            SessionPayloadSensitivity.Internal,
            createdAt: FixedTime,
            cancellationToken: cancellationToken);
        (await writer.UpsertEmbeddingAsync(embedding, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
    }

    private sealed class FixedSummaryGenerator(string text) : IExperienceSummaryGenerator
    {
        public ExperienceGeneratorIdentity Identity { get; } =
            new("test-provider", "test-model", "v1");

        public Task<ExperienceSummaryResult> SummarizeAsync(
            ExperienceSummaryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExperienceSummaryResult(text, Identity));
    }

    private sealed class FixedEmbeddingGenerator(
        IReadOnlyList<float> vector,
        ExperienceGeneratorIdentity? identity = null) : IExperienceEmbeddingGenerator
    {
        public ExperienceGeneratorIdentity Identity { get; } =
            identity ?? new ExperienceGeneratorIdentity("test-provider", "test-model", "v1");

        public Task<ExperienceEmbeddingResult> EmbedAsync(
            ExperienceEmbeddingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExperienceEmbeddingResult(vector, Identity));
    }
}
