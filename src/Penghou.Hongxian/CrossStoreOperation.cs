using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hongxian;

public readonly record struct CrossStoreOperationId
{
    public CrossStoreOperationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty cross-store operation ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static CrossStoreOperationId New() => new(Guid.CreateVersion7());

    public static CrossStoreOperationId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new CrossStoreOperationId(Guid.Parse(value));
    }

    public override string ToString() => Value.ToString("D");
}

public enum CrossStoreOperationState
{
    Prepared,
    RevisionCommitted,
    Published,
    Completed,
    ReconciliationRequired
}

public enum CrossStoreParticipantState
{
    Applied,
    Compensated,
    Failed
}

public sealed record CrossStoreOperationTransition
{
    public required long Sequence { get; init; }

    public required CrossStoreOperationState State { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Reason { get; init; }
}

public sealed record CrossStoreParticipantReceipt
{
    public required string Participant { get; init; }

    public required string IdempotencyKey { get; init; }

    public required CrossStoreParticipantState State { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public string? BeforeIdentity { get; init; }

    public string? AfterIdentity { get; init; }

    public string? ResultHash { get; init; }

    public string? RecoveryAction { get; init; }
}

public sealed record CrossStoreOperation
{
    public required CrossStoreOperationId Id { get; init; }

    public required SessionId SessionId { get; init; }

    public required ExternalOperationReference ExternalOperation { get; init; }

    public required string Kind { get; init; }

    public required string IdempotencyKey { get; init; }

    public required CrossStoreOperationState State { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required long Version { get; init; }

    public IReadOnlyList<CrossStoreParticipantReceipt> Participants { get; init; } = [];

    /// <summary>
    /// Append-only history. State is a projection of the final entry; recovery
    /// appends another transition and never removes evidence of an earlier one.
    /// </summary>
    public IReadOnlyList<CrossStoreOperationTransition> Transitions { get; init; } = [];

    public string? ReconciliationReason { get; init; }

    public string ParticipantIdempotencyKey(string participant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participant);
        var input = $"{IdempotencyKey}\n{participant}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }
}

public sealed record StartCrossStoreOperationRequest(
    SessionId SessionId,
    ExternalOperationReference ExternalOperation,
    string Kind,
    string IdempotencyKey,
    DateTimeOffset StartedAt,
    CrossStoreOperationId? OperationId = null);
