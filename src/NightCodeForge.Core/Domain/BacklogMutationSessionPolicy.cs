namespace NightCodeForge.Core.Domain;

/// <summary>
/// Execution scope mode controlling how host-level backlog mutation tools behave during an agent session.
/// </summary>
public enum BacklogMutationMode
{
    Ideation = 0,
    Triage = 1
}

/// <summary>
/// Policy metadata flowing through <see cref="AgentSessionContext"/> and tool scopes so
/// host-level backlog mutations operate cleanly under Ideation or Untrusted Intake Triage rules
/// without expanding service constructor signatures.
/// </summary>
public sealed record BacklogMutationSessionPolicy(
    BacklogMutationMode Mode,
    string Actor,
    TaskOrigin? SessionOrigin = null,
    string? DefaultBaseBranch = null,
    string? ExternalSource = null,
    string? ExternalReferenceId = null,
    string? HostRepositoryPath = null,
    string? CloneDirectory = null,
    int? SessionTaskId = null)
{
    public static BacklogMutationSessionPolicy ForIdeation(
        string? hostRepositoryPath = null,
        string? cloneDirectory = null) =>
        new(
            Mode: BacklogMutationMode.Ideation,
            Actor: AuditActors.IdeationAgent,
            HostRepositoryPath: hostRepositoryPath,
            CloneDirectory: cloneDirectory);

    public static BacklogMutationSessionPolicy ForTriage(
        TaskOrigin origin,
        string baseBranch,
        string? externalSource = null,
        string? externalReferenceId = null,
        string? hostRepositoryPath = null,
        string? cloneDirectory = null,
        int? sessionTaskId = null) =>
        new(
            Mode: BacklogMutationMode.Triage,
            Actor: AuditActors.TriageAgent,
            SessionOrigin: origin,
            DefaultBaseBranch: baseBranch,
            ExternalSource: externalSource,
            ExternalReferenceId: externalReferenceId,
            HostRepositoryPath: hostRepositoryPath,
            CloneDirectory: cloneDirectory,
            SessionTaskId: sessionTaskId);
}
