using System.Text.Json;

namespace Penghou.Hongxian;

public static class SessionEventTypes
{
    public const string SessionCreated = "session-created";
    public const string ExecutionAttached = "execution-attached";
    public const string RevisionAccepted = "revision-accepted";
    public const string DecisionLeaseAcquired = "decision-lease-acquired";
    public const string DecisionLeaseReleased = "decision-lease-released";
    public const string DecisionLeaseExpired = "decision-lease-expired";
    public const string UserMessage = "user-message";
    public const string AssistantMessage = "assistant-message";
    public const string InputRequested = "input-requested";
    public const string InputProvided = "input-provided";
    public const string ApprovalGranted = "approval-granted";
    public const string ApprovalDenied = "approval-denied";
    public const string ExecutionStarted = "execution-started";
    public const string ExecutionCompleted = "execution-completed";
    public const string ExecutionFailed = "execution-failed";
    public const string DecisionProposed = "decision-proposed";
    public const string ExecutionResumed = "execution-resumed";
    public const string ExecutionResumeFailed = "execution-resume-failed";
    public const string OperationPrepared = "operation-prepared";
    public const string OperationTransitioned = "operation-transitioned";
    public const string OperationParticipantRecorded =
        "operation-participant-recorded";
    public const string IncidentDetected = "incident-detected";
    public const string RecoveryPlanned = "recovery-planned";
    public const string RecoveryAttempted = "recovery-attempted";
    public const string RecoverySucceeded = "recovery-succeeded";
    public const string RecoveryFailed = "recovery-failed";
    public const string UserActionRequired = "user-action-required";
    public const string DecisionSuperseded = "decision-superseded";
    public const string ExternalEventMirrored = "external-event-mirrored";
}

/// <summary>
/// Validates caller-supplied occurrence-time claims. Commit time remains the
/// authoritative ordering time; old claims are allowed for delayed mirroring
/// and reconciliation, while implausible future claims are rejected.
/// </summary>
public sealed record SessionEventTimePolicy(TimeSpan MaximumFutureSkew)
{
    public static SessionEventTimePolicy Default { get; } =
        new(TimeSpan.FromMinutes(5));

    public void Validate()
    {
        if (MaximumFutureSkew < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFutureSkew),
                "Maximum future occurrence-time skew cannot be negative.");
    }
}

/// <summary>
/// An immutable, ordered session event envelope. Sequence is contiguous within
/// the session's authoritative ledger.
/// </summary>
public sealed record SessionEvent
{
    public required int SchemaVersion { get; init; }

    public required long Sequence { get; init; }

    public required Guid EventId { get; init; }

    public required SessionId SessionId { get; init; }

    public required SessionParticipantAttribution Participant { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Time assigned atomically by the authoritative ledger.</summary>
    public required DateTimeOffset CommittedAt { get; init; }

    public Guid? CausationId { get; init; }

    public Guid? CorrelationId { get; init; }

    public string? IdempotencyKey { get; init; }

    public IReadOnlyDictionary<string, string>? CrossSystemRefs { get; init; }

    public string? PayloadJson { get; init; }

    /// <summary>
    /// Canonical JSON-tree payload used by new append APIs. `PayloadJson`
    /// remains readable for events written through the preview-1 contract.
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Application-owned payload schema persisted with this envelope. Null
    /// denotes an unversioned payload written by an earlier or schema-less host.
    /// </summary>
    public SessionPayloadSchema? PayloadSchema { get; init; }

    public required SessionPayloadSensitivity PayloadSensitivity { get; init; }

    public required SessionPayloadRetention PayloadRetention { get; init; }

    /// <summary>SHA-256 of the original UTF-8 payload when retained or digest-only.</summary>
    public string? PayloadDigest { get; init; }

    public string? PreviousHash { get; init; }

    public string Hash { get; init; } = string.Empty;
}
