namespace Penghou.Hongxian.Tests;

/// <summary>
/// A transport-shaped test double: it composes the reference provider but
/// advertises remote operation, proving callers depend on contracts only.
/// </summary>
internal sealed class FakeRemoteExperienceProvider :
    IExperienceProjectionModelWriter,
    IExperienceRecallReader
{
    private readonly InMemoryExperienceProvider inner = new("remote-fake");

    public ExperienceProviderCapabilities Capabilities { get; } = new(
        "remote-fake",
        ExperienceProviderCapability.ExactLookup |
        ExperienceProviderCapability.BoundedTraversal |
        ExperienceProviderCapability.LexicalSearch |
        ExperienceProviderCapability.RemoteOperation);

    public Task<ExperienceModelWriteResult> UpsertEntityAsync(
        ExperienceEntity entity,
        CancellationToken cancellationToken = default) =>
        inner.UpsertEntityAsync(entity, cancellationToken);

    public Task<ExperienceModelWriteResult> UpsertRelationAsync(
        ExperienceRelation relation,
        CancellationToken cancellationToken = default) =>
        inner.UpsertRelationAsync(relation, cancellationToken);

    public Task DeleteProjectionAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default) =>
        inner.DeleteProjectionAsync(descriptor, cancellationToken);

    public Task<ExperienceRecallResult<ExperienceEntity>> GetEntityAsync(
        ExperienceEntityLookupRequest request,
        CancellationToken cancellationToken = default) =>
        inner.GetEntityAsync(request, cancellationToken);

    public Task<ExperienceRecallResult<ExperienceRelation>> TraverseAsync(
        ExperienceRelationTraversalRequest request,
        CancellationToken cancellationToken = default) =>
        inner.TraverseAsync(request, cancellationToken);

    public Task<ExperienceRecallResult<ExperienceEntity>> SearchLexicalAsync(
        ExperienceLexicalSearchRequest request,
        CancellationToken cancellationToken = default) =>
        inner.SearchLexicalAsync(request, cancellationToken);
}
