namespace Penghou.Hongxian;

/// <summary>
/// Serializes decisions that must observe one stable session resource state.
/// Providers coordinate across every process that can commit such a decision.
/// This lease never overrides fencing owned by an external execution system.
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

    /// <summary>Monotonically increasing token scoped to the session.</summary>
    long FencingToken { get; }

    /// <summary>Best-known expiry after the latest successful renewal.</summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>Signals immediately when renewal or ownership validation fails.</summary>
    CancellationToken LeaseLost { get; }

    /// <summary>
    /// Verifies current ownership. A protected store should compare the fencing
    /// token atomically with its own commit whenever possible.
    /// </summary>
    Task AssertOwnershipAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionDecisionLeaseLostException : Exception
{
    public SessionDecisionLeaseLostException(
        SessionId sessionId,
        long fencingToken,
        Exception? innerException = null)
        : base(
            $"Decision lease for session '{sessionId}' with fencing token " +
            $"{fencingToken} is no longer authoritative.",
            innerException)
    {
        SessionId = sessionId;
        FencingToken = fencingToken;
    }

    public SessionId SessionId { get; }

    public long FencingToken { get; }
}
