using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Scoped container managing the currently active workspace context across the Blazor circuit.
/// </summary>
public class WorkspaceStateService
{
    private int? _activeWorkspaceId;
    private Workspace? _activeWorkspace;
    private IReadOnlyList<Workspace> _knownWorkspaces = Array.Empty<Workspace>();

    public int? ActiveWorkspaceId => _activeWorkspaceId;
    public Workspace? ActiveWorkspace => _activeWorkspace;
    public IReadOnlyList<Workspace> KnownWorkspaces => _knownWorkspaces;

    public event Action? OnWorkspaceChanged;
    public event Action? OnOpenWizardRequested;

    public void RequestOpenWizard()
    {
        OnOpenWizardRequested?.Invoke();
    }

    public void SetKnownWorkspaces(IReadOnlyList<Workspace> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        _knownWorkspaces = workspaces;

        if (_activeWorkspaceId.HasValue)
        {
            var refreshed = workspaces.FirstOrDefault(w => w.Id == _activeWorkspaceId.Value);
            if (refreshed != null)
            {
                _activeWorkspace = refreshed;
            }
        }
    }

    public bool TryActivateWorkspaceById(int workspaceId)
    {
        if (workspaceId <= 0)
        {
            return false;
        }

        if (_activeWorkspaceId == workspaceId && _activeWorkspace != null)
        {
            return true;
        }

        var matched = _knownWorkspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (matched != null)
        {
            SetActiveWorkspace(matched);
            return true;
        }

        return false;
    }

    public void SetActiveWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var changed = _activeWorkspaceId != workspace.Id ||
                      _activeWorkspace?.Name != workspace.Name ||
                      _activeWorkspace?.DefaultBaseBranch != workspace.DefaultBaseBranch ||
                      _activeWorkspace?.ExecutionMode != workspace.ExecutionMode;

        _activeWorkspaceId = workspace.Id;
        _activeWorkspace = workspace;

        if (_knownWorkspaces.All(w => w.Id != workspace.Id))
        {
            _knownWorkspaces = _knownWorkspaces.Append(workspace).ToList();
        }

        if (changed)
        {
            OnWorkspaceChanged?.Invoke();
        }
    }

    public void ClearActiveWorkspace()
    {
        var changed = _activeWorkspaceId != null || _activeWorkspace != null;
        _activeWorkspaceId = null;
        _activeWorkspace = null;
        if (changed)
        {
            OnWorkspaceChanged?.Invoke();
        }
    }

    public string GetPath(string? subPath = null) =>
        WorkspaceUrlHelper.BuildPath(_activeWorkspaceId, subPath);

    public string GetNavHref(string? subPath = null) =>
        WorkspaceUrlHelper.BuildNavHref(_activeWorkspaceId, subPath);
}
