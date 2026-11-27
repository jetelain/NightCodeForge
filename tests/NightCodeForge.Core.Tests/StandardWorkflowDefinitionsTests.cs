namespace NightCodeForge.Core.Tests;

using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
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

        var taskReviewStage = orderedStages[0];
        Assert.Contains("standard", taskReviewStage.ConfigurationJson);
        Assert.Contains("difficult", taskReviewStage.ConfigurationJson);
        Assert.Contains("expert", taskReviewStage.ConfigurationJson);
        Assert.Contains("YAML frontmatter", taskReviewStage.ConfigurationJson);
        Assert.Contains("'ask'", taskReviewStage.ConfigurationJson);
    }

    [Fact]
    public void CodeReviewStageDirective_RendersDistinctToolEnabledAndXmlFallbackBranches()
    {
        var wf = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var reviewStage = wf.Stages.Single(s => s.StageId == StandardStageIds.CodeReviewAndFix);
        using var doc = JsonDocument.Parse(reviewStage.ConfigurationJson);
        var templateDirective = doc.RootElement.GetProperty("StageDirective").GetString()!;

        Assert.Contains("tools.has_request_changes", templateDirective, StringComparison.Ordinal);

        // 1. Crucible with request_changes tool active: tool-only wording, no XML fallback
        var crucibleReviewTools = StageDirectiveRenderer.ResolveEffectivePermittedTools(
            StandardHarnessIds.Crucible,
            new AgentProfile { Id = "rev", Mode = AgentMode.Review },
            AgentMode.Review);
        Assert.Contains(CanonicalToolNames.RequestChanges, crucibleReviewTools);

        var toolDirective = StageDirectiveRenderer.Render(templateDirective, crucibleReviewTools);
        Assert.Contains("Invoke the 'request_changes(feedback, reason)' tool immediately to reject.", toolDirective, StringComparison.Ordinal);
        Assert.Contains("\n   - Invoke the 'request_changes(feedback, reason)' tool immediately to reject.\n   MANDATORY BUG REGRESSION POLICY:", toolDirective, StringComparison.Ordinal);
        Assert.DoesNotContain("<request_changes>", toolDirective, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("If 'request_changes' tool is available", toolDirective, StringComparison.OrdinalIgnoreCase);

        // 2. Crucible without request_changes tool (or external harness like ACP): XML fallback wording, no tool instruction
        var acpTools = StageDirectiveRenderer.ResolveEffectivePermittedTools(
            StandardHarnessIds.Acp,
            new AgentProfile { Id = "acp-rev", Mode = AgentMode.Review, PermittedTools = [CanonicalToolNames.RequestChanges] },
            AgentMode.Review);
        Assert.Empty(acpTools);

        var xmlDirective = StageDirectiveRenderer.Render(templateDirective, acpTools);
        Assert.Contains("<request_changes>", xmlDirective, StringComparison.Ordinal);
        Assert.Contains("</request_changes>", xmlDirective, StringComparison.Ordinal);
        Assert.Contains(
            "\n   - Conclude your final response with:\n     <request_changes>\n     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n     </request_changes>\n   MANDATORY BUG REGRESSION POLICY:",
            xmlDirective,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke the 'request_changes(feedback, reason)' tool", xmlDirective, StringComparison.Ordinal);
        Assert.DoesNotContain("If 'request_changes' tool is available", xmlDirective, StringComparison.Ordinal);
    }

    [Fact]
    public void StageDirectiveRenderer_PreservesLiteralBraces_AndThrowsOnMalformedToolsTemplate()
    {
        const string literalDirective = "Check Vue template {{ item.name }} and C# raw literal {{ unclosed before editing.";
        Assert.False(StageDirectiveRenderer.ContainsTemplateExpression(literalDirective));
        Assert.Equal(literalDirective, StageDirectiveRenderer.Render(literalDirective, [CanonicalToolNames.RequestChanges]));

        const string malformedToolsDirective = "{{ if tools.has_request_changes }}Invoke request_changes";
        Assert.True(StageDirectiveRenderer.ContainsTemplateExpression(malformedToolsDirective));
        Assert.Throws<InvalidOperationException>(() =>
            StageDirectiveRenderer.Render(malformedToolsDirective, [CanonicalToolNames.RequestChanges]));
    }

    [Fact]
    public void StageDirectiveRenderer_And_DefaultBootstrapPromptBuilder_ResolveToolsAcrossHarnessConfigReadOnlyAndTriage()
    {
        var allowedProfile = new AgentProfile
        {
            Id = "crucible-json",
            HarnessId = StandardHarnessIds.Crucible,
            Mode = AgentMode.Full,
            PermittedTools = [CanonicalToolNames.Read, CanonicalToolNames.RequestChanges],
            HarnessConfigurationJson = """{"AllowedTools":["read","edit"]}"""
        };
        var resolvedOverride = StageDirectiveRenderer.ResolveEffectivePermittedTools(
            StandardHarnessIds.Crucible,
            allowedProfile,
            AgentMode.Review);
        Assert.Equal(["read", "edit"], resolvedOverride);

        var readOnlyIntersected = StageDirectiveRenderer.ResolveEffectivePermittedTools(
            StandardHarnessIds.Crucible,
            new AgentProfile
            {
                Id = "ro",
                HarnessId = StandardHarnessIds.Crucible,
                Mode = AgentMode.ReadOnly,
                PermittedTools = [CanonicalToolNames.Read, CanonicalToolNames.Bash, CanonicalToolNames.Edit],
                HarnessConfigurationJson = """{"AllowedTools":["read","edit"]}"""
            },
            AgentMode.Full);
        Assert.Equal(["read", "edit"], readOnlyIntersected);

        var triageTools = StageDirectiveRenderer.ResolveEffectivePermittedTools(
            StandardHarnessIds.Crucible,
            new AgentProfile
            {
                Id = "triage",
                HarnessId = StandardHarnessIds.Crucible,
                Mode = AgentMode.Full,
                PermittedTools = [CanonicalToolNames.Read, CanonicalToolNames.Edit, CanonicalToolNames.ProposeTasks]
            },
            AgentMode.ReadOnly,
            isTriageSession: true);
        Assert.Contains(CanonicalToolNames.ProposeTasks, triageTools);
        Assert.DoesNotContain(CanonicalToolNames.Edit, triageTools);

        var wf = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var reviewStageDef = wf.Stages.Single(s => s.StageId == StandardStageIds.CodeReviewAndFix);
        var reviewStage = new GenericAgenticStage(
            reviewStageDef.StageId,
            reviewStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(reviewStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);
        var task = new ForgeTask { Id = 12, Title = "Review", Description = "Desc" };
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace" };

        var crucibleCtx = new PipelineContext(task, workspace, new PipelineDataBag())
        {
            CurrentProfile = new AgentProfile { Id = "rev-crucible", HarnessId = StandardHarnessIds.Crucible, Mode = AgentMode.Review }
        };
        var cruciblePrompt = DefaultBootstrapPromptBuilder.Build(reviewStage, task, crucibleCtx, currentDate: "2026-11-27");
        Assert.Contains("Invoke the 'request_changes(feedback, reason)' tool immediately to reject.", cruciblePrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<request_changes>", cruciblePrompt, StringComparison.OrdinalIgnoreCase);

        var acpCtx = new PipelineContext(task, workspace, new PipelineDataBag())
        {
            CurrentProfile = new AgentProfile
            {
                Id = "rev-acp",
                HarnessId = StandardHarnessIds.Acp,
                Mode = AgentMode.Review,
                PermittedTools = [CanonicalToolNames.RequestChanges]
            }
        };
        var acpPrompt = DefaultBootstrapPromptBuilder.Build(reviewStage, task, acpCtx, currentDate: "2026-11-27");
        Assert.Contains("<request_changes>", acpPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke the 'request_changes(feedback, reason)' tool", acpPrompt, StringComparison.Ordinal);
    }
}
