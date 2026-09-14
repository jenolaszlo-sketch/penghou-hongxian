using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian.LatticeDb;

namespace Penghou.Hongxian.Tests;

public sealed class LatticeDbExperienceProviderTests
{
    [Fact]
    public async Task MemoryProvider_RoundTripsReplaysAndSearchesEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var projection = Descriptor();
        var entity = Entity(projection, "task:one", "Repair the broken build");

        (await provider.UpsertEntityAsync(entity, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEntityAsync(entity, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.AlreadyPresent);

        var exact = await provider.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, entity.Id), ct);
        exact.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
        exact.Items[0].Properties["title"].GetString().Should().Be("Repair the broken build");

        var lexical = await provider.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "broken"), ct);
        lexical.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
        lexical.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task LexicalSearch_IsProjectionScopedAndIndexesLaterWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var firstProjection = Descriptor();
        var secondProjection = Descriptor();
        var first = Entity(firstProjection, "task:first", "shared phrase first");
        var later = Entity(firstProjection, "task:later", "shared phrase later");
        var other = Entity(secondProjection, "task:other", "shared phrase other projection");
        await provider.UpsertEntityAsync(first, ct);
        await provider.UpsertEntityAsync(later, ct);
        await provider.UpsertEntityAsync(other, ct);

        var result = await provider.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(firstProjection.ProjectionId, "shared"), ct);

        result.Items.Select(item => item.Id).Should().BeEquivalentTo([first.Id, later.Id]);
        result.Items.Should().NotContain(item => item.Id == other.Id);
    }

    [Fact]
    public async Task Provider_RejectsConflictingContentAndMissingRelationEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var projection = Descriptor();
        var original = Entity(projection, "task:one", "Original");
        var changed = Entity(projection, "task:one", "Changed");
        (await provider.UpsertEntityAsync(original, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.Applied);
        (await provider.UpsertEntityAsync(changed, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.Conflict);

        var missing = Entity(projection, "task:missing", "Missing");
        var relation = Relation(projection, original.Id, missing.Id, "edge:missing");
        (await provider.UpsertRelationAsync(relation, ct)).Outcome.Should().Be(ExperienceModelWriteOutcome.Conflict);
    }

    [Fact]
    public async Task Provider_TraversesOnlyTheRequestedProjectionAndReportsBounds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var projection = Descriptor();
        var first = Entity(projection, "task:first", "First");
        var second = Entity(projection, "task:second", "Second");
        await provider.UpsertEntityAsync(first, ct);
        await provider.UpsertEntityAsync(second, ct);
        await provider.UpsertRelationAsync(Relation(projection, first.Id, second.Id, "edge:one"), ct);
        await provider.UpsertRelationAsync(Relation(projection, first.Id, second.Id, "edge:two"), ct);

        var result = await provider.TraverseAsync(
            new ExperienceRelationTraversalRequest(
                projection.ProjectionId, first.Id, "depends-on", maximumItems: 1), ct);

        result.Items.Should().ContainSingle();
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Checkpoint_AdvancesIdempotentlyAndResetsWithProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var descriptor = Descriptor();
        var position = new ExperienceProjectionPosition([
            new SessionEvidencePosition(
                SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
        ]);
        var request = new AdvanceExperienceProjectionRequest(descriptor, position, 0);

        var created = await provider.AdvanceAsync(request, ct);
        var replay = await provider.AdvanceAsync(request, ct);
        replay.Version.Should().Be(created.Version);
        replay.Position.Digest.Should().Be(created.Position.Digest);
        var stored = await provider.GetAsync(descriptor.ProjectionId, ct);
        stored.Should().NotBeNull();
        stored!.Version.Should().Be(created.Version);
        stored.Position.Digest.Should().Be(created.Position.Digest);

        await provider.DeleteProjectionAsync(descriptor, ct);
        (await provider.GetAsync(descriptor.ProjectionId, ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAndReplay_ProducesEquivalentPortableResults()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = MemoryProvider();
        var projection = Descriptor();
        var first = Entity(projection, "task:first", "first durable lesson");
        var second = Entity(projection, "task:second", "second durable lesson");
        var relation = Relation(projection, first.Id, second.Id, "edge:replay");

        async Task SeedAsync()
        {
            await provider.UpsertEntityAsync(first, ct);
            await provider.UpsertEntityAsync(second, ct);
            await provider.UpsertRelationAsync(relation, ct);
        }

        await SeedAsync();
        var beforeSearch = await provider.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "durable"), ct);
        var beforeTraversal = await provider.TraverseAsync(
            new ExperienceRelationTraversalRequest(projection.ProjectionId, first.Id), ct);

        await provider.DeleteProjectionAsync(projection, ct);
        (await provider.GetEntityAsync(
            new ExperienceEntityLookupRequest(projection.ProjectionId, first.Id), ct))
            .Items.Should().BeEmpty();
        await SeedAsync();

        var afterSearch = await provider.SearchLexicalAsync(
            new ExperienceLexicalSearchRequest(projection.ProjectionId, "durable"), ct);
        var afterTraversal = await provider.TraverseAsync(
            new ExperienceRelationTraversalRequest(projection.ProjectionId, first.Id), ct);
        afterSearch.Items.Select(item => item.Id)
            .Should().Equal(beforeSearch.Items.Select(item => item.Id));
        afterTraversal.Items.Select(item => item.Id)
            .Should().Equal(beforeTraversal.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task FileProvider_ReopensExistingStateReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"hongxian-lattice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "experience.lattice");
        try
        {
            var projection = Descriptor();
            var entity = Entity(projection, "task:file", "Persisted entity");
            using (var writer = new LatticeDbExperienceProvider(new LatticeDbExperienceProviderOptions
            {
                DatabasePath = path
            }))
                await writer.UpsertEntityAsync(entity, ct);

            using var reader = new LatticeDbExperienceProvider(new LatticeDbExperienceProviderOptions
            {
                DatabasePath = path,
                OpenMode = LatticeDbOpenMode.ReadOnly
            });
            var result = await reader.GetEntityAsync(
                new ExperienceEntityLookupRequest(projection.ProjectionId, entity.Id), ct);
            result.Items.Should().ContainSingle().Which.Id.Should().Be(entity.Id);
            var write = () => reader.UpsertEntityAsync(entity, ct);
            await write.Should().ThrowAsync<LatticeDbProviderException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Options_MakeLocationAndOpenModeExplicit()
    {
        var missingPath = () => new LatticeDbExperienceProviderOptions().Validate();
        var invalidMemory = () => new LatticeDbExperienceProviderOptions
        {
            Location = LatticeDbLocationKind.Memory,
            OpenMode = LatticeDbOpenMode.ReadOnly
        }.Validate();
        var memoryWithoutWal = () => new LatticeDbExperienceProviderOptions
        {
            Location = LatticeDbLocationKind.Memory,
            Durability = LatticeDbDurability.Volatile
        }.Validate();

        missingPath.Should().Throw<ArgumentException>();
        invalidMemory.Should().Throw<ArgumentException>();
        memoryWithoutWal.Should().Throw<ArgumentException>();
    }

    private static LatticeDbExperienceProvider MemoryProvider() =>
        new(new LatticeDbExperienceProviderOptions
        {
            Location = LatticeDbLocationKind.Memory,
            Lock = false
        });

    private static ExperienceProjectionDescriptor Descriptor() =>
        new(ExperienceProjectionId.New(), "latticedb", "test-projector", 1, "policy-1");

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
                    projection.ProjectionId, "test", stableKey)),
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
                    projection.ProjectionId, "test-relation", stableKey)),
            evidence: [Evidence()]);

    private static ExperienceEvidenceReference Evidence() =>
        new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash");
}
