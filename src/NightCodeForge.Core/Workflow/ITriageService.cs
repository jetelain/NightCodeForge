using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

public record TriageReportRequest(
    string Title,
    string Description,
    string? BaseBranch = null,
    TaskOrigin Origin = TaskOrigin.UntrustedIntake,
    string? ExternalSource = null,
    string? ExternalReferenceId = null);

/// <summary>
/// Consolidated outcome of an untrusted intake triage session, including the persisted triage session task ID
/// and surviving backlog tasks created or updated during the session.
/// </summary>
public sealed record TriageSessionResult(
    int TriageTaskId,
    IReadOnlyList<ForgeTask> Tasks,
    IReadOnlySet<int> CreatedTaskIds,
    IReadOnlySet<int> UpdatedTaskIds,
    IReadOnlySet<int> RemovedTaskIds);

/// <summary>
/// Service executing untrusted intake triage on the base branch within a RestrictedReadOnly sandbox,
/// validating reproducibility, formulating technical requirements, and proposing backlog items.
/// </summary>
public interface ITriageService
{
    Task<IReadOnlyList<ForgeTask>> TriageExternalReportAsync(
        Workspace workspace,
        TriageReportRequest request,
        CancellationToken cancellationToken = default);

    Task<TriageSessionResult> RunTriageSessionAsync(
        Workspace workspace,
        TriageReportRequest request,
        Action<int>? onTriageTaskCreated = null,
        CancellationToken cancellationToken = default);
}
