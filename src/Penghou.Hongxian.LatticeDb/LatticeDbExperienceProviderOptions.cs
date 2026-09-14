namespace Penghou.Hongxian.LatticeDb;

/// <summary>Chooses whether the provider opens a file or an isolated memory database.</summary>
public enum LatticeDbLocationKind
{
    File,
    Memory
}

/// <summary>Chooses the native open access policy.</summary>
public enum LatticeDbOpenMode
{
    CreateOrOpen,
    OpenExisting,
    ReadOnly
}

/// <summary>Chooses whether LatticeDB enables its write-ahead log.</summary>
public enum LatticeDbDurability
{
    DurableWal,
    Volatile
}

/// <summary>
/// Explicit lifecycle and native-open choices for the optional LatticeDB
/// experience provider. Storage is disposable projection state and is not an
/// authority for Siming evidence.
/// </summary>
public sealed record LatticeDbExperienceProviderOptions
{
    /// <summary>Opens a file database by default.</summary>
    public LatticeDbLocationKind Location { get; init; } = LatticeDbLocationKind.File;

    /// <summary>
    /// File path when <see cref="Location"/> is <see cref="LatticeDbLocationKind.File"/>.
    /// It is ignored for memory databases.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>Gets the native open mode.</summary>
    public LatticeDbOpenMode OpenMode { get; init; } = LatticeDbOpenMode.CreateOrOpen;

    /// <summary>Gets the native durability mode.</summary>
    public LatticeDbDurability Durability { get; init; } = LatticeDbDurability.DurableWal;

    /// <summary>Gets whether the native file lock is enabled.</summary>
    public bool Lock { get; init; } = true;

    /// <summary>Validates this option set before a provider opens native state.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Location))
            throw new ArgumentOutOfRangeException(nameof(Location));
        if (!Enum.IsDefined(OpenMode))
            throw new ArgumentOutOfRangeException(nameof(OpenMode));
        if (!Enum.IsDefined(Durability))
            throw new ArgumentOutOfRangeException(nameof(Durability));
        if (Location == LatticeDbLocationKind.File && string.IsNullOrWhiteSpace(DatabasePath))
            throw new ArgumentException(
                "A database path is required for a file-backed LatticeDB provider.",
                nameof(DatabasePath));
        if (Location == LatticeDbLocationKind.Memory &&
            !string.IsNullOrWhiteSpace(DatabasePath))
            throw new ArgumentException(
                "A database path must be omitted for a memory-backed provider.",
                nameof(DatabasePath));
        if (Location == LatticeDbLocationKind.Memory &&
            OpenMode != LatticeDbOpenMode.CreateOrOpen)
            throw new ArgumentException(
                "Memory databases can only use create-or-open mode.",
                nameof(OpenMode));
        if (Location == LatticeDbLocationKind.Memory &&
            Durability == LatticeDbDurability.Volatile)
            throw new ArgumentException(
                "The LatticeDB transactional API requires WAL for memory databases.",
                nameof(Durability));
    }
}
