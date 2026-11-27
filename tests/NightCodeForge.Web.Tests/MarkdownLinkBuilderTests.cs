using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Memory;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MarkdownLinkBuilderTests
{
    private const int WorkspaceId = 7;

    [Theory]
    [InlineData("/workspace/memory/notes.md", "/w/7/memory?scope=local&path=notes.md")]
    [InlineData("workspace/memory/MEMORY.md", "/w/7/memory?scope=local&path=MEMORY.md")]
    [InlineData("/workspace/memory/notes.md#heading-1", "/w/7/memory?scope=local&path=notes.md#heading-1")]
    [InlineData("~/.nightcodeforge/wks/7/memory/config.md", "/w/7/memory?scope=local&path=config.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/7/memory/MEMORY.md", "/w/7/memory?scope=local&path=MEMORY.md")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\7\memory\notes.md", "/w/7/memory?scope=local&path=notes.md")]
    [InlineData(@"C:\Users\Dev\.nightcodeforge\wks\7\memory\notes.md", "/w/7/memory?scope=local&path=notes.md")]
    public void ResolveLink_Tier2LocalMemoryVirtualAndHostPaths_RoutesToLocalMemory(string url, string expected)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(expected, result.TargetUrl);
    }

    [Theory]
    [InlineData("config-file-decisions.md", "/w/7/memory?scope=local&path=config-file-decisions.md")]
    [InlineData("./config-file-decisions.md", "/w/7/memory?scope=local&path=config-file-decisions.md")]
    [InlineData("./notes.md#section", "/w/7/memory?scope=local&path=notes.md#section")]
    public void ResolveLink_LocalMemoryScope_RelativeFlatMarkdown_RoutesToLocalMemory(string url, string expected)
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };
        var context = MarkdownLinkContext.ForMemory(workspace, AgentMemoryScope.LocalWorkspace, "MEMORY.md");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(expected, result.TargetUrl);
    }

    [Theory]
    [InlineData("../clone/README.md")]
    [InlineData("sub/file.md")]
    [InlineData(".hidden.md")]
    [InlineData("notes.txt")]
    [InlineData("/workspace/memory/../clone/README.md")]
    [InlineData("/workspace/memory")]
    public void ResolveLink_LocalMemoryTraversalOrNonFlat_IsNeutralized(string url)
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };
        var context = MarkdownLinkContext.ForMemory(workspace, AgentMemoryScope.LocalWorkspace, "MEMORY.md");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Neutralized, result.Kind);
    }

    [Theory]
    [InlineData("AGENTS.md", "/w/7/memory?scope=shared&path=AGENTS.md")]
    [InlineData(".agents/AGENTS.md", "/w/7/memory?scope=shared&path=.agents%2FAGENTS.md")]
    [InlineData("/workspace/clone/.agents/memory/rules.md", "/w/7/memory?scope=shared&path=.agents%2Fmemory%2Frules.md")]
    [InlineData("/workspace/clone/AGENTS.md#conventions", "/w/7/memory?scope=shared&path=AGENTS.md#conventions")]
    public void ResolveLink_Tier1SharedMemory_RoutesToSharedMemory(string url, string expected)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(expected, result.TargetUrl);
    }

    [Fact]
    public void ResolveLink_SharedMemoryScope_ResolvesRelativeSiblingsAndParentDocs()
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };
        var context = MarkdownLinkContext.ForMemory(workspace, AgentMemoryScope.SharedRepository, ".agents/memory/index.md");

        var sibling = MarkdownLinkBuilder.ResolveLink("sibling.md", context);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, sibling.Kind);
        Assert.Equal("/w/7/memory?scope=shared&path=.agents%2Fmemory%2Fsibling.md", sibling.TargetUrl);

        var parentPlan = MarkdownLinkBuilder.ResolveLink("../../docs/PLAN.md#architecture", context);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, parentPlan.Kind);
        Assert.Equal("/w/7/files?ref=main&path=docs%2FPLAN.md#architecture", parentPlan.TargetUrl);
    }

    [Theory]
    [InlineData("/workspace/clone/AGENTS.md#L12-L20", "/w/7/files?ref=main&path=AGENTS.md#L12-L20")]
    [InlineData(".agents/memory/rules.md:42", "/w/7/files?ref=main&path=.agents%2Fmemory%2Frules.md#L42")]
    [InlineData("/workspace/task-42/.agents/memory/rules.md", "/w/7/files?ref=feature%2Ftask-42&path=.agents%2Fmemory%2Frules.md&taskId=42")]
    public void ResolveLink_SharedMemoryWithLineAnchorOrTaskRef_RoutesToFileViewer(string url, string expected)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(expected, result.TargetUrl);
    }

    [Fact]
    public void ResolveLink_RepositoryFileScope_SharedMemoryOnNonDefaultBranchRoutesToFileViewer_DefaultBranchRoutesToMemory()
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };

        var nonDefaultCtx = MarkdownLinkContext.ForRepositoryFile(workspace, "feature/task-42", "docs/PLAN.md", 42);
        var onFeature = MarkdownLinkBuilder.ResolveLink("../AGENTS.md", nonDefaultCtx);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, onFeature.Kind);
        Assert.Equal("/w/7/files?ref=feature%2Ftask-42&path=AGENTS.md&taskId=42", onFeature.TargetUrl);

        var defaultCtx = MarkdownLinkContext.ForRepositoryFile(workspace, "main", "docs/PLAN.md");
        var onDefault = MarkdownLinkBuilder.ResolveLink("../AGENTS.md", defaultCtx);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, onDefault.Kind);
        Assert.Equal("/w/7/memory?scope=shared&path=AGENTS.md", onDefault.TargetUrl);
    }

    [Fact]
    public void ResolveLink_TaskMarkdownTargets_UseDirectTaskIdWhenInLookup_ElseByFileResolverRoute()
    {
        var tasks = new[]
        {
            new ForgeTask { Id = 15, WorkspaceId = WorkspaceId, Title = "My Task" }
        };
        var lookup = MarkdownLinkContext.BuildTaskPathLookup(tasks);
        var ctxWithLookup = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main", lookup);

        var direct = MarkdownLinkBuilder.ResolveLink("docs/tasks/2026-11-26-my-task.md", ctxWithLookup);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, direct.Kind);
        Assert.Equal("/w/7/tasks/15", direct.TargetUrl);

        var ctxWithoutLookup = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var byFile = MarkdownLinkBuilder.ResolveLink("/workspace/clone/docs/tasks/2026-11-26-my-task.md", ctxWithoutLookup);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, byFile.Kind);
        Assert.Equal("/w/7/tasks/by-file?path=docs%2Ftasks%2F2026-11-26-my-task.md&ref=main", byFile.TargetUrl);

        var archivedTaskRef = MarkdownLinkBuilder.ResolveLink("/workspace/task-42/docs/tasks/archive/2026-11-20-old-task.md", ctxWithoutLookup);
        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, archivedTaskRef.Kind);
        Assert.Equal("/w/7/tasks/by-file?path=docs%2Ftasks%2Farchive%2F2026-11-20-old-task.md&ref=feature%2Ftask-42&taskId=42", archivedTaskRef.TargetUrl);
    }

    [Theory]
    [InlineData("/workspace/clone", "/w/7/files?ref=main")]
    [InlineData("/workspace/clone/", "/w/7/files?ref=main")]
    [InlineData("/workspace/task-42", "/w/7/files?ref=feature%2Ftask-42&taskId=42")]
    [InlineData("/workspace/task-42/src/Bar.cs:42", "/w/7/files?ref=feature%2Ftask-42&path=src%2FBar.cs&taskId=42#L42")]
    [InlineData("/workspace/clone/src/Foo.cs#L10-L25", "/w/7/files?ref=main&path=src%2FFoo.cs#L10-L25")]
    [InlineData("src/Foo.cs#l10-25", "/w/7/files?ref=main&path=src%2FFoo.cs#L10-L25")]
    [InlineData("README.md:42", "/w/7/files?ref=main&path=README.md#L42")]
    [InlineData("Program.cs:10-20", "/w/7/files?ref=main&path=Program.cs#L10-L20")]
    [InlineData("docs/my%20file.md#overview", "/w/7/files?ref=main&path=docs%2Fmy%20file.md#overview")]
    public void ResolveLink_RepositoryFilesDirectoriesAndLineRanges_NormalizeCorrectly(string url, string expected)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(expected, result.TargetUrl);
    }

    [Fact]
    public void ResolveLink_RepositoryFileScope_ResolvesRelativeToCurrentDocumentPath()
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };
        var context = MarkdownLinkContext.ForRepositoryFile(workspace, "feature/task-12", "docs/PLAN.md", 12);

        var result = MarkdownLinkBuilder.ResolveLink("features/STEP_12_46A_GIT_BRANCH_FILE_VIEWER.md#31-overview", context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal(
            "/w/7/files?ref=feature%2Ftask-12&path=docs%2Ffeatures%2FSTEP_12_46A_GIT_BRANCH_FILE_VIEWER.md&taskId=12#31-overview",
            result.TargetUrl);
    }

    [Theory]
    [InlineData("~/.nightcodeforge/wks/8/clone/README.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/99/memory/MEMORY.md")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\8\task-1\src\Foo.cs")]
    [InlineData(@"C:\Users\Dev\.nightcodeforge\wks\8\clone\src\Foo.cs")]
    public void ResolveLink_CrossWorkspaceHostPaths_AreNeutralized(string url)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Neutralized, result.Kind);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/workspace/clone/docs/../../etc/passwd")]
    [InlineData("/workspace/clone/%2e%2e/%2e%2e/etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    public void ResolveLink_TraversalAndArbitraryRootedPaths_AreNeutralized(string url)
    {
        var workspace = new Workspace { Id = WorkspaceId, DefaultBaseBranch = "main" };
        var context = MarkdownLinkContext.ForRepositoryFile(workspace, "main", "docs/PLAN.md");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Neutralized, result.Kind);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](javascript:42)")]
    [InlineData("[x](java\tscript:42)")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("[x](blob:https://example.com/1)")]
    public void MarkdownHelper_WithContext_NeutralizesAllUnsafeSchemesIncludingColonDigits(string markdown)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var html = MarkdownHelper.ToHtml(markdown, context);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkdownHelper_WithContext_PreservesRootColonLineLinks_AndNeverRewritesImagesOrAutolinks()
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var md = "[readme](README.md:42) ![img](/workspace/clone/logo.png) <https://example.com> <javascript:alert(1)> <script>alert(1)</script> [a](https://example.com){onclick=alert(1)}";

        var html = MarkdownHelper.ToHtml(md, context);

        Assert.Contains("href=\"/w/7/files?ref=main&amp;path=README.md#L42\"", html);
        Assert.Contains("src=\"/workspace/clone/logo.png\"", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("{onclick=alert(1)}", html);
    }

    [Fact]
    public void MarkdownHelper_WorkspaceLessContext_PreservesExternalAndFragments_NeutralizesRepoPaths()
    {
        var md = "[ext](https://example.com) [mail](mailto:dev@example.com) [sec](#overview) [file](src/Foo.cs) [mem](/workspace/memory/MEMORY.md)";

        var html = MarkdownHelper.ToHtml(md, MarkdownLinkContext.Empty);

        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("href=\"mailto:dev@example.com\"", html);
        Assert.Contains("href=\"#overview\"", html);
        Assert.DoesNotContain("src/Foo.cs", html);
        Assert.DoesNotContain("/workspace/memory", html);
    }

    [Fact]
    public void MarkdownHelper_ConcurrentRenderingAcrossContexts_IsThreadSafeAndIsolated()
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 200, i =>
        {
            var wsId = (i % 5) + 1;
            var branch = $"feature/task-{wsId * 10}";
            var ctx = MarkdownLinkContext.ForRepositoryFile(
                new Workspace { Id = wsId, DefaultBaseBranch = "main" },
                branch,
                "docs/PLAN.md",
                wsId * 10);

            var html = MarkdownHelper.ToHtml("[guide](guide.md:12) and [root](../README.md:3)", ctx);
            var expectedGuide = $"/w/{wsId}/files?ref=feature%2Ftask-{wsId * 10}&amp;path=docs%2Fguide.md&amp;taskId={wsId * 10}#L12";
            var expectedRoot = $"/w/{wsId}/files?ref=feature%2Ftask-{wsId * 10}&amp;path=README.md&amp;taskId={wsId * 10}#L3";

            if (!html.Contains(expectedGuide, StringComparison.Ordinal) ||
                !html.Contains(expectedRoot, StringComparison.Ordinal))
            {
                errors.Add($"Iteration {i} ws={wsId}: {html}");
            }
        });

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("/w/7/%2e%2e/api/secret")]
    [InlineData(@"\w\7\backlog")]
    [InlineData("/w/7/back\tlog")]
    public void ResolveLink_AppRouteWithEncodedTraversalBackslashOrControlChar_IsNeutralized(string url)
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink(url, context);

        Assert.Equal(MarkdownLinkResolutionKind.Neutralized, result.Kind);
    }

    [Fact]
    public void ResolveLink_DoubleSlashUnderWorkspaceMemory_ResolvesToLocalMemory()
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink("/workspace/memory//notes.md", context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal("/w/7/memory?scope=local&path=notes.md", result.TargetUrl);
    }

    [Fact]
    public void ResolveLink_ColonLineWithQueryString_PreservesLineRangeAndStripsQuery()
    {
        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink("README.md:42?foo=bar", context);

        Assert.Equal(MarkdownLinkResolutionKind.Rewritten, result.Kind);
        Assert.Equal("/w/7/files?ref=main&path=README.md#L42", result.TargetUrl);
    }

    [Fact]
    public void TryNormalizeAndResolve_InvalidFileNameCharacters_AreRejected()
    {
        Assert.False(MarkdownLinkBuilder.TryNormalizeTaskMarkdownPath("docs/tasks/bad:name.md", out _, out _, out _));

        var context = MarkdownLinkContext.ForGeneralWorkspace(WorkspaceId, "main");
        var result = MarkdownLinkBuilder.ResolveLink("/workspace/memory/bad:name.md", context);
        Assert.Equal(MarkdownLinkResolutionKind.Neutralized, result.Kind);
    }
}
