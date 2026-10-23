namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Connection tuning constants for Ollama HTTP communication.
/// </summary>
public static class OllamaHttpConstants
{
    public static readonly TimeSpan DefaultConnectionLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultKeepAlivePingDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromSeconds(15);
}
