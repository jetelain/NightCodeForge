using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for workspace entity persistence.
/// </summary>
public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the effective agent profile for a stage within a workspace.
    /// Checks for a workspace-dedicated override first (WorkspaceId == workspaceId);
    /// falls back to the global profile (WorkspaceId == null).
    /// </summary>
    Task<AgentProfile?> GetEffectiveAgentProfileAsync(int? workspaceId, string stageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all global agent profiles (WorkspaceId == null).
    /// </summary>
    Task<IReadOnlyList<AgentProfile>> GetGlobalAgentProfilesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory workspace repository for tests and isolated workflows.
/// </summary>
public class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Workspace> _workspaces = new();
    private readonly List<AgentProfile> _globalProfiles = new();
    private int _nextId = 1;

    public Task<Workspace?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_workspaces.TryGetValue(id, out var workspace))
            {
                return Task.FromResult<Workspace?>(workspace);
            }
            return Task.FromResult<Workspace?>(null);
        }
    }

    public Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Workspace> list = _workspaces.Values.ToList();
            return Task.FromResult(list);
        }
    }

    public Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (workspace.Id <= 0)
            {
                workspace.Id = _nextId++;
            }
            else if (workspace.Id >= _nextId)
            {
                _nextId = workspace.Id + 1;
            }

            _workspaces[workspace.Id] = workspace;
            return Task.FromResult(workspace);
        }
    }

    public Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _workspaces[workspace.Id] = workspace;
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _workspaces.Remove(id);
            return Task.CompletedTask;
        }
    }

    public void SeedGlobalProfiles(IEnumerable<AgentProfile> profiles)
    {
        lock (_lock)
        {
            _globalProfiles.Clear();
            _globalProfiles.AddRange(profiles);
        }
    }

    public Task<AgentProfile?> GetEffectiveAgentProfileAsync(int? workspaceId, string stageId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (workspaceId.HasValue && _workspaces.TryGetValue(workspaceId.Value, out var ws))
            {
                var dedicated = ws.AgentProfiles?.FirstOrDefault(p =>
                    string.Equals(p.Id, stageId, StringComparison.OrdinalIgnoreCase) ||
                    (p.StageDirectives != null && p.StageDirectives.ContainsKey(stageId)));
                if (dedicated != null)
                {
                    return Task.FromResult<AgentProfile?>(dedicated);
                }
            }

            var global = _globalProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, stageId, StringComparison.OrdinalIgnoreCase) ||
                (p.StageDirectives != null && p.StageDirectives.ContainsKey(stageId)));

            return Task.FromResult<AgentProfile?>(global);
        }
    }

    public Task<IReadOnlyList<AgentProfile>> GetGlobalAgentProfilesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<AgentProfile> list = _globalProfiles.ToList();
            return Task.FromResult(list);
        }
    }
}
