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

    /// <summary>Validates the append-only hash chain and returns the last event (or null).</summary>
    Task<SessionEvent?> VerifyChainAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record SessionEventRequest(
    SessionId SessionId,
    string Actor,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CausationId = null,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    string? PayloadJson = null,
    string? IdempotencyKey = null,
    Guid? EventId = null,
    SessionPayloadSensitivity PayloadSensitivity = SessionPayloadSensitivity.Internal,
    SessionPayloadRetention PayloadRetention = SessionPayloadRetention.Retain);

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
        if (AfterSequence < 0) throw new ArgumentOutOfRangeException(nameof(AfterSequence));
        if (Limit is <= 0 or > MaximumLimit) throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}

/// <summary>One bounded session timeline page.</summary>
public sealed record SessionEventPage(
    IReadOnlyList<SessionEvent> Events,
    long? NextSequence,
    bool HasMore);
