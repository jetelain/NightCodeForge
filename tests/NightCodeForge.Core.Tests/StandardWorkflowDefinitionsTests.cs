namespace NightCodeForge.Core.Tests;

using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Xunit;

public class StandardWorkflowDefinitionsTests
{
    [Fact]
    public void CreateStandard3AgentWorkflow_ReturnsValidConfiguredWorkflow()
    {
        var wf = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();

        Assert.NotNull(wf);
        Assert.Equal(StandardWorkflowDefinitions.Standard3AgentCode, wf.Code);
        Assert.Equal("Standard 3-Agent Pipeline", wf.Name);
        Assert.True(wf.IsDefault);
        Assert.Null(wf.WorkspaceId);
        Assert.Equal("⚡", wf.Icon);
        Assert.False(string.IsNullOrWhiteSpace(wf.Description));

        Assert.Equal(6, wf.Stages.Count);

        var orderedStages = wf.Stages.OrderBy(s => s.SequenceOrder).ToList();
        var expectedStages = new (string StageId, string DisplayName, WorkflowStageKind Kind, string ImplType, int Sequence)[]
        {
            (StandardStageIds.TaskReview, "ForgeTask Review", WorkflowStageKind.Agentic, StageImplementationTypes.Agentic, 1),
            (StandardStageIds.PreImplementationBaseline, "Pre-Implementation Baseline", WorkflowStageKind.Deterministic, StageImplementationTypes.DeterministicComposite, 2),
            (StandardStageIds.Implementation, "Implementation", WorkflowStageKind.Agentic, StageImplementationTypes.Agentic, 3),
            (StandardStageIds.PostImplementationGates, "Post-Implementation Gates", WorkflowStageKind.Deterministic, StageImplementationTypes.DeterministicComposite, 4),
            (StandardStageIds.CodeReviewAndFix, "Code Review & Fix", WorkflowStageKind.Agentic, StageImplementationTypes.Agentic, 5),
            (StandardStageIds.PostReviewVerification, "Post-Review Verification", WorkflowStageKind.Deterministic, StageImplementationTypes.DeterministicComposite, 6)
        };

        for (int i = 0; i < expectedStages.Length; i++)
        {
            var stage = orderedStages[i];
            var expected = expectedStages[i];

            Assert.Equal(expected.StageId, stage.StageId);
            Assert.Equal(expected.DisplayName, stage.DisplayName);
            Assert.Equal(expected.Kind, stage.Kind);
            Assert.Equal(expected.ImplType, stage.CsharpImplementationType);
            Assert.Equal(expected.Sequence, stage.SequenceOrder);
            Assert.Same(wf, stage.Workflow);
            Assert.False(string.IsNullOrWhiteSpace(stage.ConfigurationJson));
        }

        // Verify loopbacks reference canonical implementation stage
        var postImplGate = orderedStages[3];
        Assert.Contains(StandardStageIds.Implementation, postImplGate.ConfigurationJson);

        var reviewGate = orderedStages[4];
        Assert.Contains(StandardStageIds.Implementation, reviewGate.ConfigurationJson);

        var postReviewGate = orderedStages[5];
        Assert.Contains(StandardStageIds.Implementation, postReviewGate.ConfigurationJson);
    }
}
