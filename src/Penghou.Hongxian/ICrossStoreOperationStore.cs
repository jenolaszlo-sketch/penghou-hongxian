namespace Penghou.Hongxian;

public interface ICrossStoreOperationStore
{
    Task<CrossStoreOperation> StartAsync(
        StartCrossStoreOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<CrossStoreOperation?> GetAsync(
        CrossStoreOperationId operationId,
        CancellationToken cancellationToken = default);

    Task<CrossStoreOperation?> FindByExternalOperationAsync(
        ExternalOperationReference externalOperation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrossStoreOperation>> ListAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<CrossStoreOperation> RecordParticipantAsync(
        CrossStoreOperationId operationId,
        CrossStoreParticipantReceipt receipt,
        CancellationToken cancellationToken = default);

    Task<CrossStoreOperation> TransitionAsync(
        CrossStoreOperationId operationId,
        CrossStoreOperationState targetState,
        DateTimeOffset occurredAt,
        string? reconciliationReason = null,
        CancellationToken cancellationToken = default);
}
