using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

public class TriageSubmissionModel
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public string? ExternalUrl { get; set; }
    public string? StackTrace { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Title) &&
        !string.IsNullOrWhiteSpace(Description);

    public string BuildFullDescription()
    {
        var fullDescription = Description?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(StackTrace))
        {
            fullDescription += $"\n\n### Stack Trace / Error Logs\n```text\n{StackTrace.Trim()}\n```";
        }

        if (!string.IsNullOrWhiteSpace(ExternalUrl))
        {
            fullDescription += $"\n\n*Origin:* [{ExternalUrl.Trim()}]({ExternalUrl.Trim()})";
        }

        return fullDescription;
    }
}

public record TriageExecutionState(
    bool IsRunning,
    string? CurrentActivity,
    IReadOnlyList<string> SecurityBadges,
    IReadOnlyList<ForgeTask> GeneratedTasks,
    string? ErrorMessage);
