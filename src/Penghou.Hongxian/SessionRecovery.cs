using System.Text.Json;

namespace Penghou.Hongxian;

public enum SessionIncidentSeverity
{
    Warning,
    Error,
    Critical
}

public enum SessionRecoveryAction
{
    None,
    RefreshPreview,
    AbandonCandidate,
    RetryIdempotently,
    ReconcileForward,
    HaltMutation
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
    SessionRecoveryAction Action,
    string Explanation,
    string? SafeRevision,
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
    SessionRecoveryAction Action,
    string ResourceType,
    string ResourceId,
    string Verification,
    DateTimeOffset ExecutedAt,
    bool Verified,
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

    public SessionRecoveryCoordinator(ISessionEventStore events, string actor = "hongxian")
    {
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        this.actor = actor;
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
                ("recoveryAction", plan.Action.ToString()),
                ("automatic", plan.Automatic.ToString()),
                ("safeRevision", plan.SafeRevision)),
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
            DateTimeOffset.UtcNow,
            CausationId: plannedEventId,
            CorrelationId: plan.CorrelationId,
            CrossSystemRefs: References(
                plan.CrossSystemRefs,
                ("incidentId", plan.IncidentId.ToString("D")),
                ("recoveryPlanId", plan.RecoveryPlanId.ToString("D")),
                ("recoveryAction", plan.Action.ToString()),
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
            ArgumentException.ThrowIfNullOrWhiteSpace(receipt.ResourceType);
            ArgumentException.ThrowIfNullOrWhiteSpace(receipt.ResourceId);
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
                ("recoveryAction", resolution.ActionReceipt?.Action.ToString()),
                ("recoveryReceiptId", resolution.ActionReceipt?.ReceiptId.ToString("D")),
                ("recoveryResourceType", resolution.ActionReceipt?.ResourceType),
                ("recoveryResourceId", resolution.ActionReceipt?.ResourceId),
                ("recoveryVerification", resolution.ActionReceipt?.Verification)),
            PayloadJson: JsonSerializer.Serialize(resolution, SerializerOptions),
            IdempotencyKey: $"incident:{resolution.IncidentId:D}:plan:{resolution.RecoveryPlanId:D}:attempt:{resolution.Attempt}:outcome"),
            cancellationToken);
    }

    /// <summary>
    /// Executes one recovery action and is the only convenience path that can
    /// turn execution into a successful terminal event. Exceptions and invalid
    /// receipts are retained as failed recovery evidence.
    /// </summary>
    public async Task<SessionRecoveryExecutionResult> ExecuteAsync(
        SessionRecoveryPlan plan,
        Guid plannedEventId,
        int attempt,
        Func<SessionEvent, CancellationToken, Task<SessionRecoveryActionReceipt>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execute);
        if (plan.Action is SessionRecoveryAction.None or SessionRecoveryAction.HaltMutation)
            throw new ArgumentException(
                $"Recovery action '{plan.Action}' cannot be executed automatically.",
                nameof(plan));

        var attempted = await RecordAttemptAsync(plan, plannedEventId, attempt, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var receipt = await execute(attempted, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The recovery executor returned no receipt.");
            if (receipt.Action != plan.Action)
                throw new InvalidOperationException(
                    $"Recovery receipt action '{receipt.Action}' does not match plan action '{plan.Action}'.");
            if (!receipt.Verified)
                throw new InvalidOperationException("The recovery executor did not verify the resulting state.");

            var explanation = $"Recovery action '{plan.Action}' completed and was verified: {receipt.Verification}";
            var receiptReferences = receipt.CrossSystemRefs is null
                ? plan.CrossSystemRefs
                : MergeReferences(plan.CrossSystemRefs, receipt.CrossSystemRefs);
            var terminal = await CompleteAsync(new SessionRecoveryResolution(
                    plan.RecoveryPlanId,
                    plan.IncidentId,
                    plan.SessionId,
                    SessionRecoveryOutcome.Recovered,
                    attempt,
                    explanation,
                    DateTimeOffset.UtcNow,
                    plan.CorrelationId,
                    References(receiptReferences,
                        ("recoveryAction", plan.Action.ToString()),
                        ("recoveryReceiptId", receipt.ReceiptId.ToString("D")),
                        ("recoveryResourceType", receipt.ResourceType),
                        ("recoveryResourceId", receipt.ResourceId)),
                    receipt),
                attempted.EventId,
                cancellationToken).ConfigureAwait(false);
            return new SessionRecoveryExecutionResult(
                SessionRecoveryOutcome.Recovered,
                terminal,
                receipt,
                explanation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var explanation =
                $"Recovery action '{plan.Action}' did not reach a verified safe state ({exception.GetType().Name}). " +
                "The prior accepted state remains authoritative and forward reconciliation is required.";
            var terminal = await CompleteAsync(new SessionRecoveryResolution(
                    plan.RecoveryPlanId,
                    plan.IncidentId,
                    plan.SessionId,
                    SessionRecoveryOutcome.ReconciliationRequired,
                    attempt,
                    explanation,
                    DateTimeOffset.UtcNow,
                    plan.CorrelationId,
                    References(plan.CrossSystemRefs,
                        ("recoveryErrorType", exception.GetType().Name))),
                attempted.EventId,
                cancellationToken).ConfigureAwait(false);
            return new SessionRecoveryExecutionResult(
                SessionRecoveryOutcome.ReconciliationRequired,
                terminal,
                null,
                explanation);
        }
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

    private static IReadOnlyDictionary<string, string> MergeReferences(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string> second)
    {
        var result = first is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var (key, value) in second)
            result[key] = value;
        return result;
    }
}
