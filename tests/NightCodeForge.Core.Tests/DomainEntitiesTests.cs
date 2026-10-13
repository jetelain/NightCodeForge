using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Tests;

public class DomainEntitiesTests
{
    [Fact]
    public void DomainEntities_PropertyAccessors_WorkAsExpected()
    {
        var now = DateTime.UtcNow;

        var workspace = new Workspace
        {
            Id = 1,
            Name = "My Workspace",
            RepositoryPath = "/repos/my-repo",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            ContainerImage = "nightcodeforge/agent:latest",
            Ext4MirrorPath = "/mnt/wsl/ext4",
            CreatedAt = now
        };
        Assert.Equal(1, workspace.Id);
        Assert.Equal("My Workspace", workspace.Name);
        Assert.Equal("/repos/my-repo", workspace.RepositoryPath);
        Assert.Equal("main", workspace.DefaultBaseBranch);
        Assert.Equal(ExecutionMode.BareHost, workspace.ExecutionMode);
        Assert.Equal("nightcodeforge/agent:latest", workspace.ContainerImage);
        Assert.Equal("/mnt/wsl/ext4", workspace.Ext4MirrorPath);
        Assert.Equal(now, workspace.CreatedAt);
        Assert.NotNull(workspace.Issues);
        Assert.NotNull(workspace.AgentProfiles);

        var defaultFlags = new LocalOptimizationFlags();
        Assert.True(defaultFlags.CompactOlderToolResults);
        Assert.True(defaultFlags.ToolResultCompaction);

        var flags = new LocalOptimizationFlags
        {
            GuidedGrammarDecoding = false,
            DeterministicSymbolMap = false,
            StructuredDiagnosticExtraction = false,
            PrefixStabilization = false,
            JitModelPreload = false,
            CompactOlderToolResults = false
        };
        Assert.False(flags.GuidedGrammarDecoding);
        Assert.False(flags.DeterministicSymbolMap);
        Assert.False(flags.StructuredDiagnosticExtraction);
        Assert.False(flags.PrefixStabilization);
        Assert.False(flags.JitModelPreload);
        Assert.False(flags.CompactOlderToolResults);
        Assert.False(flags.ToolResultCompaction);

        var profile = new AgentProfile
        {
            Id = "prof-1",
            WorkspaceId = 1,
            Name = "Auditor",
            HarnessId = HarnessType.Acp,
            ModelName = "deepseek-r1:32b",
            ThinkingEffort = ThinkingEffort.CustomBudget,
            ThinkingTokenBudget = 8192,
            Temperature = 0.2,
            HarnessConfigurationJson = "{\"strict\": true}",
            SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
            Workspace = workspace
        };
        Assert.Equal("prof-1", profile.Id);
        Assert.Equal(1, profile.WorkspaceId);
        Assert.Equal("Auditor", profile.Name);
        Assert.Equal(HarnessType.Acp, profile.HarnessId);
        Assert.Equal("deepseek-r1:32b", profile.ModelName);
        Assert.Equal(ThinkingEffort.CustomBudget, profile.ThinkingEffort);
        Assert.Equal(8192, profile.ThinkingTokenBudget);
        Assert.Equal(0.2, profile.Temperature);
        Assert.Equal("{\"strict\": true}", profile.HarnessConfigurationJson);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, profile.SecurityTier);
        Assert.Equal(workspace, profile.Workspace);

        var issue = new Issue
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Title",
            Description = "Desc",
            Status = IssueOverallStatus.Review,
            CurrentStageId = "code-review-fix",
            Priority = 2,
            BaseBranch = "main",
            FeatureBranch = "feat/issue-10",
            PipelineDataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
            LastHangfireJobId = "job-99",
            Workspace = workspace
        };
        Assert.Equal(10, issue.Id);
        Assert.Equal(1, issue.WorkspaceId);
        Assert.Equal("Title", issue.Title);
        Assert.Equal("Desc", issue.Description);
        Assert.Equal(IssueOverallStatus.Review, issue.Status);
        Assert.Equal("code-review-fix", issue.CurrentStageId);
        Assert.Equal(2, issue.Priority);
        Assert.Equal("main", issue.BaseBranch);
        Assert.Equal("feat/issue-10", issue.FeatureBranch);
        Assert.Equal("{}", issue.PipelineDataJson);
        Assert.Equal(now, issue.CreatedAt);
        Assert.Equal(now, issue.UpdatedAt);
        Assert.Equal("job-99", issue.LastHangfireJobId);
        Assert.Equal(workspace, issue.Workspace);
        Assert.NotNull(issue.Dependencies);
        Assert.NotNull(issue.DependentIssues);
        Assert.NotNull(issue.Questions);
        Assert.NotNull(issue.Checkpoints);
        Assert.NotNull(issue.ExecutionRecords);
        Assert.NotNull(issue.ExecutionLogs);

        var dep = new IssueDependency
        {
            Id = 1,
            IssueId = 10,
            DependsOnIssueId = 5,
            Issue = issue,
            DependsOnIssue = new Issue { Id = 5 }
        };
        Assert.Equal(1, dep.Id);
        Assert.Equal(10, dep.IssueId);
        Assert.Equal(5, dep.DependsOnIssueId);
        Assert.Equal(issue, dep.Issue);
        Assert.NotNull(dep.DependsOnIssue);

        var question = new Question
        {
            Id = 2,
            IssueId = 10,
            StageId = "issue-review",
            Prompt = "Prompt",
            Rationale = "Rationale",
            QuestionType = QuestionType.MultipleChoice,
            OptionsJson = "[\"A\", \"B\"]",
            Answer = "A",
            AskedAt = now,
            AnsweredAt = now.AddMinutes(5),
            Status = QuestionStatus.Answered,
            Issue = issue
        };
        Assert.Equal(2, question.Id);
        Assert.Equal(10, question.IssueId);
        Assert.Equal("issue-review", question.StageId);
        Assert.Equal("Prompt", question.Prompt);
        Assert.Equal("Rationale", question.Rationale);
        Assert.Equal(QuestionType.MultipleChoice, question.QuestionType);
        Assert.Equal("[\"A\", \"B\"]", question.OptionsJson);
        Assert.Equal("A", question.Answer);
        Assert.Equal(now, question.AskedAt);
        Assert.Equal(now.AddMinutes(5), question.AnsweredAt);
        Assert.Equal(QuestionStatus.Answered, question.Status);
        Assert.Equal(issue, question.Issue);

        var cp = new StageCheckpoint
        {
            Id = 3,
            IssueId = 10,
            StageId = "s1",
            SerializedMemory = "mem",
            GitCommitHash = "abcdef",
            CreatedAt = now,
            Issue = issue
        };
        Assert.Equal(3, cp.Id);
        Assert.Equal(10, cp.IssueId);
        Assert.Equal("s1", cp.StageId);
        Assert.Equal("mem", cp.SerializedMemory);
        Assert.Equal("abcdef", cp.GitCommitHash);
        Assert.Equal(now, cp.CreatedAt);
        Assert.Equal(issue, cp.Issue);

        var record = new StageExecutionRecord
        {
            Id = 4,
            IssueId = 10,
            StageId = "implementation",
            StageKind = "Agentic",
            AgentProfileId = "prof-1",
            HarnessId = "crucible",
            ModelName = "qwen2.5-coder:32b",
            StartedAt = now,
            CompletedAt = now.AddSeconds(15),
            DurationMs = 15000,
            PromptTokens = 1000,
            CompletionTokens = 500,
            TotalTokens = 1500,
            TargetRouting = "coverage-gate",
            Status = StageExecutionStatus.Completed,
            OutputDataJson = "{}",
            Issue = issue
        };
        Assert.Equal(4, record.Id);
        Assert.Equal(10, record.IssueId);
        Assert.Equal("implementation", record.StageId);
        Assert.Equal("Agentic", record.StageKind);
        Assert.Equal("prof-1", record.AgentProfileId);
        Assert.Equal("crucible", record.HarnessId);
        Assert.Equal("qwen2.5-coder:32b", record.ModelName);
        Assert.Equal(now, record.StartedAt);
        Assert.Equal(now.AddSeconds(15), record.CompletedAt);
        Assert.Equal(15000, record.DurationMs);
        Assert.Equal(1000, record.PromptTokens);
        Assert.Equal(500, record.CompletionTokens);
        Assert.Equal(1500, record.TotalTokens);
        Assert.Equal("coverage-gate", record.TargetRouting);
        Assert.Equal(StageExecutionStatus.Completed, record.Status);
        Assert.Equal("{}", record.OutputDataJson);
        Assert.Equal(issue, record.Issue);

        var log = new ExecutionLog
        {
            Id = 5,
            IssueId = 10,
            StageId = "s1",
            Timestamp = now,
            Level = Microsoft.Extensions.Logging.LogLevel.Warning,
            Source = "Compiler",
            Message = "Warning log",
            PayloadJson = "{}",
            Issue = issue
        };
        Assert.Equal(5, log.Id);
        Assert.Equal(10, log.IssueId);
        Assert.Equal("s1", log.StageId);
        Assert.Equal(now, log.Timestamp);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, log.Level);
        Assert.Equal("Compiler", log.Source);
        Assert.Equal("Warning log", log.Message);
        Assert.Equal("{}", log.PayloadJson);
        Assert.Equal(issue, log.Issue);
    }
}
