namespace Penghou.Hongxian.LatticeDb;

/// <summary>Classifies an optional-provider boundary failure.</summary>
public enum LatticeDbProviderFailure
{
    InvalidConfiguration,
    NativeRuntime,
    NativeOperation
}

/// <summary>
/// Typed diagnostic boundary for LatticeDB configuration, native-runtime, and
/// native-operation failures. Native wrapper types do not cross the provider
/// contract boundary.
/// </summary>
public sealed class LatticeDbProviderException : Exception
{
    public LatticeDbProviderException(
        LatticeDbProviderFailure failure,
        string operation,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure));
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A non-empty operation is required.", nameof(operation));
        if (operation.Length > 200 || operation.Any(char.IsControl))
            throw new ArgumentException(
                "Operations must be bounded and free of control characters.",
                nameof(operation));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A non-empty diagnostic message is required.", nameof(message));

        Failure = failure;
        Operation = operation;
    }

    public LatticeDbProviderFailure Failure { get; }

    public string Operation { get; }
}
