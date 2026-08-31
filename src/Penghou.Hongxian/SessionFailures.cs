namespace Penghou.Hongxian;

/// <summary>Raised when an idempotency key identifies different event content.</summary>
public sealed class SessionEventIdempotencyConflictException(
    SessionId sessionId,
    string idempotencyKey,
    Guid existingEventId)
    : Exception(
        $"Session event idempotency key '{idempotencyKey}' is already bound " +
        $"to event '{existingEventId:D}' in session '{sessionId}'.")
{
    public SessionId SessionId { get; } = sessionId;

    public string IdempotencyKey { get; } = idempotencyKey;

    public Guid ExistingEventId { get; } = existingEventId;
}

/// <summary>Raised when compare-and-swap observes another accepted revision.</summary>
public sealed class SessionRevisionConflictException(
    SessionId sessionId,
    string? expectedRevision,
    string? actualRevision,
    long actualVersion)
    : Exception(
        $"Session '{sessionId}' revision changed: expected " +
        $"'{expectedRevision ?? "<uninitialized>"}', observed " +
        $"'{actualRevision ?? "<uninitialized>"}' at catalog version {actualVersion}.")
{
    public SessionId SessionId { get; } = sessionId;

    public string? ExpectedRevision { get; } = expectedRevision;

    public string? ActualRevision { get; } = actualRevision;

    public long ActualVersion { get; } = actualVersion;
}

public enum SessionProjectionConsistencyFailure
{
    SequenceGap,
    HeadConflict,
    VerifiedHistoryLength,
    HashChainContinuity,
    VerifiedHeadMismatch,
    ProjectionAheadOfVerifiedHead
}

/// <summary>Raised when projection input cannot be reconciled with verified history.</summary>
public sealed class SessionProjectionConsistencyException(
    SessionId sessionId,
    SessionProjectionConsistencyFailure failure,
    long expectedSequence,
    long actualSequence,
    string? expectedHash = null,
    string? actualHash = null)
    : Exception(
        $"Session projection consistency failure '{failure}' for '{sessionId}': " +
        $"expected sequence {expectedSequence}, received {actualSequence}.")
{
    public SessionId SessionId { get; } = sessionId;

    public SessionProjectionConsistencyFailure Failure { get; } = failure;

    public long ExpectedSequence { get; } = expectedSequence;

    public long ActualSequence { get; } = actualSequence;

    public string? ExpectedHash { get; } = expectedHash;

    public string? ActualHash { get; } = actualHash;
}

/// <summary>Raised when cryptographic verification of a session ledger fails.</summary>
public sealed class SessionLedgerCorruptionException(
    SessionId sessionId,
    long? failedSequence,
    string failure,
    string? detail)
    : Exception(
        $"Session ledger '{sessionId}' failed verification at sequence " +
        $"{failedSequence?.ToString() ?? "<unknown>"}: {failure}.")
{
    public SessionId SessionId { get; } = sessionId;

    public long? FailedSequence { get; } = failedSequence;

    public string Failure { get; } = failure;

    public string? Detail { get; } = detail;
}
