namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an LLM inference or execution operation is attempted while global operations are paused.
/// </summary>
public class OperationsPausedException : InvalidOperationException
{
    public OperationsPausedException(string message = "LLM inference is disabled while global operations are paused.")
        : base(message)
    {
    }
}
