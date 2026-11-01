using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Tests;

public class BacklogContextDigestAndCycleRejectionTests
{
    private const int WorkspaceId = 1;

    private static Mock<ITaskRepository> RepositoryWith(
        IReadOnlyList<ForgeTask> tasks,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? dependencies = null)
    {
        var repo = new Mock<ITaskRepository>();
        repo.Setup(r => r.GetByWorkspaceIdAsync(WorkspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(tasks);
        repo.Setup(r => r.GetWorkspaceDependenciesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dependencies ?? new Dictionary<int, IReadOnlyList<int>>());
        return repo;
    }

    private static ForgeTask Task(int id, string title, TaskOverallStatus status, int priority, int minutesAgo = 0) => new()
    {
        Id = id,
        WorkspaceId = WorkspaceId,
        Title = title,
        Status = status,
        Priority = priority,
        UpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo)
    };

    private static WorkspaceBacklogSummaryService Summary(ITaskRepository repo, int max = WorkspaceBacklogDigestOptions.DefaultMaxActiveTasks) =>
        new(repo, new WorkspaceBacklogDigestOptions { MaxActiveTasks = max });

    [Fact]
    public async Task Digest_ExcludesTerminalTasks_AndOrdersByPriorityThenRecency()
    {
        var repo = RepositoryWith(new[]
        {
            Task(1, "Closed work", TaskOverallStatus.Closed, TaskPriority.Critical),
            Task(2, "Rejected work", TaskOverallStatus.Rejected, TaskPriority.Critical),
            Task(3, "Cancelled work", TaskOverallStatus.Cancelled, TaskPriority.Critical),
            Task(4, "Normal old", TaskOverallStatus.Suggested, TaskPriority.Normal, minutesAgo: 60),
            Task(5, "Normal recent", TaskOverallStatus.Open, TaskPriority.Normal, minutesAgo: 1),
            Task(6, "Critical", TaskOverallStatus.Ready, TaskPriority.Critical)
        });

        var digest = await Summary(repo.Object).BuildDigestAsync(WorkspaceId);

        Assert.StartsWith("<existing_workspace_tasks>", digest);
        Assert.EndsWith("</existing_workspace_tasks>", digest);
        Assert.DoesNotContain("Closed work", digest);
        Assert.DoesNotContain("Rejected work", digest);
        Assert.DoesNotContain("Cancelled work", digest);
        Assert.Contains("- #6 [P1/Critical - Ready]: Critical", digest);
        Assert.True(digest.IndexOf("#6", StringComparison.Ordinal) < digest.IndexOf("#5", StringComparison.Ordinal));
        Assert.True(digest.IndexOf("#5", StringComparison.Ordinal) < digest.IndexOf("#4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Digest_CapsTasks_AndAppendsOverflowNotice()
    {
        var tasks = Enumerable.Range(1, 5)
            .Select(i => Task(i, $"Task {i}", TaskOverallStatus.Open, TaskPriority.Normal, minutesAgo: i))
            .ToList();

        var digest = await Summary(RepositoryWith(tasks).Object, max: 3).BuildDigestAsync(WorkspaceId);

        Assert.Contains("Showing top 3 of 5 active tasks", digest);
        Assert.Contains("Lower-priority tasks omitted", digest);
        Assert.Contains("#1 ", digest);
        Assert.DoesNotContain("#5 ", digest);
    }

    [Fact]
    public async Task Digest_ExcludesRequestedTask_AndIsEmptyWithoutActiveTasks()
    {
        var repo = RepositoryWith(new[] { Task(1, "Only", TaskOverallStatus.Open, TaskPriority.Normal) });

        Assert.Equal(string.Empty, await Summary(repo.Object).BuildDigestAsync(WorkspaceId, excludeTaskId: 1));
    }

    private static TaskMarkdownValidator Validator(ITaskRepository repo) =>
        new(repo, new DependencyResolutionEngine(new InMemoryTaskDependencyStore()), NullLogger<TaskMarkdownValidator>.Instance);

    private static string Spec(int id, string dependsOn) => $"---\nid: {id}\ntitle: Task {id}\ndepends_on: {dependsOn}\n---\n\n# Task {id}\n";

    [Fact]
    public async Task Validator_RejectsCircularDependency_WithActionableError()
    {
        // #2 already depends on #1; declaring #1 -> #2 closes the cycle.
        var repo = RepositoryWith(
            new[] { Task(1, "A", TaskOverallStatus.Open, 3), Task(2, "B", TaskOverallStatus.Open, 3) },
            new Dictionary<int, IReadOnlyList<int>> { [2] = new[] { 1 } });

        var result = await Validator(repo.Object).ValidateAsync(WorkspaceId, 1, "docs/tasks/2026-01-01-a.md", Spec(1, "[ 2 ]"));

        Assert.False(result.IsValid);
        Assert.Contains("[ERROR] Circular dependency detected: Task #1 -> #2", result.ErrorMessage);
        Assert.Contains("file was not modified", result.ErrorMessage);
    }

    [Fact]
    public async Task Validator_ResolvesTitleReferences_WhenDetectingCycles()
    {
        var repo = RepositoryWith(
            new[] { Task(1, "Base models", TaskOverallStatus.Open, 3), Task(2, "Api", TaskOverallStatus.Open, 3) },
            new Dictionary<int, IReadOnlyList<int>> { [2] = new[] { 1 } });

        var result = await Validator(repo.Object).ValidateAsync(WorkspaceId, 1, "docs/tasks/a.md", Spec(1, "[ \"Api\" ]"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validator_AcceptsAcyclicDependencies_AndIgnoresOtherFiles()
    {
        var repo = RepositoryWith(
            new[] { Task(1, "A", TaskOverallStatus.Open, 3), Task(2, "B", TaskOverallStatus.Open, 3) },
            new Dictionary<int, IReadOnlyList<int>> { [2] = new[] { 1 } });
        var validator = Validator(repo.Object);

        Assert.True((await validator.ValidateAsync(WorkspaceId, 2, "docs/tasks/b.md", Spec(2, "[ 1 ]"))).IsValid);
        Assert.True((await validator.ValidateAsync(WorkspaceId, 1, "docs/other/a.md", Spec(1, "[ 2 ]"))).IsValid);
        Assert.True((await validator.ValidateAsync(WorkspaceId, 1, "docs/tasks/archive/a.md", Spec(1, "[ 2 ]"))).IsValid);
        Assert.True((await validator.ValidateAsync(WorkspaceId, 1, "docs/tasks/sub/a.md", Spec(1, "[ 2 ]"))).IsValid);
        Assert.True((await validator.ValidateAsync(WorkspaceId, 1, "docs/tasks/a.txt", Spec(1, "[ 2 ]"))).IsValid);
    }

    [Fact]
    public async Task Validator_WhenWritingNewFollowUpTaskWithoutId_DependingOnCurrentTask_AllowsValidWrite()
    {
        var repo = RepositoryWith(new[] { Task(42, "Implement OAuth", TaskOverallStatus.Open, 3) });
        var validator = Validator(repo.Object);

        var newFollowUpSpec = "---\ntitle: Add OAuth metrics\ndepends_on:\n  - 42\n---\n\n# Add OAuth metrics\n";
        var result = await validator.ValidateAsync(WorkspaceId, currentTaskId: 42, "docs/tasks/2026-11-01-add-oauth-metrics.md", newFollowUpSpec);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validator_WhenWritingCurrentTaskWithoutId_DependingOnItself_RejectsSelfCycle()
    {
        var repo = RepositoryWith(new[] { Task(42, "Implement OAuth", TaskOverallStatus.Open, 3) });
        var validator = Validator(repo.Object);

        var currentTaskSpec = "---\ntitle: Implement OAuth\ndepends_on:\n  - 42\n---\n\n# Implement OAuth\n";
        var result = await validator.ValidateAsync(WorkspaceId, currentTaskId: 42, "docs/tasks/2026-11-01-implement-oauth.md", currentTaskSpec);

        Assert.False(result.IsValid);
        Assert.Contains("Task #42 -> #42", result.ErrorMessage);
    }

    [Fact]
    public async Task Validator_WhenWritingNewFollowUpTaskDependingOnOwnTitle_RejectsSelfDependency()
    {
        var repo = RepositoryWith(new[] { Task(42, "Implement OAuth", TaskOverallStatus.Open, 3) });
        var validator = Validator(repo.Object);

        var selfDepSpec = "---\ntitle: Add OAuth metrics\ndepends_on:\n  - Add OAuth metrics\n---\n\n# Add OAuth metrics\n";
        var result = await validator.ValidateAsync(WorkspaceId, currentTaskId: 42, "docs/tasks/2026-11-01-add-oauth-metrics.md", selfDepSpec);

        Assert.False(result.IsValid);
        Assert.Contains("cannot depend on itself", result.ErrorMessage);
    }
}
