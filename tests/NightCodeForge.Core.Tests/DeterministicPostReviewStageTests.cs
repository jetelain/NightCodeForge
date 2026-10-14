using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class DeterministicPostReviewStageTests
{
    private class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public CommandResult NextResult { get; set; } = new(0, "Build succeeded.", string.Empty, TimeSpan.FromMilliseconds(10));
        public List<string> ExecutedCommands { get; } = new();

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add(command);
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenBuildFails_LoopsBackToCodeReviewFix()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, string.Empty, "error CS1002: ; expected", TimeSpan.FromMilliseconds(10))
        };

        var postReviewStage = new DeterministicPostReviewStage(executionEnv: fakeEnv);

        var task = new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            CurrentStageId = DeterministicPostReviewStage.DefaultStageId,
            Status = TaskOverallStatus.Implemented
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = "C:/fake/repo"
        };
        var context = new PipelineContext(task, workspace);

        var result = await postReviewStage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("code-review-fix", result.LoopBackStageId);
        Assert.Contains("Deterministic build failed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTestFails_LoopsBackToCodeReviewFix()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            // Build succeeds
            NextResult = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromMilliseconds(10))
        };

        var buildStage = new DeterministicBuildStage(executionEnv: fakeEnv);
        var testStage = new DeterministicTestStage(executionEnv: fakeEnv)
        {
            LoopBackStageId = DeterministicPostReviewStage.LoopBackStageId
        };

        // Make test stage return failure
        var failingTestEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "Failed: 1 test broken. Expected true, got false.", string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var failingTestStage = new DeterministicTestStage(executionEnv: failingTestEnv)
        {
            LoopBackStageId = DeterministicPostReviewStage.LoopBackStageId
        };

        var postReviewStage = new DeterministicPostReviewStage(buildStage: buildStage, testStage: failingTestStage);

        var task = new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            CurrentStageId = DeterministicPostReviewStage.DefaultStageId,
            Status = TaskOverallStatus.Implemented
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = "C:/fake/repo"
        };
        var context = new PipelineContext(task, workspace);

        var result = await postReviewStage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("code-review-fix", result.LoopBackStageId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBuildAndTestPass_ReturnsSuccess()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Build succeeded. Passed! 10 tests passed.", string.Empty, TimeSpan.FromMilliseconds(10))
        };

        var postReviewStage = new DeterministicPostReviewStage(executionEnv: fakeEnv);

        var task = new ForgeTask
        {
            Id = 3,
            WorkspaceId = 1,
            CurrentStageId = DeterministicPostReviewStage.DefaultStageId,
            Status = TaskOverallStatus.Implemented
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = "C:/fake/repo"
        };
        var context = new PipelineContext(task, workspace);

        var result = await postReviewStage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
    }
}
