using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

[JsonConverter(typeof(CrossStoreOperationIdJsonConverter))]
public readonly record struct CrossStoreOperationId : ISpanFormattable, IParsable<CrossStoreOperationId>
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

    public static CrossStoreOperationId Parse(string value, IFormatProvider? provider) =>
        Parse(value);

    public static bool TryParse(string? value, out CrossStoreOperationId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(
        string? value,
        IFormatProvider? provider,
        out CrossStoreOperationId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new CrossStoreOperationId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "D", formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
}

public enum CrossStoreOperationState
{
    Prepared = 0,
    Active = 1,
    // Value 2 was Published in Preview 1 and is migrated to Active.
    Completed = 3,
    ReconciliationRequired = 4
}

/// <summary>
/// Well-known, provider-neutral suggestions returned by consistency inspection.
/// Applications decide whether and how to execute them.
/// </summary>
public static class CrossStoreSuggestedActions
{
    public const string ResumeIncompleteParticipants = "resume-incomplete-participants";
    public const string InspectFailedParticipants = "inspect-failed-participants";
    public const string ReconcileForward = "reconcile-forward";
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

    /// <summary>Optional bounded application-defined phase; never interpreted by Hongxian.</summary>
    public string? ApplicationPhase { get; init; }

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

    /// <summary>Optional application-defined action code; Hongxian never executes it.</summary>
    public string? SuggestedActionCode { get; init; }
}

public sealed record CrossStoreOperation
{
    public required CrossStoreOperationId Id { get; init; }

    public required SessionId SessionId { get; init; }

    public required ExternalOperationReference ExternalOperation { get; init; }

    public required string Kind { get; init; }

    public required string IdempotencyKey { get; init; }

    public required CrossStoreOperationState State { get; init; }

    /// <summary>The latest opaque application phase, if one was recorded.</summary>
    public string? ApplicationPhase { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required long Version { get; init; }

    public IReadOnlyList<CrossStoreParticipantReceipt> Participants { get; init; } = [];

    /// <summary>
    /// Append-only history. State is a projection of the final entry; recovery
    /// appends another transition and never removes evidence of an earlier one.
    /// </summary>
    public IReadOnlyList<CrossStoreOperationTransition> Transitions { get; init; } = [];

    public string? StatusReasonCode { get; init; }

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
    CrossStoreOperationId? OperationId = null,
    string? InitialApplicationPhase = null);
