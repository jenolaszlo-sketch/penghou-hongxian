using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

/// <summary>
/// Identifies work owned by an external execution system. The system name is
/// part of identity. IDs are opaque ordinal strings so providers are not
/// required to use UUIDs.
/// </summary>
[JsonConverter(typeof(ExternalOperationReferenceJsonConverter))]
public readonly record struct ExternalOperationReference :
    ISpanFormattable,
    IParsable<ExternalOperationReference>
{
    public ExternalOperationReference(string system, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        SessionContractValidation.ValidateExternalOperation(
            system,
            id,
            nameof(system),
            nameof(id));
        if (system.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException(
                "External operation system names cannot contain ':'.",
                nameof(system));
        System = system;
        Id = id;
    }

    public ExternalOperationReference(string system, Guid id)
        : this(
            system,
            id == Guid.Empty
                ? throw new ArgumentException(
                    "A non-empty external operation ID is required.", nameof(id))
                : id.ToString("D"))
    {
    }

    public string System { get; }

    public string Id { get; }

    public static ExternalOperationReference Parse(string value) =>
        Parse(value, null);

    public static ExternalOperationReference Parse(
        string value,
        IFormatProvider? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
            throw new FormatException(
                "External operation references must use the 'system:id' format.");
        return new ExternalOperationReference(
            value[..separator],
            value[(separator + 1)..]);
    }

    public static bool TryParse(
        string? value,
        out ExternalOperationReference result) =>
        TryParse(value, null, out result);

    public static bool TryParse(
        string? value,
        IFormatProvider? provider,
        out ExternalOperationReference result)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 &&
                separator < value.Length - 1 &&
                separator <= SessionContractLimits.ExternalSystemCharacters &&
                value.Length - separator - 1 <=
                    SessionContractLimits.ExternalOperationIdCharacters)
            {
                result = new ExternalOperationReference(
                    value[..separator],
                    value[(separator + 1)..]);
                return true;
            }
        }

        result = default;
        return false;
    }

    public override string ToString() => $"{System}:{Id}";

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        ToString();

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider) =>
        destination.TryWrite($"{System}:{Id}", out charsWritten);
}
