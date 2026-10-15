namespace NightCodeForge.Web.Models;

public record RepositoryProbeResult(
    bool Exists,
    bool IsGitRepository,
    string? SuggestedName,
    IReadOnlyList<string> AvailableBranches,
    string DetectedBaseBranch,
    string DetectedTechStack,
    bool HasAgentsMarkdown,
    string? WarningMessage,
    bool HasGitAttributes = false,
    bool HasLfGitAttributes = false,
    bool HasEditorConfig = false,
    bool HasLfEditorConfig = false,
    string? CoreAutoCrlf = null,
    string? LineEndingRecommendation = null);
