using NightCodeForge.Agents.Context;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Tests.Memory;

public sealed class AgentsMdServiceMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-mdm-" + Guid.NewGuid().ToString("N"));
    private readonly string _memoryDir;
    private readonly string _repoDir;
    private readonly AgentsMdService _service = new();

    public AgentsMdServiceMemoryTests()
    {
        _memoryDir = Path.Combine(_root, "wks", "memory");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_memoryDir);
        Directory.CreateDirectory(Path.Combine(_repoDir, ".agents", "memory"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildWorkspaceMemorySection_WrapsPinnedMemory()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- prefers tabs\n- rejected: mongo\n");

        var section = await _service.BuildWorkspaceMemorySectionAsync(_memoryDir);

        Assert.StartsWith("<workspace_memory>", section);
        Assert.EndsWith("</workspace_memory>", section);
        Assert.Contains("- rejected: mongo", section);
        Assert.DoesNotContain(AgentsMdService.PinnedMemoryTruncationNotice, section);
    }

    [Fact]
    public async Task BuildWorkspaceMemorySection_MissingOrBlankIsEmpty()
    {
        Assert.Equal(string.Empty, await _service.BuildWorkspaceMemorySectionAsync(_memoryDir));
        Assert.Equal(string.Empty, await _service.BuildWorkspaceMemorySectionAsync(null));
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "  \n ");
        Assert.Equal(string.Empty, await _service.BuildWorkspaceMemorySectionAsync(_memoryDir));
    }

    [Fact]
    public void TruncatePinnedMemory_CapsLinesAtLineBoundaryWithNotice()
    {
        var content = string.Join("\n", Enumerable.Range(1, AgentMemoryConstants.MaxPinnedMemoryLines + 25).Select(i => $"line {i}"));

        var result = AgentsMdService.TruncatePinnedMemory(content);

        var lines = result.Split('\n');
        Assert.Equal(AgentMemoryConstants.MaxPinnedMemoryLines + 1, lines.Length);
        Assert.Equal($"line {AgentMemoryConstants.MaxPinnedMemoryLines}", lines[^2]);
        Assert.Equal(AgentsMdService.PinnedMemoryTruncationNotice, lines[^1]);
    }

    [Fact]
    public void TruncatePinnedMemory_CapsBytesAtLineBoundaryWithNotice()
    {
        var longLine = new string('x', 1000);
        var content = string.Join("\n", Enumerable.Repeat(longLine, 30));

        var result = AgentsMdService.TruncatePinnedMemory(content);

        var body = result[..result.LastIndexOf('\n')];
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) <= AgentMemoryConstants.MaxPinnedMemoryBytes);
        Assert.All(body.Split('\n'), l => Assert.Equal(longLine, l));
        Assert.EndsWith(AgentsMdService.PinnedMemoryTruncationNotice, result);
    }

    [Fact]
    public void TruncatePinnedMemory_ShortContentIsUntouched()
    {
        Assert.Equal("a\nb", AgentsMdService.TruncatePinnedMemory("a\r\nb\r\n"));
    }

    [Fact]
    public void TruncatePinnedMemory_SingleLineBoundaryExactAndOverCap()
    {
        var exactCap = new string('a', AgentMemoryConstants.MaxPinnedMemoryBytes);
        Assert.Equal(exactCap, AgentsMdService.TruncatePinnedMemory(exactCap));

        var overCap = new string('a', AgentMemoryConstants.MaxPinnedMemoryBytes + 1);
        Assert.Equal(AgentsMdService.PinnedMemoryTruncationNotice, AgentsMdService.TruncatePinnedMemory(overCap));
        Assert.False(AgentsMdService.TruncatePinnedMemory(overCap).StartsWith('\n'));
    }

    [Fact]
    public async Task BuildMemoryCatalog_ListsSharedThenLocalExcludingPinnedIndex()
    {
        File.WriteAllText(Path.Combine(_repoDir, ".agents", "memory", "architecture-decisions.md"),
            "---\ndescription: Module boundaries between Core and Agents\n---\n# Architecture\ntext");
        File.WriteAllText(Path.Combine(_repoDir, ".agents", "memory", "b-testing.md"), "# Testing pitfalls\nbody");
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "pinned");
        File.WriteAllText(Path.Combine(_memoryDir, "ideation-vision.md"), "\n\nProduct roadmap constraints\nmore");
        File.WriteAllText(Path.Combine(_memoryDir, "notes.txt"), "ignored");

        var catalog = await _service.BuildMemoryCatalogAsync(_repoDir, _memoryDir);

        var lines = catalog.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("<memory_catalog>", lines[0]);
        Assert.Equal("- [Shared/Git] .agents/memory/architecture-decisions.md — Module boundaries between Core and Agents", lines[2]);
        Assert.Equal("- [Shared/Git] .agents/memory/b-testing.md — Testing pitfalls", lines[3]);
        Assert.Equal("- [Local] /workspace/memory/ideation-vision.md — Product roadmap constraints", lines[4]);
        Assert.Equal("</memory_catalog>", lines[^1]);
        Assert.DoesNotContain("MEMORY.md", catalog);
        Assert.DoesNotContain("notes.txt", catalog);
    }

    [Fact]
    public async Task BuildMemoryCatalog_NoTopicFilesIsEmpty()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "pinned");

        Assert.Equal(string.Empty, await _service.BuildMemoryCatalogAsync(_repoDir, _memoryDir));
    }

    [Fact]
    public async Task BuildMemoryCatalog_CapsEntriesAtMaxCatalogEntriesPerSource()
    {
        for (var i = 0; i < AgentMemoryConstants.MaxCatalogEntries + 10; i++)
        {
            File.WriteAllText(Path.Combine(_repoDir, ".agents", "memory", $"t{i:000}.md"), $"# Topic {i}");
        }

        var catalog = await _service.BuildMemoryCatalogAsync(_repoDir, null);

        Assert.Equal(AgentMemoryConstants.MaxCatalogEntries, catalog.Split('\n').Count(l => l.StartsWith("- [Shared/Git]")));
    }

    [Fact]
    public void ExtractCatalogDescription_TruncatesToMaxLength()
    {
        var description = AgentsMdService.ExtractCatalogDescription("# " + new string('a', 500));

        Assert.Equal(AgentMemoryConstants.MaxCatalogDescriptionLength, description.Length);
    }

    [Fact]
    public void ExtractCatalogDescription_HandlesLoneHorizontalRuleAndMultiHashHeadings()
    {
        Assert.Equal("Real Heading", AgentsMdService.ExtractCatalogDescription("---\n# Real Heading\nbody"));
        Assert.Equal("Secondary Heading", AgentsMdService.ExtractCatalogDescription("## Secondary Heading\nbody"));
        Assert.Equal("#hashtag not heading", AgentsMdService.ExtractCatalogDescription("#hashtag not heading\nBody after hashtag"));
        Assert.Equal("Real Heading", AgentsMdService.ExtractCatalogDescription("#hashtag not heading\n## Real Heading\nbody"));
    }

    [Fact]
    public async Task BuildMemoryCatalog_SkipsSymlinkedTopicFiles()
    {
        var secret = Path.Combine(_root, "secret.md");
        File.WriteAllText(secret, "# Stolen host secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_repoDir, ".agents", "memory", "evil.md"), secret);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var catalog = await _service.BuildMemoryCatalogAsync(_repoDir, null);

        Assert.DoesNotContain("evil.md", catalog);
        Assert.DoesNotContain("Stolen", catalog);
    }

    [Fact]
    public async Task IngestRulesAsync_SkipsSymlinkedAgentsMd()
    {
        var secret = Path.Combine(_root, "secret-rules.md");
        File.WriteAllText(secret, "EXFILTRATED_SECRET");
        File.WriteAllText(Path.Combine(_repoDir, "CLAUDE.md"), "Fallback safe rules");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_repoDir, "AGENTS.md"), secret);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var rules = await _service.IngestRulesAsync(_repoDir);

        Assert.Equal("Fallback safe rules", rules);
    }
}
