using System.Text;

namespace Penghou.Hongxian;

/// <summary>
/// Stable bounds applied by Hongxian persistence providers. Payload providers
/// may impose a lower transport limit, but must not silently accept values that
/// exceed these portable contract limits.
/// </summary>
public static class SessionContractLimits
{
    public const int ParticipantKindCharacters = 64;
    public const int ParticipantProviderCharacters = 128;
    public const int ParticipantSubjectCharacters = 1_024;
    public const int ParticipantDisplayNameCharacters = 256;
    public const int EventTypeCharacters = 200;
    public const int IdempotencyKeyCharacters = 512;
    public const int CrossSystemReferenceCount = 64;
    public const int CrossSystemReferenceNameCharacters = 200;
    public const int CrossSystemReferenceValueCharacters = 2_048;
    public const int PayloadUtf8Bytes = 1_048_576;
    public const int ExternalSystemCharacters = 128;
    public const int ExternalOperationIdCharacters = 1_024;
    public const int ContextIdCharacters = 1_024;
    public const int ResourceIdCharacters = 2_048;
    public const int RevisionCharacters = 1_024;
    public const int OperationKindCharacters = 200;
    public const int ApplicationPhaseCharacters = 200;
    public const int OperationParticipantCharacters = 200;
    public const int OperationIdentityCharacters = 2_048;
    public const int DigestCharacters = 256;
    public const int SuggestedActionCodeCharacters = 200;
    public const int ReasonCodeCharacters = 200;
    public const int NarrativeCharacters = 8_192;
}

/// <summary>Shared provider-neutral validation for event persistence boundaries.</summary>
public static class SessionContractValidation
{
    public static void Validate(SessionEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSessionId(request.SessionId, nameof(request));
        Validate(request.Participant, nameof(request));
        ValidateRequired(
            request.EventType,
            SessionContractLimits.EventTypeCharacters,
            nameof(request.EventType));
        ValidateOptional(
            request.IdempotencyKey,
            SessionContractLimits.IdempotencyKeyCharacters,
            nameof(request.IdempotencyKey));
        ValidateOptionalGuid(request.EventId, nameof(request.EventId));
        ValidateOptionalGuid(request.CausationId, nameof(request.CausationId));
        ValidateOptionalGuid(request.CorrelationId, nameof(request.CorrelationId));
        if (request.OccurredAt == default)
            throw new ArgumentException(
                "A caller-supplied occurrence-time claim is required.",
                nameof(request));
        if (!Enum.IsDefined(request.PayloadSensitivity))
            throw new ArgumentOutOfRangeException(nameof(request.PayloadSensitivity));
        if (!Enum.IsDefined(request.PayloadRetention))
            throw new ArgumentOutOfRangeException(nameof(request.PayloadRetention));
        request.PayloadSchema?.Validate();
        if (request.Payload is not null && request.PayloadJson is not null)
            throw new ArgumentException(
                "Specify either a JSON-tree payload or legacy PayloadJson, not both.",
                nameof(request));

        var payloadBytes = request.Payload is { } payload
            ? Encoding.UTF8.GetByteCount(payload.GetRawText())
            : request.PayloadJson is null
                ? 0
                : Encoding.UTF8.GetByteCount(request.PayloadJson);
        if (payloadBytes > SessionContractLimits.PayloadUtf8Bytes)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Event payloads cannot exceed {SessionContractLimits.PayloadUtf8Bytes} UTF-8 bytes.");

        ValidateReferences(request.CrossSystemRefs, nameof(request.CrossSystemRefs));

        if (request.ExpectedHead is { } head)
        {
            ValidateRequired(head.LedgerIdentity, 200, nameof(request.ExpectedHead));
            ValidateRequired(head.Hash, 256, nameof(request.ExpectedHead));
            if (head.Sequence < 0)
                throw new ArgumentOutOfRangeException(nameof(request.ExpectedHead));
        }
    }

    public static void Validate(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (sessionEvent.SchemaVersion is < SessionEventEnvelopeSchema.MinimumSupportedVersion or
            > SessionEventEnvelopeSchema.CurrentVersion)
            throw new UnsupportedSessionEventSchemaException(sessionEvent.SchemaVersion);
        if (sessionEvent.Sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionEvent.Sequence));
        if (sessionEvent.EventId == Guid.Empty)
            throw new ArgumentException("A non-empty event ID is required.", nameof(sessionEvent));
        ValidateSessionId(sessionEvent.SessionId, nameof(sessionEvent));
        Validate(sessionEvent.Participant, nameof(sessionEvent));
        ValidateRequired(
            sessionEvent.EventType,
            SessionContractLimits.EventTypeCharacters,
            nameof(sessionEvent.EventType));
        if (sessionEvent.OccurredAt == default || sessionEvent.CommittedAt == default)
            throw new ArgumentException(
                "Occurrence and commit timestamps are required.",
                nameof(sessionEvent));
        ValidateOptionalGuid(sessionEvent.CausationId, nameof(sessionEvent.CausationId));
        ValidateOptionalGuid(sessionEvent.CorrelationId, nameof(sessionEvent.CorrelationId));
        ValidateOptional(
            sessionEvent.IdempotencyKey,
            SessionContractLimits.IdempotencyKeyCharacters,
            nameof(sessionEvent.IdempotencyKey));
        ValidateOptional(
            sessionEvent.PreviousHash,
            SessionContractLimits.DigestCharacters,
            nameof(sessionEvent.PreviousHash));
        ValidateRequired(
            sessionEvent.Hash,
            SessionContractLimits.DigestCharacters,
            nameof(sessionEvent.Hash));
        ValidateOptional(
            sessionEvent.PayloadDigest,
            SessionContractLimits.DigestCharacters,
            nameof(sessionEvent.PayloadDigest));
        if (!Enum.IsDefined(sessionEvent.PayloadSensitivity))
            throw new ArgumentOutOfRangeException(nameof(sessionEvent.PayloadSensitivity));
        if (!Enum.IsDefined(sessionEvent.PayloadRetention))
            throw new ArgumentOutOfRangeException(nameof(sessionEvent.PayloadRetention));
        sessionEvent.PayloadSchema?.Validate();
        ValidateReferences(sessionEvent.CrossSystemRefs, nameof(sessionEvent.CrossSystemRefs));
    }

    public static void Validate(
        SessionParticipantAttribution participant,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(participant, parameterName);
        ValidateRequired(
            participant.Kind,
            SessionContractLimits.ParticipantKindCharacters,
            parameterName);
        ValidateRequired(
            participant.Provider,
            SessionContractLimits.ParticipantProviderCharacters,
            parameterName);
        ValidateRequired(
            participant.Subject,
            SessionContractLimits.ParticipantSubjectCharacters,
            parameterName);
        ValidateOptional(
            participant.DisplayName,
            SessionContractLimits.ParticipantDisplayNameCharacters,
            parameterName);
    }

    public static void ValidateSessionIdentity(string contextId, string resourceId)
    {
        ValidateRequired(
            contextId,
            SessionContractLimits.ContextIdCharacters,
            nameof(contextId));
        ValidateRequired(
            resourceId,
            SessionContractLimits.ResourceIdCharacters,
            nameof(resourceId));
    }

    public static void ValidateSessionId(SessionId sessionId, string parameterName)
    {
        if (sessionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", parameterName);
    }

    public static void ValidateRevision(string? revision, string parameterName)
    {
        if (revision is null) return;
        ValidateRequired(
            revision,
            SessionContractLimits.RevisionCharacters,
            parameterName);
    }

    public static void Validate(StartCrossStoreOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", nameof(request));
        ValidateExternalOperation(
            request.ExternalOperation.System,
            request.ExternalOperation.Id,
            nameof(request),
            nameof(request));
        ValidateRequired(
            request.Kind,
            SessionContractLimits.OperationKindCharacters,
            nameof(request.Kind));
        ValidateRequired(
            request.IdempotencyKey,
            SessionContractLimits.IdempotencyKeyCharacters,
            nameof(request.IdempotencyKey));
        ValidateOptional(
            request.InitialApplicationPhase,
            SessionContractLimits.ApplicationPhaseCharacters,
            nameof(request.InitialApplicationPhase));
        if (request.StartedAt == default)
            throw new ArgumentException("An operation start time is required.", nameof(request));
        if (request.OperationId is { Value: var operationId } && operationId == Guid.Empty)
            throw new ArgumentException(
                "A non-empty cross-store operation ID is required when supplied.",
                nameof(request));
    }

    public static void Validate(CrossStoreParticipantReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateRequired(
            receipt.Participant,
            SessionContractLimits.OperationParticipantCharacters,
            nameof(receipt.Participant));
        ValidateRequired(
            receipt.IdempotencyKey,
            SessionContractLimits.IdempotencyKeyCharacters,
            nameof(receipt.IdempotencyKey));
        ValidateOptional(
            receipt.BeforeIdentity,
            SessionContractLimits.OperationIdentityCharacters,
            nameof(receipt.BeforeIdentity));
        ValidateOptional(
            receipt.AfterIdentity,
            SessionContractLimits.OperationIdentityCharacters,
            nameof(receipt.AfterIdentity));
        ValidateOptional(
            receipt.ResultHash,
            SessionContractLimits.DigestCharacters,
            nameof(receipt.ResultHash));
        ValidateOptional(
            receipt.SuggestedActionCode,
            SessionContractLimits.SuggestedActionCodeCharacters,
            nameof(receipt.SuggestedActionCode));
        if (!Enum.IsDefined(receipt.State))
            throw new ArgumentOutOfRangeException(nameof(receipt.State));
        if (receipt.RecordedAt == default)
            throw new ArgumentException("A participant receipt time is required.", nameof(receipt));
    }

    public static void ValidateOperationTransition(
        CrossStoreOperationState targetState,
        DateTimeOffset occurredAt,
        string? applicationPhase,
        string? reasonCode)
    {
        if (!Enum.IsDefined(targetState))
            throw new ArgumentOutOfRangeException(nameof(targetState));
        if (occurredAt == default)
            throw new ArgumentException("A transition time is required.", nameof(occurredAt));
        ValidateOptional(
            applicationPhase,
            SessionContractLimits.ApplicationPhaseCharacters,
            nameof(applicationPhase));
        ValidateOptional(
            reasonCode,
            SessionContractLimits.ReasonCodeCharacters,
            nameof(reasonCode));
    }

    internal static void ValidateExternalOperation(
        string system,
        string id,
        string systemParameterName,
        string idParameterName)
    {
        ValidateRequired(
            system,
            SessionContractLimits.ExternalSystemCharacters,
            systemParameterName);
        ValidateRequired(
            id,
            SessionContractLimits.ExternalOperationIdCharacters,
            idParameterName);
    }

    internal static void ValidateReasonCode(string value, string parameterName) =>
        ValidateRequired(value, SessionContractLimits.ReasonCodeCharacters, parameterName);

    internal static void ValidateNarrative(string value, string parameterName) =>
        ValidateRequired(value, SessionContractLimits.NarrativeCharacters, parameterName);

    internal static void ValidateActionCode(string value, string parameterName) =>
        ValidateRequired(
            value,
            SessionContractLimits.SuggestedActionCodeCharacters,
            parameterName);

    internal static void ValidateOptionalResourceIdentity(
        string? value,
        string parameterName) =>
        ValidateOptional(
            value,
            SessionContractLimits.OperationIdentityCharacters,
            parameterName);

    private static void ValidateOptionalGuid(Guid? value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "An optional identifier must be non-empty when supplied.",
                parameterName);
    }

    private static void ValidateReferences(
        IReadOnlyDictionary<string, string>? references,
        string parameterName)
    {
        if (references is null) return;
        if (references.Count > SessionContractLimits.CrossSystemReferenceCount)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Events cannot contain more than {SessionContractLimits.CrossSystemReferenceCount} cross-system references.");
        foreach (var reference in references)
        {
            ValidateRequired(
                reference.Key,
                SessionContractLimits.CrossSystemReferenceNameCharacters,
                parameterName);
            ValidateBounded(
                reference.Value,
                SessionContractLimits.CrossSystemReferenceValueCharacters,
                parameterName);
        }
    }

    private static void ValidateOptional(
        string? value,
        int maximumCharacters,
        string parameterName)
    {
        if (value is null) return;
        ValidateRequired(value, maximumCharacters, parameterName);
    }

    private static void ValidateRequired(
        string? value,
        int maximumCharacters,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        if (value.Length > maximumCharacters)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Values cannot exceed {maximumCharacters} characters.");
    }

    private static void ValidateBounded(
        string? value,
        int maximumCharacters,
        string parameterName)
    {
        if (value is null)
            throw new ArgumentException("A non-null value is required.", parameterName);
        if (value.Length > maximumCharacters)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Values cannot exceed {maximumCharacters} characters.");
    }
}
