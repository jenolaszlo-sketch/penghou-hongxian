namespace Penghou.Hongxian;

public sealed record Session
{
    public required SessionId Id { get; init; }

    public required string ContextId { get; init; }

    public required string ResourceId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<ExternalOperationReference> ExternalOperations { get; init; } = [];

    /// <summary>
    /// The accepted (promoted) revision. Identifies the exact current
    /// resource content; used to fence concurrent staging promotions.
    /// </summary>
    public string? CurrentRevision { get; init; }

    /// <summary>
    /// Monotonic operational-catalog version for optimistic concurrency and UI
    /// refresh tokens. It is not an immutable-ledger sequence.
    /// </summary>
    public long Version { get; init; }
}
