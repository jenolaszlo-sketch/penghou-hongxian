using System.Text.Json;
using FluentAssertions;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceProviderContractTests
{
    [Fact]
    public async Task InMemoryProvider_IsReplaySafeAndSupportsBoundedLexicalReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor();
        var provenance = new ExperienceProvenance(
            projection,
            ExperienceDerivationId.CreateDeterministic(projection.ProjectionId, "event", "source-1"));
        var entity = Entity(projection, provenance, "task:1", "Fix the build");

        (await provider.UpsertEntityAsync(entity, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEntityAsync(entity, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.AlreadyPresent);
        (await provider.GetEntityAsync(new ExperienceEntityLookupRequest(
            projection.ProjectionId, entity.Id), ct)).Items.Should().ContainSingle().Which.Should().Be(entity);

        var search = await provider.SearchLexicalAsync(new ExperienceLexicalSearchRequest(
            projection.ProjectionId, "build"), ct);
        search.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        search.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
    }

    [Fact]
    public async Task InMemoryProvider_ReportsUnsupportedAsOfWithoutFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var request = new ExperienceEntityLookupRequest(
            Descriptor().ProjectionId,
            ExperienceEntityId.New(),
            asOf: Position());

        var result = await provider.GetEntityAsync(request, ct);

        result.Completion.Should().Be(ExperienceRecallCompletion.Unsupported);
        result.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.AsOf);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void CapabilityAndResultTypes_RepresentRemoteAndOptionalOperationsExplicitly()
    {
        var capabilities = new ExperienceProviderCapabilities(
            "remote-fake",
            ExperienceProviderCapability.ExactLookup |
            ExperienceProviderCapability.RemoteOperation);

        capabilities.Supports(ExperienceProviderCapability.ExactLookup).Should().BeTrue();
        capabilities.Supports(ExperienceProviderCapability.VectorSearch).Should().BeFalse();

        var unsupported = ExperienceRecallResult<ExperienceEntity>.Unsupported(
            ExperienceProviderCapability.VectorSearch,
            "Remote provider has no vector index.");
        unsupported.Completion.Should().Be(ExperienceRecallCompletion.Unsupported);
        unsupported.UnsupportedCapabilities.Should().Be(ExperienceProviderCapability.VectorSearch);

        var invalidCapabilities = () => new ExperienceProviderCapabilities(
            "remote-fake",
            (ExperienceProviderCapability)256);
        var invalidName = () => new ExperienceProviderCapabilities(
            "Remote Fake",
            ExperienceProviderCapability.ExactLookup);
        invalidCapabilities.Should().Throw<ArgumentOutOfRangeException>();
        invalidName.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task InMemoryProvider_TraversesRelationsAndReportsTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor();
        var provenance = new ExperienceProvenance(projection, ExperienceDerivationId.New());
        var first = Entity(projection, provenance, "task:1", "one");
        var second = Entity(projection, provenance, "task:2", "two");
        var relation = new ExperienceRelation(
            ExperienceRelationId.CreateDeterministic(
                projection.ProjectionId, first.Id, second.Id, "depends-on", "edge:1"),
            first.Id,
            second.Id,
            "depends-on",
            provenance,
            evidence: [Evidence()]);
        await provider.UpsertRelationAsync(relation, ct);
        var anotherRelation = new ExperienceRelation(
            ExperienceRelationId.CreateDeterministic(
                projection.ProjectionId, first.Id, second.Id, "depends-on", "edge:2"),
            first.Id,
            second.Id,
            "depends-on",
            provenance,
            evidence: [Evidence()]);
        await provider.UpsertRelationAsync(anotherRelation, ct);

        var result = await provider.TraverseAsync(new ExperienceRelationTraversalRequest(
            projection.ProjectionId, first.Id, maximumItems: 1), ct);

        result.Items.Should().ContainSingle();
        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void RecallMetadata_RepresentsStaleDegradedAndTruncatedTogether()
    {
        var result = ExperienceRecallResult<ExperienceEntity>.Degraded(
            Array.Empty<ExperienceEntity>(),
            "Remote index is stale and reached its result bound.",
            ExperienceProviderFreshness.Stale,
            isTruncated: true);

        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        result.Freshness.Should().Be(ExperienceProviderFreshness.Stale);
        result.IsDegraded.Should().BeTrue();
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task FakeRemoteProvider_UsesOnlyPortableContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        IExperienceProjectionModelWriter writer = new FakeRemoteExperienceProvider();
        IExperienceRecallReader reader = (IExperienceRecallReader)writer;
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(),
            "remote-fake",
            "projector",
            1,
            "policy-1");
        var entity = Entity(
            projection,
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(
                    projection.ProjectionId,
                    "event",
                    "remote-source")),
            "task:remote",
            "Remote record");

        (await writer.UpsertEntityAsync(entity, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        var result = await reader.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, entity.Id),
            ct);

        reader.Capabilities.Supports(ExperienceProviderCapability.RemoteOperation)
            .Should().BeTrue();
        result.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
    }

    [Fact]
    public async Task InMemoryProvider_RejectsIdentityAndProviderConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new InMemoryExperienceProvider();
        var projection = Descriptor();
        var provenance = new ExperienceProvenance(
            projection,
            ExperienceDerivationId.CreateDeterministic(
                projection.ProjectionId,
                "event",
                "source-1"));
        var original = Entity(projection, provenance, "task:1", "Original");
        var changed = Entity(projection, provenance, "task:1", "Changed");

        (await provider.UpsertEntityAsync(original, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEntityAsync(changed, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Conflict);

        var otherProjection = new ExperienceProjectionDescriptor(
            projection.ProjectionId,
            "other-provider",
            projection.ProjectorName,
            projection.SchemaVersion,
            projection.PolicyVersion);
        var wrongProvider = Entity(
            otherProjection,
            new ExperienceProvenance(otherProjection, provenance.DerivationId),
            "task:2",
            "Wrong provider");
        (await provider.UpsertEntityAsync(wrongProvider, ct)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Conflict);
    }

    private static ExperienceEntity Entity(
        ExperienceProjectionDescriptor projection,
        ExperienceProvenance provenance,
        string key,
        string title) =>
        new(
            ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", key),
            "task",
            provenance,
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement(title)
            },
            evidence: [Evidence()]);

    private static ExperienceProjectionDescriptor Descriptor() =>
        new(ExperienceProjectionId.New(), "memory", "projector", 1, "policy-1");

    private static ExperienceProjectionPosition Position() =>
        new([new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))]);

    private static ExperienceEvidenceReference Evidence() =>
        new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash");
}
