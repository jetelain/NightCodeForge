using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

public record TriageReportRequest(
    string Title,
    string Description,
    string? BaseBranch = null);

/// <summary>
/// Service executing untrusted intake triage on the base branch within a RestrictedReadOnly sandbox,
/// validating reproducibility, formulating technical requirements, and proposing backlog items.
/// </summary>
public interface ITriageService
{
    Task<IReadOnlyList<Issue>> TriageExternalReportAsync(
        Workspace workspace,
        TriageReportRequest request,
        CancellationToken cancellationToken = default);
}
