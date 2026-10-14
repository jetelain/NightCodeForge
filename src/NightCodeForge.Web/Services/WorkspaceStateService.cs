using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Scoped container managing the currently active workspace context across the Blazor circuit.
/// </summary>
public class WorkspaceStateService
{
    private int? _activeWorkspaceId;
    private Workspace? _activeWorkspace;

    public int? ActiveWorkspaceId => _activeWorkspaceId;
    public Workspace? ActiveWorkspace => _activeWorkspace;

    public event Action? OnWorkspaceChanged;

    public void SetActiveWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _activeWorkspaceId = workspace.Id;
        _activeWorkspace = workspace;
        OnWorkspaceChanged?.Invoke();
    }

    public void ClearActiveWorkspace()
    {
        _activeWorkspaceId = null;
        _activeWorkspace = null;
        OnWorkspaceChanged?.Invoke();
    }
}
