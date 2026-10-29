using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

/// <summary>OllamaRouter request headers (routing pin and session affinity).</summary>
public static class OllamaRoutingHeaders
{
    public const string Target = "X-OllamaRouter-Target";
    public const string SessionId = "X-OllamaRouter-Session-Id";

    /// <summary>Attaches the target pin (unless Auto) and the session id (when provided) as default headers.</summary>
    public static void Apply(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (routingTarget != OllamaRoutingTarget.Auto)
        {
            httpClient.DefaultRequestHeaders.Add(Target, routingTarget.ToString());
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            httpClient.DefaultRequestHeaders.Add(SessionId, sessionId);
        }
    }
}

/// <summary>
/// Builds the LLM session identifiers that OllamaRouter uses for backend affinity and KV-cache reuse.
/// The id must be stable for all turns of one logical conversation.
/// </summary>
public static class LlmSessionIds
{
    public const string Prefix = "ncf";

    /// <summary>Session id shared by every turn of a workflow stage of a task.</summary>
    public static string ForStage(int taskId, string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        return $"{Prefix}-task{taskId}-{stageId}";
    }
}
