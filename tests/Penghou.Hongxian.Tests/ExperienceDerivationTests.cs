using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceDerivationTests
{
    [Fact]
    public async Task DeriveSummary_BuildsStableIdentifiedRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        var generator = new FixedSummaryGenerator("first summary");
        var request = new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1");

        var first = await ExperienceDerivation.DeriveSummaryAsync(
            request, generator, SessionPayloadSensitivity.Confidential, "team-a", cancellationToken: ct);
        var replay = await ExperienceDerivation.DeriveSummaryAsync(
            request, generator, SessionPayloadSensitivity.Confidential, "team-a", cancellationToken: ct);

        replay.Id.Should().Be(first.Id);
        replay.ContentDigest.Should().Be(first.ContentDigest);
        first.EntityId.Should().Be(entity.Id);
        first.ProjectionId.Should().Be(projection.ProjectionId);
        first.Generator.Should().Be(generator.Identity);
        first.PolicyVersion.Should().Be("policy-1");
        first.SourceEvidence.Should().BeEquivalentTo(entity.Evidence);
        first.Sensitivity.Should().Be(SessionPayloadSensitivity.Confidential);
        first.DisclosureScope.Should().Be("team-a");
        first.Supersedes.Should().BeNull();
        first.ContentDigest.Should().StartWith(ExperienceDerivationDigest.ContractVersion);
    }

    [Fact]
    public async Task DeriveEmbedding_BuildsStableFiniteVectorRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        var generator = new FixedEmbeddingGenerator([0.25f, -0.5f, 0.75f]);
        var request = new ExperienceEmbeddingRequest(entity, "policy-1", "v1");

        var first = await ExperienceDerivation.DeriveEmbeddingAsync(
            request, generator, SessionPayloadSensitivity.Internal, cancellationToken: ct);
        var replay = await ExperienceDerivation.DeriveEmbeddingAsync(
            request, generator, SessionPayloadSensitivity.Internal, cancellationToken: ct);

        replay.Id.Should().Be(first.Id);
        replay.ContentDigest.Should().Be(first.ContentDigest);
        first.Dimensions.Should().Be(3);
        first.Vector.Should().Equal(0.25f, -0.5f, 0.75f);
        first.SourceEvidence.Should().BeEquivalentTo(entity.Evidence);
        first.Supersedes.Should().BeNull();
    }

    [Fact]
    public async Task Rederivation_LinksSupersessionWithoutRewriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        var firstGen = new FixedEmbeddingGenerator([1.0f, 0.0f]);
        var secondGen = new FixedEmbeddingGenerator(
            [0.0f, 1.0f],
            new ExperienceGeneratorIdentity("other-provider", "other-model", "v2"));

        var first = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            firstGen,
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        var second = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            secondGen,
            SessionPayloadSensitivity.Internal,
            supersedes: first.Id,
            cancellationToken: ct);

        second.Id.Should().NotBe(first.Id);
        second.Supersedes.Should().Be(first.Id);
        second.ContentDigest.Should().NotBe(first.ContentDigest);
        first.Supersedes.Should().BeNull();
    }

    [Fact]
    public async Task Orchestrator_RejectsDishonestGeneratorOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");

        var overBudget = () => ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 4, "policy-1", "v1"),
            new FixedSummaryGenerator("too long for the budget"),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        await overBudget.Should().ThrowAsync<InvalidOperationException>();

        var wrongIdentity = () => ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new MislabeledSummaryGenerator(),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        await wrongIdentity.Should().ThrowAsync<InvalidOperationException>();

        var emptyVector = () => ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            new FixedEmbeddingGenerator([]),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        await emptyVector.Should().ThrowAsync<InvalidOperationException>();

        var nonFinite = () => ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            new FixedEmbeddingGenerator([float.NaN]),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        await nonFinite.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Records_RejectInvalidBounds()
    {
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        var generator = new ExperienceGeneratorIdentity("test-provider", "test-model", "v1");

        var emptyText = () => new ExperienceSummary(
            ExperienceDerivationId.New(), entity.Id, projection.ProjectionId, " ",
            generator, "policy-1", DateTimeOffset.UtcNow, entity.Evidence,
            SessionPayloadSensitivity.Internal);
        var badSensitivity = () => new ExperienceSummary(
            ExperienceDerivationId.New(), entity.Id, projection.ProjectionId, "text",
            generator, "policy-1", DateTimeOffset.UtcNow, entity.Evidence,
            (SessionPayloadSensitivity)42);
        var oversizedDims = () => new ExperienceEmbedding(
            ExperienceDerivationId.New(), entity.Id, projection.ProjectionId,
            new float[ExperienceDerivationLimits.EmbeddingDimensions + 1],
            generator, "policy-1", DateTimeOffset.UtcNow, entity.Evidence,
            SessionPayloadSensitivity.Internal);
        var badGenerator = () => new ExperienceGeneratorIdentity("Test Provider", "model", "v1");
        var badScope = () => new ExperienceSummary(
            ExperienceDerivationId.New(), entity.Id, projection.ProjectionId, "text",
            generator, "policy-1", DateTimeOffset.UtcNow, entity.Evidence,
            SessionPayloadSensitivity.Internal, " ");

        emptyText.Should().Throw<ArgumentException>();
        badSensitivity.Should().Throw<ArgumentOutOfRangeException>();
        oversizedDims.Should().Throw<ArgumentOutOfRangeException>();
        badGenerator.Should().Throw<ArgumentException>();
        badScope.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Derivations_PersistRedactionPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");

        var summary = await ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("a short summary"),
            SessionPayloadSensitivity.Internal,
            redactionPolicy: "retain-90d",
            cancellationToken: ct);
        summary.RedactionPolicy.Should().Be("retain-90d");

        var badPolicy = () => ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("a short summary"),
            SessionPayloadSensitivity.Internal,
            redactionPolicy: " ",
            cancellationToken: ct);
        await badPolicy.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Derivations_RoundTripJsonAndLeaveReceiptsVerifiable()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var projection = Descriptor("memory");
        var entity = Entity(projection, "task:1", "Repair the broken build");
        var summary = await ExperienceDerivation.DeriveSummaryAsync(
            new ExperienceSummaryRequest(entity, 8_192, "policy-1", "v1"),
            new FixedSummaryGenerator("a short summary"),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);
        var embedding = await ExperienceDerivation.DeriveEmbeddingAsync(
            new ExperienceEmbeddingRequest(entity, "policy-1", "v1"),
            new FixedEmbeddingGenerator([0.1f, 0.2f]),
            SessionPayloadSensitivity.Internal,
            cancellationToken: ct);

        JsonSerializer.Deserialize<ExperienceSummary>(
                JsonSerializer.Serialize(summary, options), options)
            .Should().BeEquivalentTo(summary);
        JsonSerializer.Deserialize<ExperienceEmbedding>(
                JsonSerializer.Serialize(embedding, options), options)
            .Should().BeEquivalentTo(embedding);
        summary.ContentDigest.Should().NotBe(embedding.ContentDigest);
    }

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

    private sealed class FixedSummaryGenerator(string text) : IExperienceSummaryGenerator
    {
        public ExperienceGeneratorIdentity Identity { get; } =
            new("test-provider", "test-model", "v1");

        public Task<ExperienceSummaryResult> SummarizeAsync(
            ExperienceSummaryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExperienceSummaryResult(text, Identity));
    }

    private sealed class MislabeledSummaryGenerator : IExperienceSummaryGenerator
    {
        public ExperienceGeneratorIdentity Identity { get; } =
            new("test-provider", "test-model", "v1");

        public Task<ExperienceSummaryResult> SummarizeAsync(
            ExperienceSummaryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExperienceSummaryResult(
                "text", new ExperienceGeneratorIdentity("other-provider", "other-model", "v2")));
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
