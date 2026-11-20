using NightCodeForge.Core.Memory;

namespace NightCodeForge.Core.Tests;

public class AgentMemoryPathClassifierTests
{
    [Theory]
    [InlineData("/workspace/memory/MEMORY.md")]
    [InlineData("/workspace/memory/ideation-vision.md")]
    [InlineData(@"\workspace\memory\Topic.MD")]
    [InlineData("workspace/memory/topic.md")]
    [InlineData("./workspace/memory/topic.md")]
    [InlineData("/workspace/memory/./topic.md")]
    [InlineData("~/.nightcodeforge/wks/42/memory/notes.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/7/memory/MEMORY.md")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\7\memory\topic.md")]
    public void Classify_LocalWorkspacePaths_ReturnsLocalWorkspace(string path)
    {
        Assert.Equal(AgentMemoryScope.LocalWorkspace, AgentMemoryPathClassifier.Classify(path));
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData(".agents/AGENTS.md")]
    [InlineData(".agents/memory/architecture-decisions.md")]
    [InlineData("./.agents/memory/arch.md")]
    [InlineData("/workspace/clone/.agents/memory/arch.md")]
    [InlineData("/workspace/task-42/.agents/memory/arch.md")]
    [InlineData("/workspace/task-42/AGENTS.md")]
    [InlineData("/workspace/task-42/./AGENTS.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/7/task-42/.agents/memory/arch.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/7/clone/AGENTS.md")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\7\task-42\AGENTS.md")]
    [InlineData(@".agents\memory\win.md")]
    public void Classify_SharedRepositoryPaths_ReturnsSharedRepository(string path)
    {
        Assert.Equal(AgentMemoryScope.SharedRepository, AgentMemoryPathClassifier.Classify(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/workspace/memory")]
    [InlineData("/workspace/memory/")]
    [InlineData("/workspace/memory/script.sh")]
    [InlineData("/workspace/memory/.hidden.md")]
    [InlineData("/workspace/memory/.md")]
    [InlineData("/workspace/memory/sub/nested.md")]
    [InlineData("~/.nightcodeforge/wks/7/memory/sub/nested.md")]
    [InlineData("~/.nightcodeforge/wks/abc/memory/x.md")]
    [InlineData(".agents/memory/sub/nested.md")]
    [InlineData(".agents/memory/.hidden.md")]
    [InlineData(".agents/memory/notes.txt")]
    [InlineData("src/Program.cs")]
    [InlineData("docs/tasks/task-1.md")]
    public void Classify_NonMemoryOrInvalidPaths_ReturnsNone(string? path)
    {
        Assert.Equal(AgentMemoryScope.None, AgentMemoryPathClassifier.Classify(path));
    }

    [Fact]
    public void ToMetadataValue_MapsExpectedValues_AndRejectsNone()
    {
        Assert.Equal(AgentMemoryConstants.ScopeLocalMetadataValue, AgentMemoryConstants.ToMetadataValue(AgentMemoryScope.LocalWorkspace));
        Assert.Equal(AgentMemoryConstants.ScopeSharedGitMetadataValue, AgentMemoryConstants.ToMetadataValue(AgentMemoryScope.SharedRepository));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentMemoryConstants.ToMetadataValue(AgentMemoryScope.None));
    }

    [Fact]
    public void ClassifyWithRepositoryRoot_AcceptsBareHostRepoPaths_AndRejectsSubdirsAndOtherRoots()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "ncf-repo-root");
        var otherRoot = Path.Combine(Path.GetTempPath(), "ncf-other-root");

        Assert.Equal(AgentMemoryScope.SharedRepository, AgentMemoryPathClassifier.Classify(Path.Combine(repoRoot, "AGENTS.md"), repoRoot));
        Assert.Equal(AgentMemoryScope.SharedRepository, AgentMemoryPathClassifier.Classify(Path.Combine(repoRoot, ".agents", "AGENTS.md"), repoRoot));
        Assert.Equal(AgentMemoryScope.SharedRepository, AgentMemoryPathClassifier.Classify(Path.Combine(repoRoot, ".agents", "memory", "arch.md"), repoRoot));

        Assert.True(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(Path.Combine(repoRoot, "AGENTS.md"), repoRoot, out var c1));
        Assert.Equal("AGENTS.md", c1);
        Assert.True(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(Path.Combine(repoRoot, ".agents", "AGENTS.md"), repoRoot, out var c2));
        Assert.Equal(".agents/AGENTS.md", c2);
        Assert.True(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(Path.Combine(repoRoot, ".agents", "memory", "arch.md"), repoRoot, out var c3));
        Assert.Equal(".agents/memory/arch.md", c3);

        Assert.Equal(AgentMemoryScope.None, AgentMemoryPathClassifier.Classify(Path.Combine(repoRoot, "sub", "AGENTS.md"), repoRoot));
        Assert.Equal(AgentMemoryScope.None, AgentMemoryPathClassifier.Classify(Path.Combine(otherRoot, "AGENTS.md"), repoRoot));
        Assert.Equal(AgentMemoryScope.None, AgentMemoryPathClassifier.Classify("/etc/AGENTS.md", repoRoot));
        Assert.False(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(Path.Combine(repoRoot, "sub", "AGENTS.md"), repoRoot, out _));
        Assert.False(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(Path.Combine(otherRoot, "AGENTS.md"), repoRoot, out _));
        Assert.False(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath("/etc/AGENTS.md", repoRoot, out _));
    }

    [Theory]
    [InlineData("agents.md", "AGENTS.md")]
    [InlineData("./AGENTS.md", "AGENTS.md")]
    [InlineData("/workspace/task-42/./agents.md", "AGENTS.md")]
    [InlineData(".agents/agents.md", ".agents/AGENTS.md")]
    [InlineData("/workspace/clone/.agents/AGENTS.md", ".agents/AGENTS.md")]
    [InlineData("/home/dev/.nightcodeforge/wks/7/task-42/.agents/memory/arch.md", ".agents/memory/arch.md")]
    public void TryGetCanonicalSharedRelativePath_CanonicalizesCasingAndPrefixes(string input, string expected)
    {
        Assert.True(AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(input, repositoryRoot: null, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void AgentMemoryReparseGuard_HandlesMissingFilesNormalFilesAndDanglingSymlinks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf-reparse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var nonExistent = Path.Combine(tempDir, "missing.md");
            var normalFile = Path.Combine(tempDir, "normal.md");
            File.WriteAllText(normalFile, "ok");

            Assert.False(AgentMemoryReparseGuard.IsReparsePoint(nonExistent));
            Assert.False(AgentMemoryReparseGuard.IsReparsePoint(normalFile));
            Assert.False(AgentMemoryReparseGuard.HasReparsePointInChain(tempDir, nonExistent));
            Assert.False(AgentMemoryReparseGuard.HasReparsePointInChain(tempDir, normalFile));

            var dangling = Path.Combine(tempDir, "dangling.md");
            try
            {
                File.CreateSymbolicLink(dangling, Path.Combine(tempDir, "target-does-not-exist.md"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // Symlink creation not permitted on this host
            }

            Assert.True(AgentMemoryReparseGuard.IsReparsePoint(dangling));
            Assert.True(AgentMemoryReparseGuard.HasReparsePointInChain(tempDir, dangling));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
