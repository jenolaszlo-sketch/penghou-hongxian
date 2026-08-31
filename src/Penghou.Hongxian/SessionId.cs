namespace Penghou.Hongxian;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.CreateVersion7());

    public static SessionId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SessionId(Guid.Parse(value));
    }

    public override string ToString() => Value.ToString("D");
}
