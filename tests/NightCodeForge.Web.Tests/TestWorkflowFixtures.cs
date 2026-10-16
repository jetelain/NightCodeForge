using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Tests;

public static class TestWorkflowFixtures
{
    public static WorkflowDefinition CreateStandardWorkflow()
    {
        var wf = new WorkflowDefinition
        {
            Id = 1,
            Name = "Standard 3-Agent Pipeline",
            Code = "standard-3-agent",
            Icon = "⚡",
            Description = "Default autonomous pipeline with 6 stages.",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new() { Id = 1, SequenceOrder = 1, StageId = "task-review", DisplayName = "ForgeTask Review", Icon = "🔍", Kind = WorkflowStageKind.Agentic, CsharpImplementationType = "agentic" },
                new() { Id = 2, SequenceOrder = 2, StageId = "deterministic-pre-impl", DisplayName = "Pre-Impl Baseline", Icon = "⚙️", Kind = WorkflowStageKind.Deterministic, CsharpImplementationType = "deterministic-composite" },
                new() { Id = 3, SequenceOrder = 3, StageId = "implementation", DisplayName = "Implementation", Icon = "💻", Kind = WorkflowStageKind.Agentic, CsharpImplementationType = "agentic" },
                new() { Id = 4, SequenceOrder = 4, StageId = "deterministic-post-impl", DisplayName = "Post-Impl Gates", Icon = "🧪", Kind = WorkflowStageKind.Deterministic, CsharpImplementationType = "deterministic-composite" },
                new() { Id = 5, SequenceOrder = 5, StageId = "code-review-fix", DisplayName = "Code Review", Icon = "🧐", Kind = WorkflowStageKind.Agentic, CsharpImplementationType = "agentic" },
                new() { Id = 6, SequenceOrder = 6, StageId = "deterministic-post-review", DisplayName = "Post-Review Verification", Icon = "🛡️", Kind = WorkflowStageKind.Deterministic, CsharpImplementationType = "deterministic-composite" }
            }
        };

        foreach (var s in wf.Stages)
        {
            s.Workflow = wf;
            s.WorkflowId = wf.Id;
        }

        return wf;
    }
}
