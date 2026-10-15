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
        Assert.NotNull(workspace.Tasks);
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

        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Title",
            Description = "Desc",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = "code-review-fix",
            Priority = 2,
            BaseBranch = "main",
            FeatureBranch = "feat/task-10",
            PipelineDataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
            LastHangfireJobId = "job-99",
            Workspace = workspace
        };
        Assert.Equal(10, task.Id);
        Assert.Equal(1, task.WorkspaceId);
        Assert.Equal("Title", task.Title);
        Assert.Equal("Desc", task.Description);
        Assert.Equal(TaskOverallStatus.Reviewed, task.Status);
        Assert.Equal("code-review-fix", task.CurrentStageId);
        Assert.Equal(2, task.Priority);
        Assert.Equal("main", task.BaseBranch);
        Assert.Equal("feat/task-10", task.FeatureBranch);
        Assert.Equal("{}", task.PipelineDataJson);
        Assert.Equal(now, task.CreatedAt);
        Assert.Equal(now, task.UpdatedAt);
        Assert.Equal("job-99", task.LastHangfireJobId);
        Assert.Equal(workspace, task.Workspace);
        Assert.NotNull(task.Dependencies);
        Assert.NotNull(task.DependentTasks);
        Assert.NotNull(task.Questions);
        Assert.NotNull(task.Checkpoints);
        Assert.NotNull(task.ExecutionRecords);
        Assert.NotNull(task.ExecutionLogs);

        var dep = new TaskDependency
        {
            Id = 1,
            TaskId = 10,
            DependsOnTaskId = 5,
            ForgeTask = task,
            DependsOnTask = new ForgeTask { Id = 5 }
        };
        Assert.Equal(1, dep.Id);
        Assert.Equal(10, dep.TaskId);
        Assert.Equal(5, dep.DependsOnTaskId);
        Assert.Equal(task, dep.ForgeTask);
        Assert.NotNull(dep.DependsOnTask);

        var question = new Question
        {
            Id = 2,
            TaskId = 10,
            StageId = "task-review",
            Prompt = "Prompt",
            Rationale = "Rationale",
            QuestionType = QuestionType.MultipleChoice,
            OptionsJson = "[\"A\", \"B\"]",
            Answer = "A",
            AskedAt = now,
            AnsweredAt = now.AddMinutes(5),
            Status = QuestionStatus.Answered,
            ForgeTask = task
        };
        Assert.Equal(2, question.Id);
        Assert.Equal(10, question.TaskId);
        Assert.Equal("task-review", question.StageId);
        Assert.Equal("Prompt", question.Prompt);
        Assert.Equal("Rationale", question.Rationale);
        Assert.Equal(QuestionType.MultipleChoice, question.QuestionType);
        Assert.Equal("[\"A\", \"B\"]", question.OptionsJson);
        Assert.Equal("A", question.Answer);
        Assert.Equal(now, question.AskedAt);
        Assert.Equal(now.AddMinutes(5), question.AnsweredAt);
        Assert.Equal(QuestionStatus.Answered, question.Status);
        Assert.Equal(task, question.ForgeTask);

        var cp = new StageCheckpoint
        {
            Id = 3,
            TaskId = 10,
            StageId = "s1",
            SerializedMemory = "mem",
            GitCommitHash = "abcdef",
            CreatedAt = now,
            ForgeTask = task
        };
        Assert.Equal(3, cp.Id);
        Assert.Equal(10, cp.TaskId);
        Assert.Equal("s1", cp.StageId);
        Assert.Equal("mem", cp.SerializedMemory);
        Assert.Equal("abcdef", cp.GitCommitHash);
        Assert.Equal(now, cp.CreatedAt);
        Assert.Equal(task, cp.ForgeTask);

        var record = new StageExecutionRecord
        {
            Id = 4,
            TaskId = 10,
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
            ForgeTask = task
        };
        Assert.Equal(4, record.Id);
        Assert.Equal(10, record.TaskId);
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
        Assert.Equal(task, record.ForgeTask);

        var log = new ExecutionLog
        {
            Id = 5,
            TaskId = 10,
            StageId = "s1",
            Timestamp = now,
            Level = Microsoft.Extensions.Logging.LogLevel.Warning,
            Source = "Compiler",
            Message = "Warning log",
            PayloadJson = "{}",
            ForgeTask = task
        };
        Assert.Equal(5, log.Id);
        Assert.Equal(10, log.TaskId);
        Assert.Equal("s1", log.StageId);
        Assert.Equal(now, log.Timestamp);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, log.Level);
        Assert.Equal("Compiler", log.Source);
        Assert.Equal("Warning log", log.Message);
        Assert.Equal("{}", log.PayloadJson);
        Assert.Equal(task, log.ForgeTask);
    }

    [Fact]
    public void CanonicalToolNames_DefinesExpectedTools_AndAllContainsAllConstants()
    {
        Assert.Equal("read", CanonicalToolNames.Read);
        Assert.Equal("edit", CanonicalToolNames.Edit);
        Assert.Equal("write", CanonicalToolNames.Write);
        Assert.Equal("bash", CanonicalToolNames.Bash);
        Assert.Equal("ask", CanonicalToolNames.Ask);
        Assert.Equal("web_search", CanonicalToolNames.WebSearch);
        Assert.Equal("propose_tasks", CanonicalToolNames.ProposeTasks);
        Assert.Equal("request_changes", CanonicalToolNames.RequestChanges);
        Assert.Equal("load_skill", CanonicalToolNames.LoadSkill);
        Assert.Equal("read_skill_resource", CanonicalToolNames.ReadSkillResource);
        Assert.Equal("run_skill_script", CanonicalToolNames.RunSkillScript);

        Assert.Equal(11, CanonicalToolNames.All.Count);
        Assert.Contains(CanonicalToolNames.Read, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.Edit, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.Write, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.Bash, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.Ask, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.WebSearch, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.ProposeTasks, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.RequestChanges, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.LoadSkill, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.ReadSkillResource, CanonicalToolNames.All);
        Assert.Contains(CanonicalToolNames.RunSkillScript, CanonicalToolNames.All);
    }

    [Fact]
    public void DefaultAgentTools_ProvidesAppropriateToolSetsPerMode()
    {
        // ReadOnly mode allows Read, Bash (with read-only enforcement), and ProposeTasks, but not destructive edit/write or request_changes
        Assert.Contains(CanonicalToolNames.Read, DefaultAgentTools.ReadOnly);
        Assert.Contains(CanonicalToolNames.Bash, DefaultAgentTools.ReadOnly);
        Assert.Contains(CanonicalToolNames.ProposeTasks, DefaultAgentTools.ReadOnly);
        Assert.DoesNotContain(CanonicalToolNames.Edit, DefaultAgentTools.ReadOnly);
        Assert.DoesNotContain(CanonicalToolNames.Write, DefaultAgentTools.ReadOnly);
        Assert.DoesNotContain(CanonicalToolNames.RequestChanges, DefaultAgentTools.ReadOnly);

        // Full mode includes write, edit, bash
        Assert.Contains(CanonicalToolNames.Write, DefaultAgentTools.Full);
        Assert.Contains(CanonicalToolNames.Edit, DefaultAgentTools.Full);
        Assert.Contains(CanonicalToolNames.Bash, DefaultAgentTools.Full);
        Assert.DoesNotContain(CanonicalToolNames.RequestChanges, DefaultAgentTools.Full);

        // Review mode includes request_changes
        Assert.Contains(CanonicalToolNames.RequestChanges, DefaultAgentTools.Review);
        Assert.Contains(CanonicalToolNames.Write, DefaultAgentTools.Review);
        Assert.Contains(CanonicalToolNames.Edit, DefaultAgentTools.Review);

        // Mode dispatch
        Assert.Same(DefaultAgentTools.ReadOnly, DefaultAgentTools.GetTools(AgentMode.ReadOnly));
        Assert.Same(DefaultAgentTools.Review, DefaultAgentTools.GetTools(AgentMode.Review));
        Assert.Same(DefaultAgentTools.Full, DefaultAgentTools.GetTools(AgentMode.Full));
    }
}

