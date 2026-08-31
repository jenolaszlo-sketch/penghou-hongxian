namespace Penghou.Hongxian;

/// <summary>
/// Identifies work owned by an external execution system. The system name is
/// part of identity. IDs are opaque ordinal strings so providers are not
/// required to use UUIDs.
/// </summary>
public readonly record struct ExternalOperationReference
{
    public ExternalOperationReference(string system, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
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

    public override string ToString() => $"{System}:{Id}";
}
