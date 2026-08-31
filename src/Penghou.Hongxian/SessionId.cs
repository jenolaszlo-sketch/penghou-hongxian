namespace Penghou.Hongxian;

public readonly record struct SessionId
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

    public override string ToString() => Value.ToString("D");
}
