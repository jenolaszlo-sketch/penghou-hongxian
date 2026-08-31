using System.Text.Json;

namespace Penghou.Hongxian;

public interface ISessionEventStore
{
    /// <summary>Appends an event and returns the durable envelope with assigned sequence/hash.</summary>
    Task<SessionEvent> AppendAsync(
        SessionEventRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads events after an optional store sequence in ascending order.</summary>
    Task<IReadOnlyList<SessionEvent>> ReadAsync(
        SessionId sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one bounded page after an exclusive sequence cursor.</summary>
    Task<SessionEventPage> ReadPageAsync(
        SessionEventPageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the authoritative ledger and returns only the history covered
    /// by that verified head. Concurrent extensions are excluded.
    /// </summary>
    Task<VerifiedSessionHistory> ReadVerifiedHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Validates the append-only hash chain and returns the last event (or null).</summary>
    Task<SessionEvent?> VerifyChainAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record SessionEventRequest(
    SessionId SessionId,
    SessionParticipantAttribution Participant,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CausationId = null,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    string? PayloadJson = null,
    string? IdempotencyKey = null,
    Guid? EventId = null,
    SessionPayloadSensitivity PayloadSensitivity = SessionPayloadSensitivity.Internal,
    SessionPayloadRetention PayloadRetention = SessionPayloadRetention.Retain,
    SessionLedgerHead? ExpectedHead = null,
    SessionPayloadSchema? PayloadSchema = null,
    JsonElement? Payload = null);

/// <summary>Provider-neutral identity of an authoritative session-ledger head.</summary>
public sealed record SessionLedgerHead(
    string LedgerIdentity,
    long Sequence,
    string Hash);

/// <summary>Raised when a conditional session append observes a different head.</summary>
public sealed class SessionLedgerHeadConflictException : Exception
{
    public SessionLedgerHeadConflictException(
        SessionLedgerHead expectedHead,
        SessionLedgerHead actualHead,
        Exception? innerException = null)
        : base(
            $"Session ledger head changed: expected {expectedHead.LedgerIdentity}/" +
            $"{expectedHead.Sequence}/{expectedHead.Hash}, observed " +
            $"{actualHead.LedgerIdentity}/{actualHead.Sequence}/{actualHead.Hash}.",
            innerException)
    {
        ExpectedHead = expectedHead;
        ActualHead = actualHead;
    }

    public SessionLedgerHead ExpectedHead { get; }

    public SessionLedgerHead ActualHead { get; }
}

/// <summary>Describes the disclosure risk of an event payload.</summary>
public enum SessionPayloadSensitivity
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3
}

/// <summary>
/// Controls what enters the immutable ledger. Retention cannot be changed after
/// append without invalidating the ledger hash chain.
/// </summary>
public enum SessionPayloadRetention
{
    Retain = 0,
    DigestOnly = 1,
    Omit = 2
}

/// <summary>Requests one ordered page from a session ledger.</summary>
public sealed record SessionEventPageRequest(
    SessionId SessionId,
    long AfterSequence = 0,
    int Limit = 100)
{
    public const int MaximumLimit = 9_999;

    public void Validate()
    {
        SessionContractValidation.ValidateSessionId(SessionId, nameof(SessionId));
        if (AfterSequence < 0) throw new ArgumentOutOfRangeException(nameof(AfterSequence));
        if (Limit is <= 0 or > MaximumLimit) throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}

/// <summary>One bounded session timeline page.</summary>
public sealed record SessionEventPage(
    IReadOnlyList<SessionEvent> Events,
    long? NextSequence,
    bool HasMore);
