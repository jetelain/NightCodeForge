namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

public class TestCanonicalToolsBuilder
{
    public string WorkspaceRoot { get; set; } = Directory.GetCurrentDirectory();
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IExecutionEnvironment ExecutionEnv { get; set; } = new Mock<IExecutionEnvironment>().Object;
    public IEditorConfigService EditorConfigService { get; set; } = new EditorConfigService();
    public IEditorConfigFileApplier EditorConfigFileApplier { get; set; } = new EditorConfigFileApplier();
    public IWebSearchService WebSearchService { get; set; } = new Mock<IWebSearchService>().Object;
    public IWorkspaceSkillsService? SkillsService { get; set; }
    public ILogger<CanonicalTools> Logger { get; set; } = NullLogger<CanonicalTools>.Instance;
    public int? BuildTimeoutSeconds { get; set; }
    public int? TestTimeoutSeconds { get; set; }
    public int? WorkspaceId { get; set; }
    public int? TaskId { get; set; }
    public bool IsReadOnly { get; set; }
    public int? MaxReadFileBytes { get; set; }
    public NightCodeForge.Core.Workflow.Filtering.ICliOutputFilterRegistry? CliFilterRegistry { get; set; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }

    public static CanonicalTools Create(
        string workspaceRoot,
        IExecutionEnvironment? executionEnv = null,
        IWorkspacePathResolver? pathResolver = null,
        IWebSearchService? webSearchService = null,
        IWorkspaceSkillsService? skillsService = null,
        bool isReadOnly = false,
        int? workspaceId = null,
        int? taskId = null,
        int? maxReadFileBytes = null,
        LocalOptimizationFlags? optimizationFlags = null,
        NightCodeForge.Core.Workflow.Filtering.ICliOutputFilterRegistry? cliFilterRegistry = null) =>
        new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = workspaceRoot,
            ExecutionEnv = executionEnv ?? new Mock<IExecutionEnvironment>().Object,
            PathResolver = pathResolver ?? new Mock<IWorkspacePathResolver>().Object,
            WebSearchService = webSearchService ?? new Mock<IWebSearchService>().Object,
            SkillsService = skillsService,
            IsReadOnly = isReadOnly,
            WorkspaceId = workspaceId,
            TaskId = taskId,
            MaxReadFileBytes = maxReadFileBytes,
            OptimizationFlags = optimizationFlags,
            CliFilterRegistry = cliFilterRegistry
        }.Build();

    public CanonicalTools Build()
    {
        var skills = SkillsService ?? new WorkspaceSkillsService(
            new Workspace { Id = WorkspaceId ?? 1, Name = "TestWorkspace" },
            WorkspaceRoot,
            TaskId,
            new SkillsService(),
            new BundledSkillsProvider(),
            NullLogger<WorkspaceSkillsService>.Instance);

        return new(
            new CanonicalToolScope(WorkspaceRoot, WorkspaceId, TaskId, BuildTimeoutSeconds, TestTimeoutSeconds, IsReadOnly, MaxReadFileBytes, OptimizationFlags: OptimizationFlags),
            PathResolver,
            ExecutionEnv,
            EditorConfigService,
            EditorConfigFileApplier,
            WebSearchService,
            skills,
            CliFilterRegistry ?? NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default,
            Logger);
    }
}
