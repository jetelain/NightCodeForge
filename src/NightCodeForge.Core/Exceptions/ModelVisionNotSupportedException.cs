using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a text-only model is asked to inspect an image input via canonical <c>read</c>,
/// upfront user turn attachment, or ACP image prompt block without vision capability.
/// </summary>
public class ModelVisionNotSupportedException : InvalidOperationException
{
    public string ModelId { get; }
    public string? VisualInput { get; }

    public ModelVisionNotSupportedException(string modelId, string? visualInput = null, string? message = null)
        : base(message ?? BuildDefaultMessage(modelId, visualInput))
    {
        ModelId = modelId ?? string.Empty;
        VisualInput = visualInput;
    }

    public ModelVisionNotSupportedException(string modelId, string? visualInput, string message, Exception innerException)
        : base(message, innerException)
    {
        ModelId = modelId ?? string.Empty;
        VisualInput = visualInput;
    }

    private static string BuildDefaultMessage(string modelId, string? visualInput)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(modelId) ? "(default)" : modelId;
        var inputDetail = string.IsNullOrWhiteSpace(visualInput)
            ? string.Empty
            : $" (attempted to inspect '{visualInput}')";
        return $"Model '{effectiveModel}' does not support vision inputs{inputDetail}. Select a vision-capable model (marked with {VisionConstants.VisionBadgeLabel}) or remove image attachments.";
    }
}
