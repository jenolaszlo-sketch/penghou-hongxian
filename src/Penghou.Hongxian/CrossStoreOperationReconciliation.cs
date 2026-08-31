namespace Penghou.Hongxian;

public enum CrossStoreOperationHealth
{
    Healthy,
    Incomplete,
    ReconciliationRequired
}

public sealed record CrossStoreOperationReconciliationItem(
    CrossStoreOperationId OperationId,
    CrossStoreOperationState State,
    CrossStoreOperationHealth Health,
    string OperatorAction,
    IReadOnlyList<string> FailedParticipants);

public sealed record CrossStoreOperationReconciliationReport(
    SessionId SessionId,
    DateTimeOffset InspectedAt,
    IReadOnlyList<CrossStoreOperationReconciliationItem> Operations)
{
    public bool IsHealthy => Operations.All(item =>
        item.Health == CrossStoreOperationHealth.Healthy);
}

public sealed class CrossStoreOperationReconciliationService(
    ICrossStoreOperationStore operationStore,
    TimeProvider timeProvider)
{
    public async Task<CrossStoreOperationReconciliationReport> InspectAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var operations = await operationStore.ListAsync(
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        return new CrossStoreOperationReconciliationReport(
            sessionId,
            timeProvider.GetUtcNow(),
            operations.Select(Inspect).ToArray());
    }

    private static CrossStoreOperationReconciliationItem Inspect(
        CrossStoreOperation operation)
    {
        var failed = operation.Participants
            .Where(item => item.State == CrossStoreParticipantState.Failed)
            .Select(item => item.Participant)
            .ToArray();
        if (operation.State == CrossStoreOperationState.Completed)
        {
            return new(
                operation.Id,
                operation.State,
                CrossStoreOperationHealth.Healthy,
                "No action required.",
                failed);
        }

        var action = operation.State switch
        {
            CrossStoreOperationState.Prepared when failed.Length == 0 =>
                "Resume the external execution from its first incomplete step; participant writes are idempotent.",
            CrossStoreOperationState.Prepared =>
                RecoveryFor(operation, failed),
            CrossStoreOperationState.RevisionCommitted =>
                "Do not roll back the accepted revision. Verify it, then replay incomplete participant publications in a forward recovery operation.",
            CrossStoreOperationState.Published =>
                "Verify publication receipts, then replay the final checkpoint/completion step.",
            CrossStoreOperationState.ReconciliationRequired =>
                RecoveryFor(operation, failed),
            _ => "Inspect the operation receipts and external execution history before taking action."
        };
        return new(
            operation.Id,
            operation.State,
            operation.State == CrossStoreOperationState.ReconciliationRequired ||
                failed.Length > 0
                ? CrossStoreOperationHealth.ReconciliationRequired
                : CrossStoreOperationHealth.Incomplete,
            action,
            failed);
    }

    private static string RecoveryFor(
        CrossStoreOperation operation,
        IReadOnlyCollection<string> failed)
    {
        var participantActions = operation.Participants
            .Where(item => failed.Contains(item.Participant))
            .Select(item => item.RecoveryAction)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (participantActions.Length > 0)
            return string.Join(" ", participantActions!);
        return operation.ReconciliationReason is null
            ? "Inspect external execution history and start a forward recovery operation."
            : $"{operation.ReconciliationReason} Start a forward recovery operation; do not rewrite the failed operation.";
    }
}
