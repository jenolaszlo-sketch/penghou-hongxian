using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;

namespace Penghou.Hongxian.Tests;

public sealed class ExperienceModelTests
{
    [Fact]
    public void DeterministicId_MatchesVersionedGoldenVector()
    {
        var projectionId = new ExperienceProjectionId(
            Guid.Parse("00000000-0000-0000-0000-000000000301"));

        ExperienceEntityId.CreateDeterministic(
                projectionId,
                "task",
                "task:42")
            .ToString().Should().Be("2bff3f23-14fb-86ee-9e48-5c56c0c0088e");
    }

    [Fact]
    public void DeterministicIds_AreStableAndScoped()
    {
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "memory", "projector", 1, "policy-1");

        var first = ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", "task:42");
        var replay = ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", "task:42");
        var otherKind = ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "person", "task:42");
        var derivation = ExperienceDerivationId.CreateDeterministic(
            projection.ProjectionId, "event-to-task", "event:42");
        var derivationReplay = ExperienceDerivationId.CreateDeterministic(
            projection.ProjectionId, "event-to-task", "event:42");

        first.Should().Be(replay);
        first.Should().NotBe(otherKind);
        derivation.Should().Be(derivationReplay);
        var relation = ExperienceRelationId.CreateDeterministic(
            projection.ProjectionId, first, otherKind, "depends-on", "edge:42");
        ExperienceRelationId.CreateDeterministic(
                projection.ProjectionId, first, otherKind, "depends-on", "edge:42")
            .Should().Be(relation);
        ExperienceEntityId.TryParse(first.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(first);

        ExperienceEntityId.CreateDeterministic(
                projection.ProjectionId, "a|b", "c")
            .Should().NotBe(ExperienceEntityId.CreateDeterministic(
                projection.ProjectionId, "a", "b|c"));
    }

    [Fact]
    public void Entity_SnapshotsPropertiesAndEvidence()
    {
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "memory", "projector", 1, "policy-1");
        var provenance = new ExperienceProvenance(projection, ExperienceDerivationId.New());
        var source = new ExperienceEvidenceReference(
            SessionId.New(), "ledger-a", 1, Guid.CreateVersion7(), "hash-1");
        using var json = JsonDocument.Parse("{\"title\":\"build\"}");
        var properties = new Dictionary<string, JsonElement>
        {
            ["title"] = json.RootElement.GetProperty("title")
        };
        var entity = new ExperienceEntity(
            ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", "task:42"),
            "task",
            provenance,
            properties,
            new ExperienceTemporalValidity(DateTimeOffset.UtcNow.AddDays(-1), null),
            [source]);

        properties["title"] = JsonDocument.Parse("\"changed\"").RootElement.Clone();
        entity.Properties["title"].GetString().Should().Be("build");
        entity.Evidence.Should().ContainSingle().Which.Should().Be(source);
        entity.Validity!.ValidTo.Should().BeNull();
    }

    [Fact]
    public void EvidenceReference_UsesEventIdentityAndRejectsInvalidValidity()
    {
        var eventId = Guid.CreateVersion7();
        var reference = new ExperienceEvidenceReference(
            SessionId.New(), "ledger-a", 3, eventId, "sha256:event");

        var replay = new ExperienceEvidenceReference(
            reference.SessionId,
            reference.LedgerIdentity,
            reference.Sequence,
            reference.EventId,
            reference.EventHash);
        reference.Id.Should().Be(replay.Id);
        reference.Id.Value.Should().NotBe(eventId);
        var invalid = () => new ExperienceTemporalValidity(
            DateTimeOffset.UnixEpoch.AddDays(2), DateTimeOffset.UnixEpoch);
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ModelIds_HaveStableJsonStrings()
    {
        var ids = new object[]
        {
            ExperienceEntityId.New(),
            ExperienceRelationId.New(),
            new ExperienceEvidenceReference(
                SessionId.New(),
                "ledger-a",
                1,
                Guid.CreateVersion7(),
                "hash-1").Id,
            ExperienceDerivationId.New()
        };

        foreach (var id in ids)
        {
            var json = JsonSerializer.Serialize(id, id.GetType());
            json.Should().MatchRegex("^\\\"[0-9a-f-]{36}\\\"$");
        }
    }

    [Fact]
    public void Entity_EnforcesPropertyBounds()
    {
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "memory", "projector", 1, "policy-1");
        var provenance = new ExperienceProvenance(projection, ExperienceDerivationId.New());
        var properties = Enumerable.Range(0, ExperienceContractLimits.PropertyCount + 1)
            .ToDictionary(index => $"property-{index}", _ => JsonDocument.Parse("true").RootElement.Clone());

        var evidence = new ExperienceEvidenceReference(
            SessionId.New(), "ledger-a", 1, Guid.CreateVersion7(), "hash-1");
        var action = () => new ExperienceEntity(
            ExperienceEntityId.New(), "task", provenance, properties, evidence: [evidence]);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Entity_CanonicalizesPropertiesAndEvidenceAndRequiresAnAnchor()
    {
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "memory", "projector", 1, "policy-1");
        var provenance = new ExperienceProvenance(
            projection,
            ExperienceDerivationId.CreateDeterministic(
                projection.ProjectionId,
                "event-to-entity",
                "entity-1"));
        var first = new ExperienceEvidenceReference(
            SessionId.New(), "ledger-b", 2, Guid.CreateVersion7(), "hash-2");
        var second = new ExperienceEvidenceReference(
            SessionId.New(), "ledger-a", 1, Guid.CreateVersion7(), "hash-1");
        var entity = new ExperienceEntity(
            ExperienceEntityId.New(),
            "task",
            provenance,
            new Dictionary<string, JsonElement>
            {
                ["zeta"] = JsonDocument.Parse("2").RootElement.Clone(),
                ["alpha"] = JsonDocument.Parse("1").RootElement.Clone()
            },
            evidence: [first, second]);

        entity.Properties.Keys.Should().Equal("alpha", "zeta");
        entity.Evidence.Select(item => item.Id.ToString())
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        var missing = () => new ExperienceEntity(
            ExperienceEntityId.New(), "task", provenance);
        var duplicate = () => new ExperienceEntity(
            ExperienceEntityId.New(), "task", provenance, evidence: [first, first]);
        missing.Should().Throw<ArgumentException>();
        duplicate.Should().Throw<ArgumentException>();
    }
}
