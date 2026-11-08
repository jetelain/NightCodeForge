namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an operation is attempted that is prohibited by the air-gapped security policy.
/// </summary>
public class AirGappedOperationException : InvalidOperationException
{
    public AirGappedOperationException()
        : base("The requested operation is prohibited because NightCodeForge is operating in Air-Gapped mode.")
    {
    }

    public AirGappedOperationException(string message)
        : base(message)
    {
    }

    public AirGappedOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

