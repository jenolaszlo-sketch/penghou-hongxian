namespace Penghou.Hongxian;

/// <summary>
/// Serializes decisions that must observe one stable session resource and code
/// graph revision. Providers must coordinate across every process that can
/// approve, promote, or reindex the same session.
/// </summary>
public interface ISessionDecisionLeaseProvider
{
    ValueTask<ISessionDecisionLease> AcquireAsync(
        SessionId sessionId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public interface ISessionDecisionLease : IAsyncDisposable
{
    SessionId SessionId { get; }

    Guid OperationId { get; }

    DateTimeOffset AcquiredAt { get; }
}
