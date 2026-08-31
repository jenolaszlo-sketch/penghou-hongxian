namespace Penghou.Hongxian;

public sealed record SessionPendingInput(
    Guid RequestEventId,
    Guid? ExternalOperationId,
    string? SignalName,
    DateTimeOffset RequestedAt,
    DateTimeOffset ClaimedOccurredAt);

public sealed record SessionPendingApproval(
    Guid ProposalId,
    Guid? ExternalOperationId,
    string? TargetId,
    string? Revision,
    DateTimeOffset ProposedAt,
    DateTimeOffset ClaimedOccurredAt);

public sealed record SessionActiveIncident(
    Guid IncidentId,
    string? ReasonCode,
    SessionIncidentSeverity Severity,
    DateTimeOffset DetectedAt,
    DateTimeOffset ClaimedOccurredAt,
    Guid? ExternalOperationId = null,
    SessionRecoveryOutcome? Outcome = null);

public sealed record SessionCurrentState(
    int TotalEvents,
    string? LastEventType,
    DateTimeOffset? LastEventAt,
    IReadOnlyList<string> PendingInputEventIds,
    string? CurrentRevision,
    Guid? LastExternalOperationId,
    DateTimeOffset? SessionCreatedAt,
    DateTimeOffset? LastCommittedAt = null,
    SessionOperatorState OperatorState = SessionOperatorState.Ready,
    IReadOnlyList<string>? OpenIncidentIds = null,
    int ResolvedIncidentCount = 0,
    string? LastIncidentReason = null,
    IReadOnlyList<SessionPendingInput>? PendingInputs = null,
    IReadOnlyList<SessionPendingApproval>? PendingApprovals = null,
    IReadOnlyList<SessionActiveIncident>? ActiveIncidents = null);

public sealed record SessionProjectionSnapshot(
    SessionId SessionId,
    long AppliedSequence,
    string HeadHash,
    SessionCurrentState State);

/// <summary>
/// A complete contiguous session history bound to a successfully verified
/// authoritative ledger head. Projection providers must still validate the
/// supplied sequence and hash links before replacing existing state.
/// </summary>
public sealed record VerifiedSessionHistory(
    SessionId SessionId,
    SessionLedgerHead VerifiedHead,
    IReadOnlyList<SessionEvent> Events);

/// <summary>
/// Durable delivery cursor comparing the authoritative Siming ledger head with
/// the rebuildable projection head.
/// </summary>
public sealed record SessionProjectionDeliveryStatus(
    SessionId SessionId,
    long CommittedSequence,
    string? CommittedHeadHash,
    long AppliedSequence,
    string? AppliedHeadHash,
    DateTimeOffset UpdatedAt,
    string? LastFailureType = null,
    string? LastFailureDetail = null)
{
    public bool IsLagging => AppliedSequence < CommittedSequence ||
        (AppliedSequence == CommittedSequence &&
         !string.Equals(AppliedHeadHash, CommittedHeadHash, StringComparison.Ordinal));
}

public interface ISessionProjectionDeliveryStore
{
    Task RecordCommittedAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        SessionEvent sessionEvent,
        Exception exception,
        CancellationToken cancellationToken = default);

    Task<SessionProjectionDeliveryStatus?> GetDeliveryStatusAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionProjectionDeliveryStatus>> ListLaggingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);
}

public interface ISessionProjectionStore
{
    Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default);

    Task<SessionProjectionSnapshot?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<SessionProjectionSnapshot?> RebuildAsync(
        VerifiedSessionHistory history,
        CancellationToken cancellationToken = default);
}

public static class SessionTimelineProjection
{
    public static SessionCurrentState Project(IReadOnlyList<SessionEvent> events)
    {
        SessionCurrentState? state = null;
        foreach (var sessionEvent in events.OrderBy(item => item.Sequence))
            state = Apply(state, sessionEvent);
        return state ?? new SessionCurrentState(0, null, null, [], null, null, null);
    }

    public static SessionCurrentState Apply(SessionCurrentState? state, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var pendingInputs = RestorePendingInputs(state);
        if (sessionEvent.EventType == SessionEventTypes.InputRequested)
        {
            pendingInputs[sessionEvent.EventId] = new SessionPendingInput(
                sessionEvent.EventId,
                sessionEvent.CorrelationId,
                Reference(sessionEvent, "signalName"),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt);
        }
        else if (sessionEvent.EventType == SessionEventTypes.InputProvided && sessionEvent.CausationId is not null)
            pendingInputs.Remove(sessionEvent.CausationId.Value);

        var pendingApprovals = (state?.PendingApprovals ?? [])
            .ToDictionary(item => item.ProposalId);
        if (sessionEvent.EventType == SessionEventTypes.DecisionProposed &&
            TryReferenceGuid(sessionEvent, "proposalId", out var proposalId))
        {
            pendingApprovals[proposalId] = new SessionPendingApproval(
                proposalId,
                ReferenceGuid(sessionEvent, "externalOperationId") ?? sessionEvent.CorrelationId,
                Reference(sessionEvent, "targetId"),
                Reference(sessionEvent, "revision"),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt);
        }
        else if (sessionEvent.EventType is SessionEventTypes.ApprovalGranted or
                 SessionEventTypes.ApprovalDenied or
                 SessionEventTypes.DecisionSuperseded &&
                 TryReferenceGuid(sessionEvent, "proposalId", out proposalId))
        {
            pendingApprovals.Remove(proposalId);
        }

        var revision = state?.CurrentRevision;
        if (sessionEvent.EventType == SessionEventTypes.RevisionAccepted)
            revision = sessionEvent.CrossSystemRefs?.GetValueOrDefault("toRevision") ?? revision;
        var externalOperationId = state?.LastExternalOperationId;
        if (sessionEvent.EventType is SessionEventTypes.ExecutionStarted or SessionEventTypes.ExecutionCompleted or SessionEventTypes.ExecutionFailed)
            externalOperationId = sessionEvent.CorrelationId ?? externalOperationId;

        var incidents = RestoreIncidents(state);
        var resolvedCount = state?.ResolvedIncidentCount ?? 0;
        var lastIncidentReason = state?.LastIncidentReason;
        var incidentId = ReferenceGuid(sessionEvent, "incidentId");
        if (sessionEvent.EventType == SessionEventTypes.IncidentDetected && incidentId is not null)
        {
            lastIncidentReason = Reference(sessionEvent, "reasonCode");
            incidents[incidentId.Value] = new SessionActiveIncident(
                incidentId.Value,
                lastIncidentReason,
                ParseEnum(Reference(sessionEvent, "severity"), SessionIncidentSeverity.Error),
                sessionEvent.CommittedAt,
                sessionEvent.OccurredAt,
                sessionEvent.CorrelationId);
        }
        else if (sessionEvent.EventType == SessionEventTypes.RecoverySucceeded && incidentId is not null)
        {
            if (incidents.Remove(incidentId.Value)) resolvedCount++;
        }
        else if (sessionEvent.EventType is SessionEventTypes.UserActionRequired or SessionEventTypes.RecoveryFailed &&
                 incidentId is not null && incidents.TryGetValue(incidentId.Value, out var incident))
        {
            incidents[incidentId.Value] = incident with
            {
                Outcome = sessionEvent.EventType == SessionEventTypes.UserActionRequired
                    ? SessionRecoveryOutcome.UserActionRequired
                    : ParseEnum(
                        Reference(sessionEvent, "outcome"),
                        SessionRecoveryOutcome.ReconciliationRequired)
            };
        }

        var orderedInputs = pendingInputs.Values
            .OrderBy(item => item.RequestEventId)
            .ToArray();
        var orderedApprovals = pendingApprovals.Values
            .OrderBy(item => item.ProposalId)
            .ToArray();
        var orderedIncidents = incidents.Values
            .OrderBy(item => item.IncidentId)
            .ToArray();
        var operatorState = DeriveOperatorState(orderedInputs, orderedApprovals, orderedIncidents);

        return new SessionCurrentState(
            (state?.TotalEvents ?? 0) + 1,
            sessionEvent.EventType,
            sessionEvent.CommittedAt,
            orderedInputs.Select(item => item.RequestEventId.ToString("D")).ToArray(),
            revision,
            externalOperationId,
            state?.SessionCreatedAt ?? sessionEvent.CommittedAt,
            sessionEvent.CommittedAt,
            operatorState,
            orderedIncidents.Select(item => item.IncidentId.ToString("D")).ToArray(),
            resolvedCount,
            lastIncidentReason,
            orderedInputs,
            orderedApprovals,
            orderedIncidents);
    }

    /// <summary>
    /// Reconstructs a human- and machine-readable timeline of who did what, when,
    /// why (causation), and against which external operation or revision.
    /// </summary>
    public static IReadOnlyList<string> RenderTimeline(IReadOnlyList<SessionEvent> events) =>
        events.Select(item =>
            $"[{item.CommittedAt:O}] #{item.Sequence} {item.EventType} by " +
            $"{item.Participant.DisplayName ?? item.Participant.Subject} " +
            $"({item.Participant.Kind}@{item.Participant.Provider})" +
            (item.OccurredAt != item.CommittedAt ? $" claimed-at {item.OccurredAt:O}" : string.Empty) +
            (item.CausationId is not null ? $" caused-by {item.CausationId:D}" : string.Empty) +
            (item.CrossSystemRefs is { Count: > 0 }
                ? $" {string.Join(",", item.CrossSystemRefs.Select(pair => $"{pair.Key}={pair.Value}"))}"
                : string.Empty))
            .ToArray();

    private static Dictionary<Guid, SessionPendingInput> RestorePendingInputs(
        SessionCurrentState? state)
    {
        if (state?.PendingInputs is not null)
            return state.PendingInputs.ToDictionary(item => item.RequestEventId);

        return (state?.PendingInputEventIds ?? [])
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id is not null)
            .ToDictionary(
                id => id!.Value,
                id => new SessionPendingInput(
                    id!.Value,
                    state?.LastExternalOperationId,
                    null,
                    state?.LastCommittedAt ?? DateTimeOffset.MinValue,
                    state?.LastEventAt ?? DateTimeOffset.MinValue));
    }

    private static Dictionary<Guid, SessionActiveIncident> RestoreIncidents(
        SessionCurrentState? state)
    {
        if (state?.ActiveIncidents is not null)
            return state.ActiveIncidents.ToDictionary(item => item.IncidentId);

        return (state?.OpenIncidentIds ?? [])
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id is not null)
            .ToDictionary(
                id => id!.Value,
                id => new SessionActiveIncident(
                    id!.Value,
                    state?.LastIncidentReason,
                    state?.OperatorState == SessionOperatorState.Corrupt
                        ? SessionIncidentSeverity.Critical
                        : SessionIncidentSeverity.Error,
                    state?.LastCommittedAt ?? DateTimeOffset.MinValue,
                    state?.LastEventAt ?? DateTimeOffset.MinValue,
                    state?.LastExternalOperationId,
                    state?.OperatorState switch
                    {
                        SessionOperatorState.AwaitingInput => SessionRecoveryOutcome.UserActionRequired,
                        SessionOperatorState.ReconciliationRequired => SessionRecoveryOutcome.ReconciliationRequired,
                        SessionOperatorState.Corrupt => SessionRecoveryOutcome.Corrupt,
                        _ => null
                    }));
    }

    private static SessionOperatorState DeriveOperatorState(
        IReadOnlyList<SessionPendingInput> pendingInputs,
        IReadOnlyList<SessionPendingApproval> pendingApprovals,
        IReadOnlyList<SessionActiveIncident> incidents)
    {
        if (incidents.Any(item => item.Severity == SessionIncidentSeverity.Critical ||
                                  item.Outcome == SessionRecoveryOutcome.Corrupt))
            return SessionOperatorState.Corrupt;
        if (incidents.Any(item => item.Outcome == SessionRecoveryOutcome.ReconciliationRequired))
            return SessionOperatorState.ReconciliationRequired;
        if (pendingInputs.Count > 0 ||
            incidents.Any(item => item.Outcome == SessionRecoveryOutcome.UserActionRequired))
            return SessionOperatorState.AwaitingInput;
        if (pendingApprovals.Count > 0)
            return SessionOperatorState.AwaitingApproval;
        return incidents.Count > 0
            ? SessionOperatorState.Recovering
            : SessionOperatorState.Ready;
    }

    private static string? Reference(SessionEvent sessionEvent, string name) =>
        sessionEvent.CrossSystemRefs?.GetValueOrDefault(name);

    private static Guid? ReferenceGuid(SessionEvent sessionEvent, string name) =>
        TryReferenceGuid(sessionEvent, name, out var value) ? value : null;

    private static bool TryReferenceGuid(SessionEvent sessionEvent, string name, out Guid value) =>
        Guid.TryParse(Reference(sessionEvent, name), out value);

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
