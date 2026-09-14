using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

/// <summary>Stable identity of one logical experience projection.</summary>
[JsonConverter(typeof(ExperienceProjectionIdJsonConverter))]
public readonly record struct ExperienceProjectionId :
    ISpanFormattable,
    IParsable<ExperienceProjectionId>
{
    public ExperienceProjectionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "A non-empty experience projection ID is required.",
                nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static ExperienceProjectionId New() => new(Guid.CreateVersion7());

    public static ExperienceProjectionId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ExperienceProjectionId(Guid.Parse(value));
    }

    public static ExperienceProjectionId Parse(
        string value,
        IFormatProvider? provider) =>
        Parse(value);

    public static bool TryParse(string? value, out ExperienceProjectionId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(
        string? value,
        IFormatProvider? provider,
        out ExperienceProjectionId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new ExperienceProjectionId(parsed);
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

/// <summary>
/// The verified head of one independently ordered session evidence stream.
/// </summary>
public sealed record SessionEvidencePosition
{
    public SessionEvidencePosition(
        SessionId sessionId,
        SessionLedgerHead sessionLedgerHead)
    {
        SessionContractValidation.ValidateSessionId(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(sessionLedgerHead);
        ValidateHead(sessionLedgerHead, nameof(sessionLedgerHead));

        SessionId = sessionId;
        SessionLedgerHead = sessionLedgerHead;
    }

    public SessionId SessionId { get; }

    public SessionLedgerHead SessionLedgerHead { get; }

    private static void ValidateHead(SessionLedgerHead head, string parameterName)
    {
        ValidateBoundedText(
            head.LedgerIdentity,
            SessionContractLimits.LedgerIdentityCharacters,
            parameterName);
        if (head.Sequence < 0)
            throw new ArgumentOutOfRangeException(parameterName);
        ValidateBoundedText(
            head.Hash,
            SessionContractLimits.DigestCharacters,
            parameterName);
    }

    private static void ValidateBoundedText(
        string value,
        int maximumCharacters,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        if (value.Length > maximumCharacters)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Values cannot exceed {maximumCharacters} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Control characters are not permitted.", parameterName);
    }
}

/// <summary>
/// A canonical vector of independently verified session heads. It deliberately
/// has no global sequence because the source ledgers are independently ordered.
/// </summary>
public sealed class ExperienceProjectionPosition
{
    public const string HashContractVersion =
        "sha256:penghou-hongxian-experience-position:v1";

    public ExperienceProjectionPosition(IEnumerable<SessionEvidencePosition> streams)
    {
        var normalized = Normalize(streams);
        Streams = new ReadOnlyCollection<SessionEvidencePosition>(normalized);
        Digest = ComputeDigestCore(normalized);
    }

    public ExperienceProjectionPosition(
        IEnumerable<SessionEvidencePosition> streams,
        string digest)
        : this(streams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        if (!string.Equals(Digest, digest, StringComparison.Ordinal))
            throw new ExperienceProjectionPositionValidationException(
                "Position digest does not match its canonical streams.");
    }

    public IReadOnlyList<SessionEvidencePosition> Streams { get; }

    public string Digest { get; }

    public static string ComputeDigest(IEnumerable<SessionEvidencePosition> streams) =>
        ComputeDigestCore(Normalize(streams));

    private static List<SessionEvidencePosition> Normalize(
        IEnumerable<SessionEvidencePosition> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);

        var normalized = streams
            .Take(SessionContractLimits.ProjectionStreamCount + 1)
            .ToList();
        if (normalized.Count == 0)
            throw new ExperienceProjectionPositionValidationException(
                "At least one evidence stream is required.");
        if (normalized.Count > SessionContractLimits.ProjectionStreamCount)
            throw new ExperienceProjectionPositionValidationException(
                $"A projection position cannot exceed " +
                $"{SessionContractLimits.ProjectionStreamCount} streams.");
        if (normalized.Any(stream => stream is null))
            throw new ExperienceProjectionPositionValidationException(
                "A projection position cannot contain a null stream.");
        if (normalized.GroupBy(stream => stream.SessionId).Any(group => group.Count() > 1))
            throw new ExperienceProjectionPositionValidationException(
                "A projection position cannot contain duplicate sessions.");

        normalized.Sort(static (left, right) => string.CompareOrdinal(
            left.SessionId.ToString(),
            right.SessionId.ToString()));
        return normalized;
    }

    private static string ComputeDigestCore(IReadOnlyList<SessionEvidencePosition> streams)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, HashContractVersion);
        AppendInt32(hash, streams.Count);
        foreach (var stream in streams)
        {
            AppendUtf8(hash, stream.SessionId.ToString());
            AppendUtf8(hash, stream.SessionLedgerHead.LedgerIdentity);
            AppendInt64(hash, stream.SessionLedgerHead.Sequence);
            AppendUtf8(hash, stream.SessionLedgerHead.Hash);
        }

        return $"{HashContractVersion}:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// Stable identity and interpretation version of one logical experience
/// projection. Provider storage details are deliberately excluded.
/// </summary>
public sealed record ExperienceProjectionDescriptor
{
    public ExperienceProjectionDescriptor(
        ExperienceProjectionId projectionId,
        string provider,
        string projectorName,
        int schemaVersion,
        string policyVersion)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException(
                "A non-empty experience projection ID is required.",
                nameof(projectionId));
        ValidateIdentifier(
            provider,
            SessionContractLimits.ProjectionProviderCharacters,
            nameof(provider));
        ValidateIdentifier(
            projectorName,
            SessionContractLimits.ProjectorNameCharacters,
            nameof(projectorName));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ValidateIdentifier(
            policyVersion,
            SessionContractLimits.PolicyVersionCharacters,
            nameof(policyVersion));

        ProjectionId = projectionId;
        Provider = provider;
        ProjectorName = projectorName;
        SchemaVersion = schemaVersion;
        PolicyVersion = policyVersion;
    }

    public ExperienceProjectionId ProjectionId { get; }

    public string Provider { get; }

    public string ProjectorName { get; }

    public int SchemaVersion { get; }

    public string PolicyVersion { get; }

    private static void ValidateIdentifier(
        string value,
        int maximumCharacters,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        if (value.Length > maximumCharacters)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Identifiers cannot exceed {maximumCharacters} characters.");
        if (!IsPortableIdentifier(value))
            throw new ArgumentException(
                "Identifiers must use lowercase portable identifier characters.",
                parameterName);
    }

    private static bool IsPortableIdentifier(string value)
    {
        static bool IsLowerAsciiAlphaNumeric(char character) =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9';

        if (!IsLowerAsciiAlphaNumeric(value[0])) return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsLowerAsciiAlphaNumeric(character) &&
                character is not ('.' or '-' or '_' or ':' or '/'))
                return false;
        }

        return true;
    }
}

/// <summary>A durable provider checkpoint covering one canonical position.</summary>
public sealed record ExperienceProjectionCheckpoint
{
    public ExperienceProjectionCheckpoint(
        ExperienceProjectionDescriptor descriptor,
        ExperienceProjectionPosition position,
        long version)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(position);
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));

        Descriptor = descriptor;
        Position = position;
        Version = version;
    }

    public ExperienceProjectionDescriptor Descriptor { get; }

    public ExperienceProjectionPosition Position { get; }

    public long Version { get; }
}

public sealed record AdvanceExperienceProjectionRequest(
    ExperienceProjectionDescriptor Descriptor,
    ExperienceProjectionPosition Position,
    long ExpectedVersion);

/// <summary>
/// Provider checkpoint persistence. Implementations must apply
/// <see cref="ExperienceProjectionCheckpointRules"/> atomically with their
/// checkpoint write.
/// </summary>
public interface IExperienceProjectionCheckpointStore
{
    Task<ExperienceProjectionCheckpoint?> GetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default);

    Task<ExperienceProjectionCheckpoint> AdvanceAsync(
        AdvanceExperienceProjectionRequest request,
        CancellationToken cancellationToken = default);
}

public enum ExperienceProjectionCheckpointFailure
{
    VersionConflict,
    DescriptorMismatch,
    PositionRegression,
    StreamRegression,
    LedgerIdentityConflict,
    SameSequenceDifferentHash,
    InvalidRequest,
    VersionExhausted
}

public sealed class ExperienceProjectionCheckpointException : Exception
{
    public ExperienceProjectionCheckpointException(
        ExperienceProjectionCheckpointFailure failure,
        string message)
        : base(message)
    {
        Failure = failure;
    }

    public ExperienceProjectionCheckpointFailure Failure { get; }
}

public sealed class ExperienceProjectionPositionValidationException(string message)
    : Exception(message);

/// <summary>Provider-neutral checkpoint transition rules.</summary>
public static class ExperienceProjectionCheckpointRules
{
    public static ExperienceProjectionCheckpoint Validate(
        ExperienceProjectionCheckpoint? current,
        AdvanceExperienceProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Descriptor);
        ArgumentNullException.ThrowIfNull(request.Position);
        if (request.ExpectedVersion < 0)
            throw Failure(
                ExperienceProjectionCheckpointFailure.InvalidRequest,
                "Expected version cannot be negative.");

        if (current is null)
        {
            if (request.ExpectedVersion != 0)
                throw Failure(
                    ExperienceProjectionCheckpointFailure.VersionConflict,
                    "A new checkpoint requires expected version zero.");
            return new ExperienceProjectionCheckpoint(
                request.Descriptor,
                request.Position,
                version: 1);
        }

        if (current.Descriptor != request.Descriptor)
            throw Failure(
                ExperienceProjectionCheckpointFailure.DescriptorMismatch,
                "The stored projector descriptor differs from the requested descriptor.");

        ValidateAdvancement(current.Position, request.Position);

        // A retry of an already committed position is idempotent even when the
        // caller still carries the pre-commit expected version.
        if (string.Equals(
                current.Position.Digest,
                request.Position.Digest,
                StringComparison.Ordinal))
            return current;

        if (current.Version != request.ExpectedVersion)
            throw Failure(
                ExperienceProjectionCheckpointFailure.VersionConflict,
                $"Expected checkpoint version {request.ExpectedVersion}, " +
                $"but observed {current.Version}.");
        if (current.Version == long.MaxValue)
            throw Failure(
                ExperienceProjectionCheckpointFailure.VersionExhausted,
                "Checkpoint version cannot advance beyond Int64.MaxValue.");

        return new ExperienceProjectionCheckpoint(
            current.Descriptor,
            request.Position,
            current.Version + 1);
    }

    private static void ValidateAdvancement(
        ExperienceProjectionPosition current,
        ExperienceProjectionPosition requested)
    {
        var requestedBySession = requested.Streams.ToDictionary(stream => stream.SessionId);
        foreach (var currentStream in current.Streams)
        {
            if (!requestedBySession.TryGetValue(currentStream.SessionId, out var requestedStream))
                throw Failure(
                    ExperienceProjectionCheckpointFailure.PositionRegression,
                    $"Projection position dropped session '{currentStream.SessionId}'.");

            var currentHead = currentStream.SessionLedgerHead;
            var requestedHead = requestedStream.SessionLedgerHead;
            if (!string.Equals(
                    currentHead.LedgerIdentity,
                    requestedHead.LedgerIdentity,
                    StringComparison.Ordinal))
                throw Failure(
                    ExperienceProjectionCheckpointFailure.LedgerIdentityConflict,
                    $"Ledger identity changed for session '{currentStream.SessionId}'.");
            if (requestedHead.Sequence < currentHead.Sequence)
                throw Failure(
                    ExperienceProjectionCheckpointFailure.StreamRegression,
                    $"Evidence stream '{currentStream.SessionId}' regressed from " +
                    $"sequence {currentHead.Sequence} to {requestedHead.Sequence}.");
            if (requestedHead.Sequence == currentHead.Sequence &&
                !string.Equals(
                    currentHead.Hash,
                    requestedHead.Hash,
                    StringComparison.Ordinal))
                throw Failure(
                    ExperienceProjectionCheckpointFailure.SameSequenceDifferentHash,
                    $"Evidence stream '{currentStream.SessionId}' changed hash at " +
                    $"sequence {currentHead.Sequence}.");
        }
    }

    private static ExperienceProjectionCheckpointException Failure(
        ExperienceProjectionCheckpointFailure failure,
        string message) =>
        new(failure, message);
}
