using System.Text.Json;
using FluentAssertions;

namespace Penghou.Hongxian.Tests;

/// <summary>
/// Portable writer/reader behaviors every experience provider shape must honor.
/// Only contract-level outcomes shared by the in-memory reference, LatticeDB,
/// and transport-shaped fake providers belong here. Provider-specific behaviors
/// stay in provider-owned test classes: LatticeDB rejects relations with
/// missing endpoints while the reference provider accepts them, and the two
/// signal provider mismatch differently (conflict result versus
/// <see cref="ArgumentException"/>). Neither difference is portable, so neither
/// appears in these cases.
/// </summary>
internal static class ExperienceProviderConformanceCases
{
    public static async Task ReplayRoundtripAsync(
        IExperienceProjectionModelWriter writer,
        IExperienceRecallReader reader,
        string providerName,
        CancellationToken cancellationToken)
    {
        var projection = Descriptor(providerName);
        var entity = Entity(projection, "task:one", "Repair the broken build");

        (await writer.UpsertEntityAsync(entity, cancellationToken)).Outcome
            .Should().Be(
                ExperienceModelWriteOutcome.Applied,
                $"provider '{providerName}' must apply a new entity");
        (await writer.UpsertEntityAsync(entity, cancellationToken)).Outcome
            .Should().Be(
                ExperienceModelWriteOutcome.AlreadyPresent,
                $"provider '{providerName}' replay must be idempotent");

        var exact = await reader.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, entity.Id),
            cancellationToken);
        exact.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        exact.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);

        var lexical = await reader.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "broken"),
            cancellationToken);
        lexical.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        lexical.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
    }

    public static async Task LexicalSearchIsProjectionScopedAsync(
        IExperienceProjectionModelWriter writer,
        IExperienceRecallReader reader,
        string providerName,
        CancellationToken cancellationToken)
    {
        var firstProjection = Descriptor(providerName);
        var secondProjection = Descriptor(providerName);
        var first = Entity(firstProjection, "task:first", "shared phrase first");
        var later = Entity(firstProjection, "task:later", "shared phrase later");
        var other = Entity(secondProjection, "task:other", "shared phrase other projection");
        (await writer.UpsertEntityAsync(first, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertEntityAsync(later, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertEntityAsync(other, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var result = await reader.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(firstProjection.ProjectionId, "shared"),
            cancellationToken);

        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        result.Items.Select(item => item.Id)
            .Should().BeEquivalentTo(
                [first.Id, later.Id],
                $"provider '{providerName}' must isolate lexical recall by projection");
    }

    public static async Task ConflictingContentIsRejectedAsync(
        IExperienceProjectionModelWriter writer,
        IExperienceRecallReader reader,
        string providerName,
        CancellationToken cancellationToken)
    {
        _ = reader;
        var projection = Descriptor(providerName);
        var original = Entity(projection, "task:one", "Original");
        var changed = Entity(projection, "task:one", "Changed");

        (await writer.UpsertEntityAsync(original, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertEntityAsync(changed, cancellationToken)).Outcome
            .Should().Be(
                ExperienceModelWriteOutcome.Conflict,
                $"provider '{providerName}' must reject a reused identity with different content");
    }

    public static async Task TraversalReportsBoundsAsync(
        IExperienceProjectionModelWriter writer,
        IExperienceRecallReader reader,
        string providerName,
        CancellationToken cancellationToken)
    {
        var projection = Descriptor(providerName);
        var first = Entity(projection, "task:first", "First");
        var second = Entity(projection, "task:second", "Second");
        (await writer.UpsertEntityAsync(first, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertEntityAsync(second, cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertRelationAsync(Relation(projection, first.Id, second.Id, "edge:one"), cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);
        (await writer.UpsertRelationAsync(Relation(projection, first.Id, second.Id, "edge:two"), cancellationToken)).Outcome
            .Should().Be(ExperienceModelWriteOutcome.Applied);

        var result = await reader.TraverseAsync(
            new ExperienceRelationTraversalRequest(
                projection.ProjectionId, first.Id, "depends-on", maximumItems: 1),
            cancellationToken);

        result.Completion.Should().Be(ExperienceRecallCompletion.Completed);
        result.Items.Should().ContainSingle();
        result.IsTruncated.Should().BeTrue(
            $"provider '{providerName}' must report traversal truncation explicitly");
    }

    public static async Task DeleteAndReplayIsEquivalentAsync(
        IExperienceProjectionModelWriter writer,
        IExperienceRecallReader reader,
        string providerName,
        CancellationToken cancellationToken)
    {
        var projection = Descriptor(providerName);
        var first = Entity(projection, "task:first", "first durable lesson");
        var second = Entity(projection, "task:second", "second durable lesson");
        var relation = Relation(projection, first.Id, second.Id, "edge:replay");

        async Task SeedAsync()
        {
            (await writer.UpsertEntityAsync(first, cancellationToken)).Outcome
                .Should().Be(ExperienceModelWriteOutcome.Applied);
            (await writer.UpsertEntityAsync(second, cancellationToken)).Outcome
                .Should().Be(ExperienceModelWriteOutcome.Applied);
            (await writer.UpsertRelationAsync(relation, cancellationToken)).Outcome
                .Should().Be(ExperienceModelWriteOutcome.Applied);
        }

        await SeedAsync();
        var beforeSearch = await reader.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "durable"),
            cancellationToken);
        beforeSearch.Items.Should().HaveCount(2);
        var beforeTraversal = await reader.TraverseAsync(
            new ExperienceRelationTraversalRequest(projection.ProjectionId, first.Id),
            cancellationToken);
        beforeTraversal.Items.Should().ContainSingle().Which.Id.Should().Be(relation.Id);

        await writer.DeleteProjectionAsync(projection, cancellationToken);
        (await reader.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, first.Id),
            cancellationToken)).Items.Should().BeEmpty(
            $"provider '{providerName}' delete must remove the projection");
        (await reader.TraverseAsync(
            new ExperienceRelationTraversalRequest(projection.ProjectionId, first.Id),
            cancellationToken)).Items.Should().BeEmpty();

        await SeedAsync();
        var afterSearch = await reader.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "durable"),
            cancellationToken);
        afterSearch.Items.Select(item => item.Id)
            .Should().Equal(
                beforeSearch.Items.Select(item => item.Id),
                $"provider '{providerName}' replay must reproduce equivalent portable results");
        var afterTraversal = await reader.TraverseAsync(
            new ExperienceRelationTraversalRequest(projection.ProjectionId, first.Id),
            cancellationToken);
        afterTraversal.Items.Select(item => item.Id)
            .Should().Equal(beforeTraversal.Items.Select(item => item.Id));
    }

    private static ExperienceProjectionDescriptor Descriptor(string providerName) =>
        new(ExperienceProjectionId.New(), providerName, "conformance", 1, "policy-1");

    private static ExperienceEntity Entity(
        ExperienceProjectionDescriptor projection,
        string stableKey,
        string title) =>
        new(
            ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", stableKey),
            "task",
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(
                    projection.ProjectionId, "conformance", stableKey)),
            new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement(title)
            },
            evidence: [Evidence()]);

    private static ExperienceRelation Relation(
        ExperienceProjectionDescriptor projection,
        ExperienceEntityId from,
        ExperienceEntityId to,
        string stableKey) =>
        new(
            ExperienceRelationId.CreateDeterministic(
                projection.ProjectionId, from, to, "depends-on", stableKey),
            from,
            to,
            "depends-on",
            new ExperienceProvenance(
                projection,
                ExperienceDerivationId.CreateDeterministic(
                    projection.ProjectionId, "conformance-relation", stableKey)),
            evidence: [Evidence()]);

    private static ExperienceEvidenceReference Evidence() =>
        new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash");
}
