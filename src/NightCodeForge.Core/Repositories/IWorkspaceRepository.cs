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
}

/// <summary>
/// Thread-safe in-memory workspace repository for tests and isolated workflows.
/// </summary>
public class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Workspace> _workspaces = new();
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
}
