using System.Globalization;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

[JsonConverter(typeof(SessionIdJsonConverter))]
public readonly record struct SessionId : ISpanFormattable, IParsable<SessionId>
{
    public SessionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static SessionId New() => new(Guid.CreateVersion7());

    public static SessionId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SessionId(Guid.Parse(value));
    }

    public static SessionId Parse(string value, IFormatProvider? provider) =>
        Parse(value);

    public static bool TryParse(string? value, out SessionId result) =>
        TryParse(value, null, out result);

    public static bool TryParse(
        string? value,
        IFormatProvider? provider,
        out SessionId result)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            result = new SessionId(parsed);
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
