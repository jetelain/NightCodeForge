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

    public static CanonicalTools Create(
        string workspaceRoot,
        IExecutionEnvironment? executionEnv = null,
        IWorkspacePathResolver? pathResolver = null,
        IWebSearchService? webSearchService = null,
        IWorkspaceSkillsService? skillsService = null,
        bool isReadOnly = false,
        int? workspaceId = null,
        int? taskId = null) =>
        new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = workspaceRoot,
            ExecutionEnv = executionEnv ?? new Mock<IExecutionEnvironment>().Object,
            PathResolver = pathResolver ?? new Mock<IWorkspacePathResolver>().Object,
            WebSearchService = webSearchService ?? new Mock<IWebSearchService>().Object,
            SkillsService = skillsService,
            IsReadOnly = isReadOnly,
            WorkspaceId = workspaceId,
            TaskId = taskId
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
            WorkspaceRoot,
            PathResolver,
            ExecutionEnv,
            EditorConfigService,
            EditorConfigFileApplier,
            WebSearchService,
            skills,
            Logger,
            BuildTimeoutSeconds,
            TestTimeoutSeconds,
            WorkspaceId,
            TaskId,
            IsReadOnly);
    }
}
