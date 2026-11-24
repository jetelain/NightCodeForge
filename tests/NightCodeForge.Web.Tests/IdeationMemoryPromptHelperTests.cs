using NightCodeForge.Core.Memory;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationMemoryPromptHelperTests
{
    private static MemoryFileEntry Entry(string path) =>
        new(AgentMemoryScope.LocalWorkspace, path, 100, 5, DateTime.UtcNow, "summary");

    [Fact]
    public void BuildIdeationMemoryActionUrl_IncludesActionAndOptionalScopeAndPath()
    {
        var summarizeUrl = IdeationMemoryPromptHelper.BuildIdeationMemoryActionUrl(4, MemoryIdeationAction.SummarizePinnedMemory);
        Assert.Equal("/w/4/ideation?memoryAction=summarize-pinned", summarizeUrl);

        var discussUrl = IdeationMemoryPromptHelper.BuildIdeationMemoryActionUrl(
            4,
            MemoryIdeationAction.DiscussSelectedFile,
            AgentMemoryScope.SharedRepository,
            ".agents/memory/arch.md");
        Assert.Equal("/w/4/ideation?memoryAction=discuss-file&scope=shared&path=.agents%2Fmemory%2Farch.md", discussUrl);
    }

    [Theory]
    [InlineData("summarize-pinned", MemoryIdeationAction.SummarizePinnedMemory)]
    [InlineData("deduplicate-local", MemoryIdeationAction.DeduplicateLocalMemory)]
    [InlineData("promote-repo", MemoryIdeationAction.PromoteToRepositoryMemory)]
    [InlineData("discuss-file", MemoryIdeationAction.DiscussSelectedFile)]
    public void TryParseActionQueryValue_ParsesValidValues(string raw, MemoryIdeationAction expected)
    {
        Assert.True(IdeationMemoryPromptHelper.TryParseActionQueryValue(raw, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void TryParseActionQueryValue_RejectsInvalidValues(string? raw)
    {
        Assert.False(IdeationMemoryPromptHelper.TryParseActionQueryValue(raw, out _));
    }

    [Fact]
    public void ComputeQuickActionState_EvaluatesEnablementAndBudgetEmphasis()
    {
        var emptyState = IdeationMemoryPromptHelper.ComputeQuickActionState([], null, null, null);
        Assert.False(emptyState.CanSummarizePinned);
        Assert.False(emptyState.EmphasizeSummarizePinned);
        Assert.False(emptyState.CanDeduplicateLocal);
        Assert.False(emptyState.CanPromoteToRepo);
        Assert.False(emptyState.CanDiscussSelected);

        var okBudget = new MemoryBudget(20, 1000, AgentMemoryConstants.MaxPinnedMemoryLines, AgentMemoryConstants.MaxPinnedMemoryBytes);
        var normalState = IdeationMemoryPromptHelper.ComputeQuickActionState(
            [Entry("MEMORY.md")],
            okBudget,
            AgentMemoryScope.LocalWorkspace,
            "MEMORY.md");
        Assert.True(normalState.CanSummarizePinned);
        Assert.False(normalState.EmphasizeSummarizePinned);
        Assert.True(normalState.CanDeduplicateLocal);
        Assert.True(normalState.CanPromoteToRepo);
        Assert.True(normalState.CanDiscussSelected);

        var nearBudget = new MemoryBudget(125, 1000, AgentMemoryConstants.MaxPinnedMemoryLines, AgentMemoryConstants.MaxPinnedMemoryBytes);
        var emphasizedState = IdeationMemoryPromptHelper.ComputeQuickActionState(
            [Entry("MEMORY.md")],
            nearBudget,
            AgentMemoryScope.SharedRepository,
            "AGENTS.md");
        Assert.True(emphasizedState.EmphasizeSummarizePinned);
        Assert.True(emphasizedState.CanDiscussSelected);
    }

    [Fact]
    public void BuildPrompt_AllActionsEnforceTwoTierBoundary()
    {
        var prompts = new[]
        {
            IdeationMemoryPromptHelper.BuildPrompt(MemoryIdeationAction.SummarizePinnedMemory),
            IdeationMemoryPromptHelper.BuildPrompt(MemoryIdeationAction.DeduplicateLocalMemory),
            IdeationMemoryPromptHelper.BuildPrompt(MemoryIdeationAction.PromoteToRepositoryMemory),
            IdeationMemoryPromptHelper.BuildPrompt(MemoryIdeationAction.DiscussSelectedFile, AgentMemoryScope.LocalWorkspace, "notes.md"),
            IdeationMemoryPromptHelper.BuildPrompt(MemoryIdeationAction.DiscussSelectedFile, AgentMemoryScope.SharedRepository, ".agents/memory/arch.md")
        };

        foreach (var prompt in prompts)
        {
            Assert.Contains("/workspace/memory/*.md", prompt);
            Assert.Contains("propose_tasks", prompt);
            Assert.Contains("AGENTS.md", prompt);
        }

        Assert.Contains(AgentMemoryConstants.MaxPinnedMemoryLines.ToString(), prompts[0]);
        Assert.Contains("/workspace/memory/notes.md", prompts[3]);
        Assert.Contains(".agents/memory/arch.md", prompts[4]);
    }

    [Fact]
    public void BuildDeduplicationKey_IncludesWorkspaceIdAndNormalizesActionAndScope()
    {
        var key1 = IdeationMemoryPromptHelper.BuildDeduplicationKey(1, " Summarize-Pinned ", " LOCAL ", "MEMORY.md ");
        var key2 = IdeationMemoryPromptHelper.BuildDeduplicationKey(1, "summarize-pinned", "local", "MEMORY.md");
        var keyOtherWs = IdeationMemoryPromptHelper.BuildDeduplicationKey(2, "summarize-pinned", "local", "MEMORY.md");

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, keyOtherWs);
    }

    [Fact]
    public void ComputeQuickActionState_UnsavedNewFile_DisablesDiscussSelected()
    {
        var unsavedState = IdeationMemoryPromptHelper.ComputeQuickActionState(
            [Entry("MEMORY.md")],
            null,
            AgentMemoryScope.LocalWorkspace,
            "new-topic.md",
            isSelectedFileUnsaved: true);

        Assert.False(unsavedState.CanDiscussSelected);
    }
}
