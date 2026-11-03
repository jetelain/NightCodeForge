using System.Text.RegularExpressions;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// HTTP request headers used to correlate transport-level LLM calls with NightCodeForge tasks and sessions.
/// </summary>
public static partial class LlmCorrelationHeaders
{
    public const string SessionId = "X-NCF-Session-Id";
    public const string TaskId = "X-NCF-Task-Id";
    public const string StageId = "X-NCF-Stage-Id";
    public const string DefaultStageId = "execution";

    /// <summary>
    /// Attaches correlation headers to an HttpClient instance.
    /// </summary>
    public static void Apply(HttpClient httpClient, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(SessionId, sessionId.Trim());

        var (taskId, stageId) = ParseSessionId(sessionId);
        if (taskId.HasValue)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(TaskId, taskId.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(stageId))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(StageId, stageId.Trim());
        }
    }

    /// <summary>
    /// Parses task ID and stage ID from standard LlmSessionIds formatted as "ncf-task{taskId}-{stageId}".
    /// </summary>
    public static (int? TaskId, string? StageId) ParseSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return (null, null);

        var match = SessionRegex().Match(sessionId.Trim());
        if (match.Success && int.TryParse(match.Groups[1].Value, out var taskId))
        {
            return (taskId, match.Groups[2].Value);
        }

        return (null, null);
    }

    [GeneratedRegex(@"-task(\d+)-(.*)$")]
    private static partial Regex SessionRegex();
}

