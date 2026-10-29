namespace NightCodeForge.Core.Domain;

/// <summary>
/// Central compile-time model and authentication defaults.
/// </summary>
public static class KnownDefaultModels
{
    /// <summary>
    /// Factory baseline model identifier applied when no default model is specified
    /// and dynamic catalog probing is unavailable or returns an empty catalog.
    /// </summary>
    public const string DefaultBaseline = "Qwen3.8-27B:latest";

    /// <summary>
    /// Fallback dummy API key used for unauthenticated local endpoints (LM Studio, local vLLM)
    /// to satisfy SDK ApiKeyCredential validation without throwing ArgumentException.
    /// </summary>
    public const string LocalAuthBypassKey = "local-auth-bypass";
}
