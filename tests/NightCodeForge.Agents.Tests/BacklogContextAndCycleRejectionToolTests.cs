using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class BacklogContextAndCycleRejectionToolTests : IDisposable
{
    private const string DigestMarker = "<existing_workspace_tasks>";
    private const int WorkspaceId = 7;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-cycle-" + Guid.NewGuid().ToString("N"));

    public BacklogContextAndCycleRejectionToolTests() => Directory.CreateDirectory(Path.Combine(_root, "docs", "tasks"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Stabilizer_PlacesBacklogDigestInLayer3Only_AndKeepsLayers1And2Stable()
    {
        var stabilizer = new PromptPrefixStabilizer();
        var digestA = $"{DigestMarker}\n- #1 [P3/Normal - Open]: A\n</existing_workspace_tasks>";
        var digestB = $"{DigestMarker}\n- #2 [P1/Critical - Ready]: B\n</existing_workspace_tasks>";

        var without = stabilizer.BuildLayers("Review", "<task id=\"1\" />");
        var withA = stabilizer.BuildLayers("Review", "<task id=\"1\" />", backlogDigest: digestA);
        var withB = stabilizer.BuildLayers("Review", "<task id=\"1\" />", backlogDigest: digestB);

        Assert.Equal(without.Layer1GlobalRules, withA.Layer1GlobalRules);
        Assert.Equal(without.Layer2StagePersona, withA.Layer2StagePersona);
        Assert.Equal(withA.Layer1GlobalRules, withB.Layer1GlobalRules);
        Assert.Equal(withA.Layer2StagePersona, withB.Layer2StagePersona);
        Assert.DoesNotContain(DigestMarker, withA.Layer1GlobalRules + withA.Layer2StagePersona);
        Assert.Contains(DigestMarker, withA.Layer3TaskScope);
        Assert.DoesNotContain(DigestMarker, without.Layer3TaskScope);
    }

    private CanonicalTools ToolsWithCycle(string specFileName, string specContent)
    {
        var specPath = Path.Combine(_root, "docs", "tasks", specFileName);
        File.WriteAllText(specPath, specContent);

        var repo = new Mock<ITaskRepository>();
        repo.Setup(r => r.GetByWorkspaceIdAsync(WorkspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ForgeTask { Id = 1, WorkspaceId = WorkspaceId, Title = "A" },
            new ForgeTask { Id = 2, WorkspaceId = WorkspaceId, Title = "B" }
        });
        repo.Setup(r => r.GetWorkspaceDependenciesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, IReadOnlyList<int>> { [2] = new[] { 1 } });

        var tools = TestCanonicalToolsBuilder.Create(
            _root,
            pathResolver: TestPathResolver.Create(),
            workspaceId: WorkspaceId,
            taskId: 1);
        tools.TaskMarkdownValidator = new TaskMarkdownValidator(
            repo.Object,
            new DependencyResolutionEngine(new InMemoryTaskDependencyStore()),
            NullLogger<TaskMarkdownValidator>.Instance);
        return tools;
    }

    private const string OriginalSpec = "---\nid: 1\ntitle: A\ndepends_on: []\n---\n\n# A\n";

    [Fact]
    public async Task Edit_OnTaskSpec_RejectsCircularDependency_WithoutWriting()
    {
        var tools = ToolsWithCycle("a.md", OriginalSpec);
        var specPath = Path.Combine("docs", "tasks", "a.md");

        var result = await tools.EditAsync(specPath, "depends_on: []", "depends_on: [ 2 ]");

        Assert.Contains("[ERROR] Circular dependency detected: Task #1 -> #2", result);
        Assert.Equal(OriginalSpec, File.ReadAllText(Path.Combine(_root, specPath)));
    }

    [Fact]
    public async Task Write_OnTaskSpec_RejectsCircularDependency_WithoutWriting()
    {
        var tools = ToolsWithCycle("a.md", OriginalSpec);
        var specPath = Path.Combine("docs", "tasks", "a.md");

        var result = await tools.WriteAsync(specPath, "---\nid: 1\ntitle: A\ndepends_on: [ 2 ]\n---\n\n# A\n");

        Assert.Contains("Circular dependency detected", result);
        Assert.Equal(OriginalSpec, File.ReadAllText(Path.Combine(_root, specPath)));
    }

    [Fact]
    public async Task Edit_OnTaskSpec_AllowsAcyclicEdit()
    {
        var tools = ToolsWithCycle("a.md", OriginalSpec);
        var specPath = Path.Combine("docs", "tasks", "a.md");

        var result = await tools.EditAsync(specPath, "# A", "# A updated");

        Assert.StartsWith("Successfully applied", result);
        Assert.Contains("# A updated", File.ReadAllText(Path.Combine(_root, specPath)));
    }

    [Fact]
    public async Task AgentStreamProjector_MarksCycleRejectionResultAsError()
    {
        var tools = ToolsWithCycle("a.md", OriginalSpec);
        var specPath = Path.Combine("docs", "tasks", "a.md");
        var rejection = await tools.EditAsync(specPath, "depends_on: []", "depends_on: [ 2 ]");

        var projector = new NightCodeForge.Agents.Streaming.AgentStreamProjector();
        var callUpdate = new Microsoft.Agents.AI.AgentResponseUpdate(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            [new Microsoft.Extensions.AI.FunctionCallContent("call-1", "edit", new Dictionary<string, object?> { ["path"] = specPath })]);
        _ = projector.ProjectUpdate(callUpdate).ToList();

        var resultUpdate = new Microsoft.Agents.AI.AgentResponseUpdate(
            Microsoft.Extensions.AI.ChatRole.Tool,
            [new Microsoft.Extensions.AI.FunctionResultContent("call-1", rejection)]);
        var projected = projector.ProjectUpdate(resultUpdate).Single();

        Assert.NotNull(projected.ToolCall);
        Assert.True(projected.ToolCall!.IsError);
        Assert.Contains("Circular dependency detected", projected.ToolCall.Result);
    }
}

