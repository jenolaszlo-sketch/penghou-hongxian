namespace Penghou.Hongxian;

/// <summary>
/// Identifies work owned by an external execution system. The system name is
/// part of identity, so two providers may safely use the same UUID.
/// </summary>
public readonly record struct ExternalOperationReference
{
    public ExternalOperationReference(string system, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        if (id == Guid.Empty)
            throw new ArgumentException("A non-empty external operation ID is required.", nameof(id));
        System = system;
        Id = id;
    }

    public string System { get; }

    public Guid Id { get; }

    public override string ToString() => $"{System}:{Id:D}";
}
