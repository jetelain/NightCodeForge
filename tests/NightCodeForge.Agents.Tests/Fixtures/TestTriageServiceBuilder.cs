namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Triage;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

public class TestTriageServiceBuilder
{
    public ITaskRepository TaskRepository { get; set; } = new InMemoryTaskRepository();
    public IWorkspaceRepository WorkspaceRepository { get; set; } = new InMemoryWorkspaceRepository();
    public IHarnessRegistry HarnessRegistry { get; set; } = new Mock<IHarnessRegistry>().Object;
    public ITaskMarkdownSyncService MarkdownSyncService { get; set; } = new Mock<ITaskMarkdownSyncService>().Object;
    public IGlobalOptimizationSettingsService GlobalOptimizationSettings { get; set; } = new FakeGlobalOptimizationSettingsService();
    public ILogger<TriageService> Logger { get; set; } = NullLogger<TriageService>.Instance;

    public TriageService Build()
    {
        return new TriageService(
            TaskRepository,
            WorkspaceRepository,
            HarnessRegistry,
            MarkdownSyncService,
            GlobalOptimizationSettings,
            Logger);
    }

    private class FakeGlobalOptimizationSettingsService : IGlobalOptimizationSettingsService
    {
        private LocalOptimizationFlags _flags = new();
        public LocalOptimizationFlags GetFlags() => _flags;
        public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_flags);
        public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default) { _flags = flags; return Task.CompletedTask; }
        public event Action<LocalOptimizationFlags>? OnFlagsChanged { add { } remove { } }
    }
}
