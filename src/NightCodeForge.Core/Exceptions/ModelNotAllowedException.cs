using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an LLM model invocation is attempted for a model that is neither discovered
/// via endpoint probing nor explicitly configured in the provider's allowed models list.
/// </summary>
public class ModelNotAllowedException : InvalidOperationException
{
    public ModelNotAllowedException(string modelId, LlmProviderType providerType)
        : base($"Model '{modelId}' is not available in the model catalog for provider '{providerType}'. Verify endpoint model discovery or configure the model and its context size under Settings.")
    {
        ModelId = modelId;
        ProviderType = providerType;
    }

    public string ModelId { get; }
    public LlmProviderType ProviderType { get; }
}
