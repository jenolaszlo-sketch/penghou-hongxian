using FluentAssertions;

namespace Penghou.Hongxian.Tests;

public sealed class SessionContractValidationTests
{
    [Fact]
    public void CatalogIdentityAndRevisionBounds_ArePortableProviderContracts()
    {
        var context = () => SessionContractValidation.ValidateSessionIdentity(
            new string('c', SessionContractLimits.ContextIdCharacters + 1),
            "resource");
        var resource = () => SessionContractValidation.ValidateSessionIdentity(
            "context",
            new string('r', SessionContractLimits.ResourceIdCharacters + 1));
        var revision = () => SessionContractValidation.ValidateRevision(
            new string('v', SessionContractLimits.RevisionCharacters + 1),
            "revision");

        context.Should().Throw<ArgumentOutOfRangeException>();
        resource.Should().Throw<ArgumentOutOfRangeException>();
        revision.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CrossStoreBounds_AreValidatedBeforeProviderPersistence()
    {
        var request = new StartCrossStoreOperationRequest(
            SessionId.New(),
            new ExternalOperationReference("zhinu", "run-1"),
            new string('k', SessionContractLimits.OperationKindCharacters + 1),
            "operation:1",
            DateTimeOffset.UtcNow);
        var receipt = new CrossStoreParticipantReceipt
        {
            Participant = "workspace",
            IdempotencyKey = "receipt:1",
            State = CrossStoreParticipantState.Applied,
            RecordedAt = DateTimeOffset.UtcNow,
            AfterIdentity = new string(
                'i',
                SessionContractLimits.OperationIdentityCharacters + 1)
        };

        var invalidRequest = () => SessionContractValidation.Validate(request);
        var invalidReceipt = () => SessionContractValidation.Validate(receipt);

        invalidRequest.Should().Throw<ArgumentOutOfRangeException>();
        invalidReceipt.Should().Throw<ArgumentOutOfRangeException>();
    }
}
