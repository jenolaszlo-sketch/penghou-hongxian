namespace Penghou.Hongxian;

/// <summary>
/// Optional convenience wrapper around an application-supplied recovery
/// handler. The coordinator remains recording-only; the host owns execution,
/// authorization, and the meaning of the action and verification receipt.
/// </summary>
public sealed class SessionRecoveryExecutor(
    SessionRecoveryCoordinator coordinator,
    TimeProvider? timeProvider = null)
{
    private readonly SessionRecoveryCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SessionRecoveryExecutionResult> ExecuteAsync(
        SessionRecoveryPlan plan,
        Guid plannedEventId,
        int attempt,
        Func<SessionEvent, CancellationToken, Task<SessionRecoveryActionReceipt>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execute);
        plan.Action.Validate();

        var attempted = await coordinator.RecordAttemptAsync(
                plan, plannedEventId, attempt, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var receipt = await execute(attempted, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The recovery handler returned no receipt.");
            if (receipt.Action != plan.Action)
                throw new InvalidOperationException(
                    $"Recovery receipt action '{receipt.Action.Code}' does not match plan action '{plan.Action.Code}'.");
            if (!receipt.Verified)
                throw new InvalidOperationException(
                    "The recovery handler did not verify the resulting state.");

            var explanation =
                $"Recovery action '{plan.Action.Code}' completed and was verified: {receipt.Verification}";
            var terminal = await coordinator.CompleteAsync(
                new SessionRecoveryResolution(
                    plan.RecoveryPlanId,
                    plan.IncidentId,
                    plan.SessionId,
                    SessionRecoveryOutcome.Recovered,
                    attempt,
                    explanation,
                    timeProvider.GetUtcNow(),
                    plan.CorrelationId,
                    MergeReferences(plan.CrossSystemRefs, receipt.CrossSystemRefs),
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
                $"Recovery action '{plan.Action.Code}' did not reach a verified state " +
                $"({exception.GetType().Name}); forward reconciliation is required.";
            var references = MergeReferences(
                plan.CrossSystemRefs,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recoveryErrorType"] = exception.GetType().Name
                });
            var terminal = await coordinator.CompleteAsync(
                new SessionRecoveryResolution(
                    plan.RecoveryPlanId,
                    plan.IncidentId,
                    plan.SessionId,
                    SessionRecoveryOutcome.ReconciliationRequired,
                    attempt,
                    explanation,
                    timeProvider.GetUtcNow(),
                    plan.CorrelationId,
                    references),
                attempted.EventId,
                cancellationToken).ConfigureAwait(false);
            return new SessionRecoveryExecutionResult(
                SessionRecoveryOutcome.ReconciliationRequired,
                terminal,
                null,
                explanation);
        }
    }

    private static IReadOnlyDictionary<string, string>? MergeReferences(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        if (first is null && second is null) return null;
        var result = first is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(first, StringComparer.Ordinal);
        if (second is not null)
            foreach (var (key, value) in second)
                result[key] = value;
        return result;
    }
}
