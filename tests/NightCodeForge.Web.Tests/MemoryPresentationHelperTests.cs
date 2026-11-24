using System.Text.RegularExpressions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Memory;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MemoryPresentationHelperTests
{
    [Theory]
    [InlineData("write", "{\"path\":\"/workspace/memory/ideation-vision.md\"}", AgentMemoryScope.LocalWorkspace, "ideation-vision.md")]
    [InlineData("edit", "{\"path\":\".agents/memory/architecture.md\"}", AgentMemoryScope.SharedRepository, ".agents/memory/architecture.md")]
    [InlineData("write", "{\"path\":\"/workspace/clone/AGENTS.md\"}", AgentMemoryScope.SharedRepository, "AGENTS.md")]
    [InlineData("WRITE", "{\"path\":\"AGENTS.md\"}", AgentMemoryScope.SharedRepository, "AGENTS.md")]
    public void TryGetToolTarget_MemoryWrites_AreDetected(string tool, string args, AgentMemoryScope scope, string relative)
    {
        var target = MemoryPresentationHelper.TryGetToolTarget(tool, args);

        Assert.NotNull(target);
        Assert.Equal(scope, target.Scope);
        Assert.Equal(relative, target.RelativePath);
        Assert.Contains("🧠 Memory", target.BadgeText);
    }

    [Theory]
    [InlineData("read", "{\"path\":\"/workspace/memory/a.md\"}")]
    [InlineData("write", "{\"path\":\"src/Program.cs\"}")]
    [InlineData("write", "{\"path\":\"/workspace/memory/sub/a.md\"}")]
    [InlineData("write", "{\"path\":\"/workspace/memory/a.txt\"}")]
    [InlineData("write", "{\"path\":")]
    [InlineData("write", null)]
    [InlineData(null, "{\"path\":\"/workspace/memory/a.md\"}")]
    public void TryGetToolTarget_NonMemory_ReturnsNull(string? tool, string? args)
    {
        Assert.Null(MemoryPresentationHelper.TryGetToolTarget(tool, args));
    }

    [Fact]
    public void TryGetToolTarget_WriteResultMentioningCreated_IsCreated()
    {
        var target = MemoryPresentationHelper.TryGetToolTarget("write", "{\"path\":\"/workspace/memory/a.md\"}", "File created");

        Assert.Equal(AgentMemoryChangeKind.Created, target!.ChangeKind);
        Assert.Contains("Created", target.BadgeText);
    }

    [Fact]
    public void FormatBadgeText_DistinguishesScopes()
    {
        Assert.Contains("Local", MemoryPresentationHelper.FormatBadgeText(AgentMemoryScope.LocalWorkspace, null));
        Assert.Contains("Shared/Git", MemoryPresentationHelper.FormatBadgeText(AgentMemoryScope.SharedRepository, AgentMemoryChangeKind.Deleted));
    }

    [Fact]
    public void FormatToastMessage_IdeationAndTask()
    {
        Assert.Equal("Ideation updated /workspace/memory/v.md",
            MemoryPresentationHelper.FormatToastMessage(null, "", "Updated", "/workspace/memory/v.md", "x"));
        Assert.Equal("Task #42 (Stage 2) created .agents/memory/a.md",
            MemoryPresentationHelper.FormatToastMessage(42, "2", "Created", ".agents/memory/a.md", "x"));
        Assert.Equal("fallback summary",
            MemoryPresentationHelper.FormatToastMessage(null, "", "weird", "p.md", "fallback summary"));
    }

    [Fact]
    public void ParseScope_MapsMetadataValues()
    {
        Assert.Equal(AgentMemoryScope.LocalWorkspace, MemoryPresentationHelper.ParseScope(AgentMemoryConstants.ScopeLocalMetadataValue));
        Assert.Equal(AgentMemoryScope.SharedRepository, MemoryPresentationHelper.ParseScope(AgentMemoryConstants.ScopeSharedGitMetadataValue));
        Assert.Equal(AgentMemoryScope.None, MemoryPresentationHelper.ParseScope("other"));
    }

    [Fact]
    public void ExtractSummary_PrefersFrontmatterDescription_ElseFirstBodyLine()
    {
        Assert.Equal("Vision notes", MemoryPresentationHelper.ExtractSummary("---\nname: x\ndescription: \"Vision notes\"\n---\n# Title\nbody"));
        Assert.Equal("First line", MemoryPresentationHelper.ExtractSummary("# Title\n\nFirst line\nsecond"));
        Assert.Equal(string.Empty, MemoryPresentationHelper.ExtractSummary("  "));
    }

    [Fact]
    public void ExtractSummary_TruncatesLongLines()
    {
        var summary = MemoryPresentationHelper.ExtractSummary(new string('a', 500));

        Assert.True(summary.Length <= AgentMemoryConstants.MaxCatalogDescriptionLength + MemoryUiConstants.SummaryPreviewEllipsis.Length);
    }

    [Fact]
    public void ComputeBudget_FlagsOverBudget()
    {
        var ok = MemoryPresentationHelper.ComputeBudget("a\nb\nc\n");
        Assert.Equal(3, ok.Lines);
        Assert.False(ok.IsOverBudget);

        var tooManyLines = MemoryPresentationHelper.ComputeBudget(string.Join("\n", Enumerable.Repeat("x", AgentMemoryConstants.MaxPinnedMemoryLines + 1)));
        Assert.True(tooManyLines.IsOverBudget);

        var tooManyBytes = MemoryPresentationHelper.ComputeBudget(new string('x', AgentMemoryConstants.MaxPinnedMemoryBytes + 1));
        Assert.True(tooManyBytes.IsOverBudget);
        Assert.Equal(100d, tooManyBytes.BytesPercent);
    }

    [Fact]
    public void MarkdownHelper_DisablesRawHtml()
    {
        var html = MarkdownHelper.ToHtml("hello <script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
    }

    [Theory]
    [InlineData("![x](x){onerror=alert(1)}", "{onerror=alert(1)}")]
    [InlineData("# Title {onclick=alert(1)}", "{onclick=alert(1)}")]
    [InlineData("[a](https://example.com){onmouseover=alert(1)}", "{onmouseover=alert(1)}")]
    public void MarkdownHelper_StripsGenericAttributesXssPayloads(string markdown, string literalBracePayload)
    {
        var html = MarkdownHelper.ToHtml(markdown);

        Assert.DoesNotMatch(new Regex(@"<[^>]+\bon[a-z]+\s*=", RegexOptions.IgnoreCase), html);
        Assert.Contains(literalBracePayload, html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](&#x6a;avascript:alert(1))")]
    [InlineData("[click](java\tscript:alert(1))")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("[click](data:text/html,<script>alert(1)</script>)")]
    [InlineData("![img](mailto:user@example.com)")]
    [InlineData("<javascript:alert(1)>")]
    public void MarkdownHelper_StripsDangerousUrlSchemes(string markdown)
    {
        var html = MarkdownHelper.ToHtml(markdown);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mailto:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkdownHelper_PreservesSafeLinks()
    {
        Assert.Contains("href=\"https://example.com/a\"", MarkdownHelper.ToHtml("[docs](https://example.com/a)"));
        Assert.Contains("href=\"mailto:dev@example.com\"", MarkdownHelper.ToHtml("[mail](mailto:dev@example.com)"));
        Assert.Contains("href=\"./docs/a.md\"", MarkdownHelper.ToHtml("[rel](./docs/a.md)"));
        Assert.Contains("href=\"https://example.com\"", MarkdownHelper.ToHtml("<https://example.com>"));
    }

    [Fact]
    public void TryGetToolTarget_ErrorToolCalls_ReturnsNull()
    {
        Assert.Null(MemoryPresentationHelper.TryGetToolTarget("write", "{\"path\":\"/workspace/memory/a.md\"}", "ok", isError: true));
        Assert.Null(MemoryPresentationHelper.TryGetToolTarget("write", "{\"path\":\"/workspace/memory/a.md\"}", "Error: Access denied: Mount '/workspace/clone' is read-only."));
        Assert.Null(MemoryPresentationHelper.TryGetToolTarget("edit", "{\"path\":\"/workspace/memory/a.md\"}", "  Error: File not found"));
    }

    [Theory]
    [InlineData(StandardStageIds.TaskReview, "Stage 1")]
    [InlineData(StandardStageIds.Implementation, "Stage 2")]
    [InlineData(StandardStageIds.CodeReviewAndFix, "Stage 3")]
    [InlineData("stage-2", "Stage 2")]
    [InlineData("custom", "custom")]
    public void FormatToastMessage_FormatsStandardAndCustomStageIds(string stageId, string expectedLabel)
    {
        var message = MemoryPresentationHelper.FormatToastMessage(42, stageId, "Created", ".agents/memory/a.md", "fallback");

        Assert.Equal($"Task #42 ({expectedLabel}) created .agents/memory/a.md", message);
    }

    [Fact]
    public void ExtractSummary_HandlesLoneHorizontalRuleAndHeadingOnly()
    {
        Assert.Equal("Body line", MemoryPresentationHelper.ExtractSummary("---\nBody line"));
        Assert.Equal("Only Heading", MemoryPresentationHelper.ExtractSummary("# Only Heading"));
    }

    [Fact]
    public void ToMemoryRelativePath_StripsWksTaskWorktreeAndDotSegments()
    {
        Assert.Equal(
            ".agents/memory/arch.md",
            MemoryPresentationHelper.ToMemoryRelativePath(AgentMemoryScope.SharedRepository, "/home/dev/.nightcodeforge/wks/7/task-42/.agents/memory/arch.md"));
        Assert.Equal(
            "AGENTS.md",
            MemoryPresentationHelper.ToMemoryRelativePath(AgentMemoryScope.SharedRepository, "/workspace/task-42/./AGENTS.md"));
    }

    [Fact]
    public void BuildMemoryPageUrl_FormatsWorkspaceScopedAndDeepLinkUrls()
    {
        Assert.Equal("/w/5/memory", MemoryPresentationHelper.BuildMemoryPageUrl(5));
        Assert.Equal("/memory", MemoryPresentationHelper.BuildMemoryPageUrl(null));
        Assert.Equal(
            "/w/5/memory?scope=local&path=MEMORY.md",
            MemoryPresentationHelper.BuildMemoryPageUrl(5, AgentMemoryScope.LocalWorkspace, "MEMORY.md"));
        Assert.Equal(
            "/w/5/memory?scope=shared&path=.agents%2Fmemory%2Farch.md",
            MemoryPresentationHelper.BuildMemoryPageUrl(5, AgentMemoryScope.SharedRepository, ".agents/memory/arch.md"));
    }

    [Theory]
    [InlineData("local", true, AgentMemoryScope.LocalWorkspace)]
    [InlineData("LocalWorkspace", true, AgentMemoryScope.LocalWorkspace)]
    [InlineData("shared", true, AgentMemoryScope.SharedRepository)]
    [InlineData("SharedGit", true, AgentMemoryScope.SharedRepository)]
    [InlineData("SharedRepository", true, AgentMemoryScope.SharedRepository)]
    [InlineData("invalid", false, AgentMemoryScope.None)]
    public void TryParseScopeQueryValue_ParsesExpectedValues(string raw, bool expectedSuccess, AgentMemoryScope expectedScope)
    {
        var ok = MemoryPresentationHelper.TryParseScopeQueryValue(raw, out var scope);

        Assert.Equal(expectedSuccess, ok);
        Assert.Equal(expectedScope, scope);
    }

    [Fact]
    public void ResolveDeepLink_HandlesValidInvalidAndPathOnlyCases()
    {
        var empty = MemoryPresentationHelper.ResolveDeepLink(null, null);
        Assert.Null(empty.Scope);
        Assert.Null(empty.RelativePath);
        Assert.Null(empty.WarningMessage);

        var invalid = MemoryPresentationHelper.ResolveDeepLink("bogus", "a.md");
        Assert.Null(invalid.Scope);
        Assert.NotNull(invalid.WarningMessage);

        var sharedInferred = MemoryPresentationHelper.ResolveDeepLink(null, ".agents/memory/arch.md");
        Assert.Equal(AgentMemoryScope.SharedRepository, sharedInferred.Scope);
        Assert.Equal(".agents/memory/arch.md", sharedInferred.RelativePath);
        Assert.Null(sharedInferred.WarningMessage);

        var localExplicit = MemoryPresentationHelper.ResolveDeepLink("local", "MEMORY.md");
        Assert.Equal(AgentMemoryScope.LocalWorkspace, localExplicit.Scope);
        Assert.Equal("MEMORY.md", localExplicit.RelativePath);
        Assert.Null(localExplicit.WarningMessage);
    }

    [Fact]
    public void ComputeBudget_FlagsNearBudgetAndOverBudget()
    {
        var nearLines = string.Join("\n", Enumerable.Repeat("x", (int)Math.Ceiling(AgentMemoryConstants.MaxPinnedMemoryLines * 0.8)));
        var near = MemoryPresentationHelper.ComputeBudget(nearLines);
        Assert.True(near.IsNearBudget);
        Assert.False(near.IsOverBudget);

        var overLines = string.Join("\n", Enumerable.Repeat("x", AgentMemoryConstants.MaxPinnedMemoryLines + 1));
        var over = MemoryPresentationHelper.ComputeBudget(overLines);
        Assert.True(over.IsOverBudget);
        Assert.False(over.IsNearBudget);
    }

    [Fact]
    public void MemoryToolTarget_BadgeTooltip_ReferencesAgentMemoryPage()
    {
        var target = MemoryPresentationHelper.TryGetToolTarget("write", "{\"path\":\"/workspace/memory/a.md\"}");

        Assert.NotNull(target);
        Assert.Equal("Open a.md in Agent Memory", target.BadgeTooltip);
    }

    [Fact]
    public void ResolveDeepLink_NormalizesVirtualLocalPath_PreservesTraversalForValidation_AndWarnsOnMissingPath()
    {
        var virtualLocal = MemoryPresentationHelper.ResolveDeepLink(null, "/workspace/memory/MEMORY.md");
        Assert.Equal(AgentMemoryScope.LocalWorkspace, virtualLocal.Scope);
        Assert.Equal("MEMORY.md", virtualLocal.RelativePath);
        Assert.Null(virtualLocal.WarningMessage);

        var traversal = MemoryPresentationHelper.ResolveDeepLink("local", "../secret.md");
        Assert.Equal(AgentMemoryScope.LocalWorkspace, traversal.Scope);
        Assert.Equal("../secret.md", traversal.RelativePath);

        var missingPath = MemoryPresentationHelper.ResolveDeepLink("local", null);
        Assert.Null(missingPath.Scope);
        Assert.NotNull(missingPath.WarningMessage);
    }

    [Fact]
    public void BuildMemoryNotificationUrl_OmitsFileParamsForDeletedOrEmptyPath()
    {
        Assert.Equal(
            "/w/5/memory",
            MemoryPresentationHelper.BuildMemoryNotificationUrl(5, AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Deleted"));
        Assert.Equal(
            "/w/5/memory",
            MemoryPresentationHelper.BuildMemoryNotificationUrl(5, AgentMemoryConstants.ScopeLocalMetadataValue, "", "Updated"));
        Assert.Equal(
            "/w/5/memory?scope=shared&path=.agents%2Fmemory%2Farch.md",
            MemoryPresentationHelper.BuildMemoryNotificationUrl(5, AgentMemoryConstants.ScopeSharedGitMetadataValue, ".agents/memory/arch.md", "Updated"));
    }
}
