using System.Text.Json;

namespace Penghou.Hongxian;

public enum SessionConsistencyHealth
{
    Healthy,
    Warning,
    ReconciliationRequired,
    Incompatible,
    Corrupt
}

public enum SessionLedgerAuditHealth
{
    Missing,
    Verified,
    UnsupportedSchema,
    Corrupt
}

public sealed record SessionLedgerAuditResult(
    SessionLedgerAuditHealth Health,
    SessionLedgerHead? VerifiedHead,
    long? FailedSequence = null,
    string? Failure = null,
    string? Detail = null);

public sealed record SessionOperationAuditResult(
    CrossStoreOperationId OperationId,
    CrossStoreOperationState State,
    long Version,
    int ParticipantCount,
    int FailedParticipantCount,
    string? StatusReasonCode);

public sealed record SessionEvidenceOutboxAuditResult(
    string Source,
    int PendingCount,
    bool ScanComplete);

/// <summary>Best-known decision lease observed outside a protected commit.</summary>
public sealed record SessionDecisionLeaseStatus(
    SessionId SessionId,
    Guid OperationId,
    long FencingToken,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ObservedAt)
{
    public bool IsExpired => ExpiresAt <= ObservedAt;
}

public interface ISessionDecisionLeaseInspector
{
    Task<SessionDecisionLeaseStatus?> GetStatusAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record SessionEvidenceOutboxAuditSource(
    string Name,
    ISessionEvidenceOutbox Outbox);

/// <summary>
/// A non-atomic, provider-neutral diagnostic snapshot. Every authoritative
/// store remains authoritative for its own component; this result never acts
/// as a distributed transaction or repairs state.
/// </summary>
public sealed record SessionConsistencyAuditResult(
    SessionId SessionId,
    DateTimeOffset ObservedAt,
    SessionConsistencyHealth Health,
    SessionLedgerAuditResult Ledger,
    long? CatalogVersion,
    SessionProjectionDeliveryStatus? Projection,
    IReadOnlyList<SessionOperationAuditResult> Operations,
    IReadOnlyList<SessionEvidenceOutboxAuditResult> EvidenceOutboxes,
    SessionDecisionLeaseStatus? DecisionLease)
{
    public bool CatalogEntryExists => CatalogVersion is not null;

    public bool ProjectionIsLagging => Projection?.IsLagging == true;

    public int IncompleteOperationCount => Operations.Count(item =>
        item.State != CrossStoreOperationState.Completed);

    public int FailedParticipantCount => Operations.Sum(item =>
        item.FailedParticipantCount);

    public int PendingEvidenceCount => EvidenceOutboxes.Sum(item =>
        item.PendingCount);
}

public sealed class SessionConsistencyAuditService
{
    public const int MaximumOutboxScan = 1_000;

    private readonly ISessionEventStore events;
    private readonly ISessionProjectionDeliveryStore projections;
    private readonly ISessionStore catalog;
    private readonly ICrossStoreOperationStore operations;
    private readonly ISessionDecisionLeaseInspector? leases;
    private readonly IReadOnlyList<SessionEvidenceOutboxAuditSource> outboxes;
    private readonly TimeProvider timeProvider;

    public SessionConsistencyAuditService(
        ISessionEventStore events,
        ISessionProjectionDeliveryStore projections,
        ISessionStore catalog,
        ICrossStoreOperationStore operations,
        IEnumerable<SessionEvidenceOutboxAuditSource>? outboxes = null,
        ISessionDecisionLeaseInspector? leases = null,
        TimeProvider? timeProvider = null)
    {
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.projections = projections ?? throw new ArgumentNullException(nameof(projections));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.leases = leases;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.outboxes = (outboxes ?? []).Select(source =>
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            ArgumentNullException.ThrowIfNull(source.Outbox);
            return source;
        }).ToArray();
        if (this.outboxes.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
            this.outboxes.Count)
            throw new ArgumentException("Evidence outbox audit source names must be unique.", nameof(outboxes));
    }

    public async Task<SessionConsistencyAuditResult> InspectAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        SessionContractValidation.ValidateSessionId(sessionId, nameof(sessionId));
        var observedAt = timeProvider.GetUtcNow();
        var ledger = await InspectLedgerAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var session = await catalog.GetAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var projection = await projections.GetDeliveryStatusAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        var operationItems = (await operations.ListAsync(sessionId, cancellationToken)
                .ConfigureAwait(false))
            .Select(operation => new SessionOperationAuditResult(
                operation.Id,
                operation.State,
                operation.Version,
                operation.Participants.Count,
                operation.Participants.Count(item =>
                    item.State == CrossStoreParticipantState.Failed),
                operation.StatusReasonCode))
            .ToArray();
        var outboxItems = new List<SessionEvidenceOutboxAuditResult>(outboxes.Count);
        foreach (var source in outboxes)
        {
            var pending = await source.Outbox.ListPendingAsync(
                MaximumOutboxScan,
                cancellationToken).ConfigureAwait(false);
            outboxItems.Add(new SessionEvidenceOutboxAuditResult(
                source.Name,
                pending.Count(item => item.SessionId == sessionId),
                pending.Count < MaximumOutboxScan));
        }
        var lease = leases is null
            ? null
            : await leases.GetStatusAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        var health = DetermineHealth(
            ledger,
            session,
            projection,
            operationItems,
            outboxItems,
            lease);
        return new SessionConsistencyAuditResult(
            sessionId,
            observedAt,
            health,
            ledger,
            session?.Version,
            projection,
            operationItems,
            outboxItems,
            lease);
    }

    private async Task<SessionLedgerAuditResult> InspectLedgerAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (events is ISessionLedgerPresenceStore presence &&
            !await presence.ExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            return new SessionLedgerAuditResult(SessionLedgerAuditHealth.Missing, null);
        try
        {
            var history = await events.ReadVerifiedHistoryAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            return new SessionLedgerAuditResult(
                SessionLedgerAuditHealth.Verified,
                history.VerifiedHead);
        }
        catch (UnsupportedSessionEventSchemaException exception)
        {
            return new SessionLedgerAuditResult(
                SessionLedgerAuditHealth.UnsupportedSchema,
                null,
                Failure: exception.GetType().FullName,
                Detail: exception.Message);
        }
        catch (SessionLedgerCorruptionException exception)
        {
            return new SessionLedgerAuditResult(
                SessionLedgerAuditHealth.Corrupt,
                null,
                exception.FailedSequence,
                exception.Failure,
                exception.Detail);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or
            FormatException or ArgumentException)
        {
            return new SessionLedgerAuditResult(
                SessionLedgerAuditHealth.Corrupt,
                null,
                Failure: "invalid-event-envelope",
                Detail: exception.Message);
        }
    }

    private static SessionConsistencyHealth DetermineHealth(
        SessionLedgerAuditResult ledger,
        Session? session,
        SessionProjectionDeliveryStatus? projection,
        IReadOnlyList<SessionOperationAuditResult> operations,
        IReadOnlyList<SessionEvidenceOutboxAuditResult> outboxes,
        SessionDecisionLeaseStatus? lease)
    {
        if (ledger.Health == SessionLedgerAuditHealth.Corrupt)
            return SessionConsistencyHealth.Corrupt;
        if (ledger.Health == SessionLedgerAuditHealth.UnsupportedSchema)
            return SessionConsistencyHealth.Incompatible;
        if (session is null ||
            operations.Any(item =>
                item.State == CrossStoreOperationState.ReconciliationRequired ||
                item.FailedParticipantCount > 0))
            return SessionConsistencyHealth.ReconciliationRequired;
        if (ledger.Health == SessionLedgerAuditHealth.Missing ||
            !ProjectionMatchesLedger(ledger.VerifiedHead, projection) ||
            operations.Any(item => item.State != CrossStoreOperationState.Completed) ||
            outboxes.Any(item => item.PendingCount > 0 || !item.ScanComplete) ||
            lease?.IsExpired == true)
            return SessionConsistencyHealth.Warning;
        return SessionConsistencyHealth.Healthy;
    }

    private static bool ProjectionMatchesLedger(
        SessionLedgerHead? ledgerHead,
        SessionProjectionDeliveryStatus? projection)
    {
        if (ledgerHead is null) return false;
        if (ledgerHead.Sequence == 0) return projection is null || !projection.IsLagging;
        return projection is not null &&
            projection.CommittedSequence == ledgerHead.Sequence &&
            projection.AppliedSequence == ledgerHead.Sequence &&
            string.Equals(
                projection.CommittedHeadHash,
                ledgerHead.Hash,
                StringComparison.Ordinal) &&
            string.Equals(
                projection.AppliedHeadHash,
                ledgerHead.Hash,
                StringComparison.Ordinal);
    }
}
