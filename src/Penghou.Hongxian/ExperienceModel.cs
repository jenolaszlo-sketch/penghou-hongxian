using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

/// <summary>Stable identity of one portable experience entity.</summary>
[JsonConverter(typeof(ExperienceEntityIdJsonConverter))]
public readonly record struct ExperienceEntityId : ISpanFormattable, IParsable<ExperienceEntityId>
{
    public ExperienceEntityId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty experience entity ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static ExperienceEntityId New() => new(Guid.CreateVersion7());

    /// <summary>Creates an ID stable across projection rebuilds.</summary>
    public static ExperienceEntityId CreateDeterministic(
        ExperienceProjectionId projectionId,
        string kind,
        string stableKey) =>
        new(ExperienceIdentityHash.Create("entity", projectionId, kind, stableKey));

    public static ExperienceEntityId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ExperienceEntityId(Guid.Parse(value));
    }

    public static ExperienceEntityId Parse(string value, IFormatProvider? provider) => Parse(value);

    public static bool TryParse(string? value, out ExperienceEntityId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(string? value, IFormatProvider? provider, out ExperienceEntityId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new ExperienceEntityId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "D", formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
}

/// <summary>Stable identity of one portable experience relation.</summary>
[JsonConverter(typeof(ExperienceRelationIdJsonConverter))]
public readonly record struct ExperienceRelationId : ISpanFormattable, IParsable<ExperienceRelationId>
{
    public ExperienceRelationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty experience relation ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static ExperienceRelationId New() => new(Guid.CreateVersion7());

    /// <summary>Creates a relation ID stable across projection rebuilds.</summary>
    public static ExperienceRelationId CreateDeterministic(
        ExperienceProjectionId projectionId,
        ExperienceEntityId from,
        ExperienceEntityId to,
        string kind,
        string stableKey) =>
        new(ExperienceIdentityHash.Create(
            "relation", projectionId, kind, $"{from:D}|{to:D}|{stableKey}"));

    public static ExperienceRelationId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ExperienceRelationId(Guid.Parse(value));
    }

    public static ExperienceRelationId Parse(string value, IFormatProvider? provider) => Parse(value);

    public static bool TryParse(string? value, out ExperienceRelationId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(string? value, IFormatProvider? provider, out ExperienceRelationId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new ExperienceRelationId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "D", formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
}

/// <summary>Stable identity of one immutable source-evidence reference.</summary>
[JsonConverter(typeof(ExperienceEvidenceReferenceIdJsonConverter))]
public readonly record struct ExperienceEvidenceReferenceId : ISpanFormattable, IParsable<ExperienceEvidenceReferenceId>
{
    public ExperienceEvidenceReferenceId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty experience evidence reference ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static ExperienceEvidenceReferenceId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ExperienceEvidenceReferenceId(Guid.Parse(value));
    }

    public static ExperienceEvidenceReferenceId Parse(string value, IFormatProvider? provider) => Parse(value);

    public static bool TryParse(string? value, out ExperienceEvidenceReferenceId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(string? value, IFormatProvider? provider, out ExperienceEvidenceReferenceId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new ExperienceEvidenceReferenceId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "D", formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
}

/// <summary>Stable identity for the derivation that produced a record.</summary>
[JsonConverter(typeof(ExperienceDerivationIdJsonConverter))]
public readonly record struct ExperienceDerivationId : ISpanFormattable, IParsable<ExperienceDerivationId>
{
    public ExperienceDerivationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty experience derivation ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static ExperienceDerivationId New() => new(Guid.CreateVersion7());

    /// <summary>Creates a derivation identity stable across projection rebuilds.</summary>
    public static ExperienceDerivationId CreateDeterministic(
        ExperienceProjectionId projectionId,
        string derivationKind,
        string stableKey) =>
        new(ExperienceIdentityHash.Create(
            "derivation", projectionId, derivationKind, stableKey));

    public static ExperienceDerivationId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ExperienceDerivationId(Guid.Parse(value));
    }

    public static ExperienceDerivationId Parse(string value, IFormatProvider? provider) => Parse(value);

    public static bool TryParse(string? value, out ExperienceDerivationId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(string? value, IFormatProvider? provider, out ExperienceDerivationId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new ExperienceDerivationId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "D", formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
}

/// <summary>Bounded limits for the portable experience value model.</summary>
public static class ExperienceContractLimits
{
    public const int KindCharacters = 200;
    public const int PropertyCount = 128;
    public const int PropertyNameCharacters = 200;
    public const int PropertyValueUtf8Bytes = 32_768;
    public const int PropertySetUtf8Bytes = 1_048_576;
    public const int EvidenceReferenceCount = 256;
    public const int IdentityComponentUtf8Bytes = 4_096;
    public const int DerivationNameCharacters = 200;
    public const int DerivationVersionCharacters = 128;
}

/// <summary>Versioned persistence contracts for deterministic experience IDs.</summary>
public static class ExperienceIdentityContracts
{
    public const string DeterministicIdV1 =
        "sha256:penghou-hongxian-experience-id:v1";
}

/// <summary>Immutable location of one event in an authoritative evidence ledger.</summary>
public sealed record ExperienceEvidenceReference
{
    public ExperienceEvidenceReference(
        SessionId sessionId,
        string ledgerIdentity,
        long sequence,
        Guid eventId,
        string eventHash)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("A non-empty source event ID is required.", nameof(eventId));
        SessionContractValidation.ValidateSessionId(sessionId, nameof(sessionId));
        ValidateText(ledgerIdentity, SessionContractLimits.LedgerIdentityCharacters, nameof(ledgerIdentity));
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        ValidateText(eventHash, SessionContractLimits.DigestCharacters, nameof(eventHash));
        Id = new ExperienceEvidenceReferenceId(ExperienceIdentityHash.CreateEvidence(
            sessionId,
            ledgerIdentity,
            sequence,
            eventId));
        SessionId = sessionId;
        LedgerIdentity = ledgerIdentity;
        Sequence = sequence;
        EventId = eventId;
        EventHash = eventHash;
    }

    public ExperienceEvidenceReferenceId Id { get; }
    public SessionId SessionId { get; }
    public string LedgerIdentity { get; }
    public long Sequence { get; }
    public Guid EventId { get; }
    public string EventHash { get; }

    public static ExperienceEvidenceReference FromEvent(
        SessionEvent sessionEvent,
        string ledgerIdentity)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        SessionContractValidation.Validate(sessionEvent);
        return new ExperienceEvidenceReference(
            sessionEvent.SessionId,
            ledgerIdentity,
            sessionEvent.Sequence,
            sessionEvent.EventId,
            sessionEvent.Hash);
    }

    private static void ValidateText(string value, int limit, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        if (value.Length > limit)
            throw new ArgumentOutOfRangeException(parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Control characters are not permitted.", parameterName);
    }
}

/// <summary>Projection and derivation identity attached to every experience record.</summary>
public sealed record ExperienceProvenance
{
    public ExperienceProvenance(ExperienceProjectionDescriptor projection, ExperienceDerivationId derivationId)
    {
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        if (derivationId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty derivation ID is required.", nameof(derivationId));
        DerivationId = derivationId;
    }

    public ExperienceProjectionDescriptor Projection { get; }
    public ExperienceDerivationId DerivationId { get; }
}

/// <summary>Optional claimed interval during which an experience is valid.</summary>
public sealed record ExperienceTemporalValidity
{
    public ExperienceTemporalValidity(DateTimeOffset? validFrom, DateTimeOffset? validTo)
    {
        if (validFrom is null && validTo is null)
            throw new ArgumentException("At least one validity bound is required.");
        if (validFrom is not null && validTo is not null && validFrom > validTo)
            throw new ArgumentException("Validity start cannot be later than validity end.");
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public DateTimeOffset? ValidFrom { get; }
    public DateTimeOffset? ValidTo { get; }
}

/// <summary>Portable experience entity with bounded, JSON-compatible properties.</summary>
public sealed record ExperienceEntity
{
    public ExperienceEntity(
        ExperienceEntityId id,
        string kind,
        ExperienceProvenance provenance,
        IReadOnlyDictionary<string, JsonElement>? properties = null,
        ExperienceTemporalValidity? validity = null,
        IReadOnlyList<ExperienceEvidenceReference>? evidence = null)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("A non-empty entity ID is required.", nameof(id));
        ValidateKind(kind);
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Id = id;
        Kind = kind;
        Properties = SnapshotProperties(properties);
        Validity = validity;
        Evidence = SnapshotEvidence(evidence);
    }

    public ExperienceEntityId Id { get; }
    public string Kind { get; }
    public IReadOnlyDictionary<string, JsonElement> Properties { get; }
    public ExperienceProvenance Provenance { get; }
    public ExperienceTemporalValidity? Validity { get; }
    public IReadOnlyList<ExperienceEvidenceReference> Evidence { get; }

    private static void ValidateKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty kind is required.", nameof(value));
        if (value.Length > ExperienceContractLimits.KindCharacters) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.Any(char.IsControl)) throw new ArgumentException("Control characters are not permitted.", nameof(value));
    }

    internal static IReadOnlyDictionary<string, JsonElement> SnapshotProperties(IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (properties is null || properties.Count == 0)
            return new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        if (properties.Count > ExperienceContractLimits.PropertyCount) throw new ArgumentOutOfRangeException(nameof(properties));
        var copy = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var totalUtf8Bytes = 0L;
        foreach (var pair in properties.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > ExperienceContractLimits.PropertyNameCharacters || pair.Key.Any(char.IsControl))
                throw new ArgumentException("Property names must be bounded and free of control characters.", nameof(properties));
            if (pair.Value.ValueKind == JsonValueKind.Undefined)
                throw new ArgumentException("Undefined JSON values are not portable properties.", nameof(properties));
            var value = pair.Value.Clone();
            var valueUtf8Bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
            if (valueUtf8Bytes > ExperienceContractLimits.PropertyValueUtf8Bytes)
                throw new ArgumentOutOfRangeException(nameof(properties));
            totalUtf8Bytes = checked(totalUtf8Bytes + Encoding.UTF8.GetByteCount(pair.Key) + valueUtf8Bytes);
            if (totalUtf8Bytes > ExperienceContractLimits.PropertySetUtf8Bytes)
                throw new ArgumentOutOfRangeException(nameof(properties));
            copy.Add(pair.Key, value);
        }
        return new ReadOnlyDictionary<string, JsonElement>(copy);
    }

    internal static IReadOnlyList<ExperienceEvidenceReference> SnapshotEvidence(IReadOnlyList<ExperienceEvidenceReference>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
            throw new ArgumentException(
                "At least one immutable evidence reference is required.",
                nameof(evidence));
        if (evidence.Count > ExperienceContractLimits.EvidenceReferenceCount) throw new ArgumentOutOfRangeException(nameof(evidence));
        if (evidence.Any(item => item is null)) throw new ArgumentException("Evidence references cannot be null.", nameof(evidence));
        var ordered = evidence.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal).ToArray();
        if (ordered.Select(item => item.Id).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Evidence references cannot contain duplicate identities.", nameof(evidence));
        return Array.AsReadOnly(ordered);
    }
}

/// <summary>Portable directed relation between two experience entities.</summary>
public sealed record ExperienceRelation
{
    public ExperienceRelation(
        ExperienceRelationId id,
        ExperienceEntityId from,
        ExperienceEntityId to,
        string kind,
        ExperienceProvenance provenance,
        IReadOnlyDictionary<string, JsonElement>? properties = null,
        ExperienceTemporalValidity? validity = null,
        IReadOnlyList<ExperienceEvidenceReference>? evidence = null)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("A non-empty relation ID is required.", nameof(id));
        if (from.Value == Guid.Empty) throw new ArgumentException("A non-empty source entity ID is required.", nameof(from));
        if (to.Value == Guid.Empty) throw new ArgumentException("A non-empty target entity ID is required.", nameof(to));
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("A non-empty kind is required.", nameof(kind));
        if (kind.Length > ExperienceContractLimits.KindCharacters || kind.Any(char.IsControl)) throw new ArgumentException("Relation kind is not bounded.", nameof(kind));
        Id = id;
        From = from;
        To = to;
        Kind = kind;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Properties = ExperienceEntity.SnapshotProperties(properties);
        Validity = validity;
        Evidence = ExperienceEntity.SnapshotEvidence(evidence);
    }

    public ExperienceRelationId Id { get; }
    public ExperienceEntityId From { get; }
    public ExperienceEntityId To { get; }
    public string Kind { get; }
    public IReadOnlyDictionary<string, JsonElement> Properties { get; }
    public ExperienceProvenance Provenance { get; }
    public ExperienceTemporalValidity? Validity { get; }
    public IReadOnlyList<ExperienceEvidenceReference> Evidence { get; }
}

internal static class ExperienceIdentityHash
{
    public static Guid Create(
        string category,
        ExperienceProjectionId projectionId,
        string kind,
        string stableKey)
    {
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty projection ID is required.", nameof(projectionId));
        ValidateComponent(category, nameof(category));
        ValidateComponent(kind, nameof(kind));
        ValidateComponent(stableKey, nameof(stableKey));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, ExperienceIdentityContracts.DeterministicIdV1);
        AppendUtf8(hash, category);
        AppendUtf8(hash, projectionId.ToString());
        AppendUtf8(hash, kind);
        AppendUtf8(hash, stableKey);
        return CreateUuid(hash.GetHashAndReset());
    }

    public static Guid CreateEvidence(
        SessionId sessionId,
        string ledgerIdentity,
        long sequence,
        Guid eventId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, ExperienceIdentityContracts.DeterministicIdV1);
        AppendUtf8(hash, "evidence");
        AppendUtf8(hash, sessionId.ToString());
        AppendUtf8(hash, ledgerIdentity);
        AppendInt64(hash, sequence);
        AppendUtf8(hash, eventId.ToString("D"));
        return CreateUuid(hash.GetHashAndReset());
    }

    private static Guid CreateUuid(byte[] hash)
    {
        var bytes = hash[..16];
        // RFC 9562 UUID version 8, with the RFC variant bits. This is a
        // deterministic UUID, not a time-ordered UUID, by design.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Control characters are not permitted.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > ExperienceContractLimits.IdentityComponentUtf8Bytes)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Identity components cannot exceed " +
                $"{ExperienceContractLimits.IdentityComponentUtf8Bytes} UTF-8 bytes.");
    }
}
