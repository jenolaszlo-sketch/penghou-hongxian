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
    IReadOnlyList<string> SuggestedActionCodes,
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
                [],
                failed);
        }

        var actions = SuggestionsFor(operation, failed);
        return new(
            operation.Id,
            operation.State,
            operation.State == CrossStoreOperationState.ReconciliationRequired ||
                failed.Length > 0
                ? CrossStoreOperationHealth.ReconciliationRequired
                : CrossStoreOperationHealth.Incomplete,
            actions,
            failed);
    }

    private static IReadOnlyList<string> SuggestionsFor(
        CrossStoreOperation operation,
        IReadOnlyCollection<string> failed)
    {
        var participantActions = operation.Participants
            .Where(item => failed.Contains(item.Participant))
            .Select(item => item.SuggestedActionCode)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Select(item => item!)
            .ToArray();
        if (participantActions.Length > 0)
            return participantActions;
        if (failed.Count > 0)
            return [CrossStoreSuggestedActions.InspectFailedParticipants];
        return operation.State == CrossStoreOperationState.ReconciliationRequired
            ? [CrossStoreSuggestedActions.ReconcileForward]
            : [CrossStoreSuggestedActions.ResumeIncompleteParticipants];
    }
}
