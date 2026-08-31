namespace Penghou.Hongxian;

public interface ISessionStore
{
    Task<Session> CreateAsync(
        string contextId,
        string resourceId,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<Session?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions from newest to oldest without opening their ledger or
    /// external operation stores. This is the operational query used by project/session
    /// pickers and background runtime discovery.
    /// </summary>
    Task<IReadOnlyList<Session>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Session?> FindByExternalOperationAsync(
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default);

    Task<Session> AttachExternalOperationAsync(
        SessionId sessionId,
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap the accepted revision. Returns the updated
    /// session when <paramref name="expectedRevision"/> matches the stored value,
    /// or <c>null</c> when another promotion already advanced the revision.
    /// </summary>
    Task<Session?> UpdateRevisionAsync(
        SessionId sessionId,
        string? expectedRevision,
        string replacementRevision,
        CancellationToken cancellationToken = default);
}
