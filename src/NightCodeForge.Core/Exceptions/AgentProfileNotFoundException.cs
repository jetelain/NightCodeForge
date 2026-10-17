namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an agent profile cannot be found in the database for a workspace or global context.
/// </summary>
public class AgentProfileNotFoundException : InvalidOperationException
{
    public string ProfileId { get; }
    public int? WorkspaceId { get; }

    public AgentProfileNotFoundException(string profileId, int? workspaceId = null)
        : base($"Agent profile '{profileId}' was not found in the database for workspace #{workspaceId?.ToString() ?? "global"}.")
    {
        ProfileId = profileId;
        WorkspaceId = workspaceId;
    }

    public AgentProfileNotFoundException(string profileId, int? workspaceId, Exception? innerException)
        : base($"Agent profile '{profileId}' was not found in the database for workspace #{workspaceId?.ToString() ?? "global"}.", innerException)
    {
        ProfileId = profileId;
        WorkspaceId = workspaceId;
    }
}
