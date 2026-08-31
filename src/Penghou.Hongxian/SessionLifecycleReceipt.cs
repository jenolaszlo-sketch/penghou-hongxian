namespace Penghou.Hongxian;

/// <summary>
/// Transactional outbox record produced by the operational catalog. Delivery
/// to Siming is retry-safe and does not roll back the catalog mutation.
/// </summary>
public sealed record SessionEvidenceOutboxRecord
{
    public required Guid ReceiptId { get; init; }

    public required SessionId SessionId { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string IdempotencyKey { get; init; }

    public Guid? CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    public IReadOnlyDictionary<string, string> CrossSystemRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset? DeliveredAt { get; init; }
}

public interface ISessionEvidenceOutbox
{
    Task<IReadOnlyList<SessionEvidenceOutboxRecord>> ListPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(
        Guid receiptId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}

public sealed record SessionEvidenceDispatchResult(int Attempted, int Delivered);

/// <summary>
/// Delivers transactional operational evidence into the immutable session
/// ledger. Appends and delivery acknowledgements are independently retry-safe.
/// </summary>
public sealed class SessionEvidenceOutboxDispatcher(
    ISessionEvidenceOutbox outbox,
    ISessionEventStore eventStore,
    string actor = "hongxian")
{
    private readonly ISessionEvidenceOutbox outbox =
        outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly ISessionEventStore eventStore =
        eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    private readonly string actor = !string.IsNullOrWhiteSpace(actor)
        ? actor
        : throw new ArgumentException("An evidence actor is required.", nameof(actor));

    public async Task<SessionEvidenceDispatchResult> DispatchPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        var pending = await outbox.ListPendingAsync(maximumCount, cancellationToken)
            .ConfigureAwait(false);
        var delivered = 0;
        foreach (var record in pending)
        {
            var committed = await eventStore.AppendAsync(
                new SessionEventRequest(
                    record.SessionId,
                    actor,
                    record.EventType,
                    record.OccurredAt,
                    CausationId: record.CausationId,
                    CorrelationId: record.CorrelationId,
                    CrossSystemRefs: record.CrossSystemRefs,
                    IdempotencyKey: record.IdempotencyKey),
                cancellationToken).ConfigureAwait(false);
            await outbox.MarkDeliveredAsync(
                record.ReceiptId,
                committed.CommittedAt,
                cancellationToken).ConfigureAwait(false);
            delivered++;
        }
        return new SessionEvidenceDispatchResult(pending.Count, delivered);
    }
}
