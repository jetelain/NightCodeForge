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
    public IWorkspacePathResolver PathResolver { get; set; } = CreateDefaultPathResolver();
    public IGlobalOptimizationSettingsService GlobalOptimizationSettings { get; set; } = new FakeGlobalOptimizationSettingsService();
    public ILogger<TriageService> Logger { get; set; } = NullLogger<TriageService>.Instance;

    private static IWorkspacePathResolver CreateDefaultPathResolver()
    {
        var mock = new Mock<IWorkspacePathResolver>();
        mock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        mock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns<int>(id => $"~/.nightcodeforge/wks/{id}/clone");
        return mock.Object;
    }

    public TriageService Build()
    {
        return new TriageService(
            TaskRepository,
            WorkspaceRepository,
            HarnessRegistry,
            MarkdownSyncService,
            PathResolver,
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
