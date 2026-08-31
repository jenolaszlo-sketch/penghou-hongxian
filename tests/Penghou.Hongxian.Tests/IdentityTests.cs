using FluentAssertions;
using System.Text.Json;

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

    [Fact]
    public void PublicIdentifiers_SupportNonThrowingParsing()
    {
        var sessionId = SessionId.New();
        var operationId = CrossStoreOperationId.New();
        var external = new ExternalOperationReference("zhinu", "run:step/2");

        SessionId.TryParse(sessionId.ToString(), out var parsedSession).Should().BeTrue();
        parsedSession.Should().Be(sessionId);
        CrossStoreOperationId.TryParse(operationId.ToString(), out var parsedOperation)
            .Should().BeTrue();
        parsedOperation.Should().Be(operationId);
        ExternalOperationReference.TryParse(external.ToString(), out var parsedExternal)
            .Should().BeTrue();
        parsedExternal.Should().Be(external);

        SessionId.TryParse("not-an-id", out _).Should().BeFalse();
        CrossStoreOperationId.TryParse(Guid.Empty.ToString(), out _).Should().BeFalse();
        ExternalOperationReference.TryParse("missing-separator", out _).Should().BeFalse();
    }

    [Fact]
    public void PublicIdentifiers_HaveStableStringJsonRepresentation()
    {
        var sessionId = SessionId.New();
        var operationId = CrossStoreOperationId.New();
        var external = new ExternalOperationReference("zhinu", "run/2");

        JsonSerializer.Deserialize<SessionId>(JsonSerializer.Serialize(sessionId))
            .Should().Be(sessionId);
        JsonSerializer.Deserialize<CrossStoreOperationId>(JsonSerializer.Serialize(operationId))
            .Should().Be(operationId);
        JsonSerializer.Deserialize<ExternalOperationReference>(JsonSerializer.Serialize(external))
            .Should().Be(external);
    }
}
