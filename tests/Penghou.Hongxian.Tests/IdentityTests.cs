using FluentAssertions;

namespace Penghou.Hongxian.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void SessionId_RejectsEmptyGuid()
    {
        var action = () => new SessionId(Guid.Empty);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExternalOperationReference_RequiresSystemAndId()
    {
        var missingSystem = () => new ExternalOperationReference("", Guid.NewGuid());
        var missingId = () => new ExternalOperationReference("example", Guid.Empty);

        missingSystem.Should().Throw<ArgumentException>();
        missingId.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProviderName_IsPartOfExternalOperationIdentity()
    {
        var id = Guid.CreateVersion7();

        new ExternalOperationReference("engine-a", id)
            .Should().NotBe(new ExternalOperationReference("engine-b", id));
    }

    [Fact]
    public void ExternalOperationId_IsAnOpaqueOrdinalString()
    {
        var reference = new ExternalOperationReference(
            "media-engine",
            "batch/2026-08-31/candidate-02");

        reference.Id.Should().Be("batch/2026-08-31/candidate-02");
        reference.Should().NotBe(new ExternalOperationReference(
            "media-engine",
            "BATCH/2026-08-31/CANDIDATE-02"));
    }
}
