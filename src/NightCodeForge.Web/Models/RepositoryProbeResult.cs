namespace NightCodeForge.Web.Models;

public record RepositoryProbeResult(
    bool Exists,
    bool IsGitRepository,
    string? SuggestedName,
    IReadOnlyList<string> AvailableBranches,
    string DetectedBaseBranch,
    string DetectedTechStack,
    bool HasAgentsMarkdown,
    string? WarningMessage);
