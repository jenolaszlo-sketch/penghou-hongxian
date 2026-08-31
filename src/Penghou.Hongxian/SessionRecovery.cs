using System.Text.Json;

namespace Penghou.Hongxian;

public enum SessionIncidentSeverity
{
    Warning,
    Error,
    Critical
}

/// <summary>
/// Opaque application-defined recovery action. Hongxian records this identity
/// but does not interpret, authorize, schedule, or execute it.
/// </summary>
public sealed record SessionRecoveryActionReference(
    string Code,
    string? TargetType = null,
    string? TargetId = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Code);
        if ((TargetType is null) != (TargetId is null))
            throw new ArgumentException(
                "Recovery action target type and ID must either both be supplied or both be omitted.");
        if (TargetType is not null) ArgumentException.ThrowIfNullOrWhiteSpace(TargetType);
        if (TargetId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(TargetId);
    }
}

public enum SessionRecoveryOutcome
{
    Recovered,
    UserActionRequired,
    ReconciliationRequired,
    Corrupt
}

public enum SessionOperatorState
{
    Ready,
    AwaitingInput,
    AwaitingApproval,
    Recovering,
    ReconciliationRequired,
    Corrupt
}

public sealed record SessionIncident(
    Guid IncidentId,
    SessionId SessionId,
    string ReasonCode,
    SessionIncidentSeverity Severity,
    string Summary,
    DateTimeOffset DetectedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    Guid? CausationId = null);

public sealed record SessionRecoveryPlan(
    Guid RecoveryPlanId,
    Guid IncidentId,
    SessionId SessionId,
    SessionRecoveryActionReference Action,
    string Explanation,
    bool Automatic,
    DateTimeOffset PlannedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null);

public sealed record SessionRecoveryResolution(
    Guid RecoveryPlanId,
    Guid IncidentId,
    SessionId SessionId,
    SessionRecoveryOutcome Outcome,
    int Attempt,
    string Explanation,
    DateTimeOffset CompletedAt,
    Guid? CorrelationId = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null,
    SessionRecoveryActionReceipt? ActionReceipt = null);

/// <summary>
/// Durable evidence that a recovery action changed or inspected the named
/// resource and then verified the resulting safe state.
/// </summary>
public sealed record SessionRecoveryActionReceipt(
    Guid ReceiptId,
    SessionRecoveryActionReference Action,
    string Verification,
    DateTimeOffset ExecutedAt,
    bool Verified,
    string? ResultIdentity = null,
    IReadOnlyDictionary<string, string>? CrossSystemRefs = null);

public sealed record SessionRecoveryExecutionResult(
    SessionRecoveryOutcome Outcome,
    SessionEvent TerminalEvent,
    SessionRecoveryActionReceipt? Receipt,
    string Explanation);

/// <summary>
/// Appends forward-only incident and recovery evidence. It never changes the
/// operation or external execution that produced the incident.
/// </summary>
public sealed class SessionRecoveryCoordinator
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly ISessionEventStore events;
    private readonly string actor;
    private readonly TimeProvider timeProvider;

    public SessionRecoveryCoordinator(
        ISessionEventStore events,
        string actor = "hongxian",
        TimeProvider? timeProvider = null)
    {
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        this.actor = actor;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SessionEvent> DetectAsync(
        SessionIncident incident,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(incident.ReasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(incident.Summary);
        return events.AppendAsync(new SessionEventRequest(
            incident.SessionId,
            actor,
            SessionEventTypes.IncidentDetected,
            incident.DetectedAt,
            CausationId: incident.CausationId,
            CorrelationId: incident.CorrelationId,
            CrossSystemRefs: References(
                incident.CrossSystemRefs,
                ("incidentId", incident.IncidentId.ToString("D")),
                ("reasonCode", incident.ReasonCode),
                ("severity", incident.Severity.ToString())),
            PayloadJson: JsonSerializer.Serialize(incident, SerializerOptions),
            IdempotencyKey: $"incident:{incident.IncidentId:D}:detected"),
            cancellationToken);
    }

    public Task<SessionEvent> PlanAsync(
        SessionRecoveryPlan plan,
        Guid detectedEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Explanation);
        plan.Action.Validate();
        return events.AppendAsync(new SessionEventRequest(
            plan.SessionId,
            actor,
            SessionEventTypes.RecoveryPlanned,
            plan.PlannedAt,
            CausationId: detectedEventId,
            CorrelationId: plan.CorrelationId,
            CrossSystemRefs: References(
                plan.CrossSystemRefs,
                ("incidentId", plan.IncidentId.ToString("D")),
                ("recoveryPlanId", plan.RecoveryPlanId.ToString("D")),
                ("recoveryAction", plan.Action.Code),
                ("recoveryTargetType", plan.Action.TargetType),
                ("recoveryTargetId", plan.Action.TargetId),
                ("automatic", plan.Automatic.ToString())),
            PayloadJson: JsonSerializer.Serialize(plan, SerializerOptions),
            IdempotencyKey: $"incident:{plan.IncidentId:D}:plan:{plan.RecoveryPlanId:D}"),
            cancellationToken);
    }

    public Task<SessionEvent> RecordAttemptAsync(
        SessionRecoveryPlan plan,
        Guid plannedEventId,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        if (attempt <= 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        return events.AppendAsync(new SessionEventRequest(
            plan.SessionId,
            actor,
            SessionEventTypes.RecoveryAttempted,
            timeProvider.GetUtcNow(),
            CausationId: plannedEventId,
            CorrelationId: plan.CorrelationId,
            CrossSystemRefs: References(
                plan.CrossSystemRefs,
                ("incidentId", plan.IncidentId.ToString("D")),
                ("recoveryPlanId", plan.RecoveryPlanId.ToString("D")),
                ("recoveryAction", plan.Action.Code),
                ("attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            IdempotencyKey: $"incident:{plan.IncidentId:D}:plan:{plan.RecoveryPlanId:D}:attempt:{attempt}"),
            cancellationToken);
    }

    public Task<SessionEvent> CompleteAsync(
        SessionRecoveryResolution resolution,
        Guid causationEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Attempt < 0 ||
            resolution.Attempt == 0 &&
            resolution.Outcome != SessionRecoveryOutcome.UserActionRequired)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution.Attempt),
                "Attempt zero is reserved for recovery actions that were explicitly deferred to a user.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution.Explanation);
        if (resolution.Outcome == SessionRecoveryOutcome.Recovered)
        {
            var receipt = resolution.ActionReceipt ?? throw new ArgumentException(
                "A verified action receipt is required before recovery can succeed.",
                nameof(resolution));
            if (!receipt.Verified)
                throw new ArgumentException(
                    "An unverified action receipt cannot complete recovery.",
                    nameof(resolution));
            if (receipt.ReceiptId == Guid.Empty)
                throw new ArgumentException("A stable recovery receipt ID is required.", nameof(resolution));
            if (receipt.ExecutedAt == default)
                throw new ArgumentException("A recovery receipt execution time is required.", nameof(resolution));
            receipt.Action.Validate();
            ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Verification);
        }
        var eventType = resolution.Outcome switch
        {
            SessionRecoveryOutcome.Recovered => SessionEventTypes.RecoverySucceeded,
            SessionRecoveryOutcome.UserActionRequired => SessionEventTypes.UserActionRequired,
            _ => SessionEventTypes.RecoveryFailed
        };
        return events.AppendAsync(new SessionEventRequest(
            resolution.SessionId,
            actor,
            eventType,
            resolution.CompletedAt,
            CausationId: causationEventId,
            CorrelationId: resolution.CorrelationId,
            CrossSystemRefs: References(
                resolution.CrossSystemRefs,
                ("incidentId", resolution.IncidentId.ToString("D")),
                ("recoveryPlanId", resolution.RecoveryPlanId.ToString("D")),
                ("attempt", resolution.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("outcome", resolution.Outcome.ToString()),
                ("recoveryAction", resolution.ActionReceipt?.Action.Code),
                ("recoveryReceiptId", resolution.ActionReceipt?.ReceiptId.ToString("D")),
                ("recoveryTargetType", resolution.ActionReceipt?.Action.TargetType),
                ("recoveryTargetId", resolution.ActionReceipt?.Action.TargetId),
                ("recoveryResultIdentity", resolution.ActionReceipt?.ResultIdentity),
                ("recoveryVerification", resolution.ActionReceipt?.Verification)),
            PayloadJson: JsonSerializer.Serialize(resolution, SerializerOptions),
            IdempotencyKey: $"incident:{resolution.IncidentId:D}:plan:{resolution.RecoveryPlanId:D}:attempt:{resolution.Attempt}:outcome"),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> References(
        IReadOnlyDictionary<string, string>? source,
        params (string Key, string? Value)[] additions)
    {
        var result = source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);
        foreach (var (key, value) in additions)
            if (!string.IsNullOrWhiteSpace(value)) result[key] = value;
        return result;
    }

}
