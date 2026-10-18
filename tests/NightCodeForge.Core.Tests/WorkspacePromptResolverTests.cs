using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class WorkspacePromptResolverTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspacePromptResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_prompt_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData("../traversal")]
    [InlineData("..\\traversal")]
    [InlineData("sub/stage")]
    [InlineData("sub\\stage")]
    [InlineData("stage space")]
    [InlineData("stage;injection")]
    [InlineData("stage*")]
    [InlineData("stage?")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStageId_RejectsInvalidStageIds_WithArgumentException(string invalidStageId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            WorkspacePromptResolver.ValidateStageId(invalidStageId));
    }

    [Theory]
    [InlineData("stage-compile")]
    [InlineData("stage_1")]
    [InlineData("stage1")]
    [InlineData("IMPLEMENTATION")]
    public void ValidateStageId_AcceptsValidStageIds(string validStageId)
    {
        WorkspacePromptResolver.ValidateStageId(validStageId);
    }

    [Fact]
    public void ResolveSystemPrompt_LoadsFromNightCodeForgePromptsDirectory()
    {
        var promptsDir = Path.Combine(_tempDir, NightCodeForgeDirectories.Root, NightCodeForgeDirectories.Prompts);
        Directory.CreateDirectory(promptsDir);

        var promptFile = Path.Combine(promptsDir, "system.md");
        File.WriteAllText(promptFile, "Custom workspace system prompt.");

        var loaded = WorkspacePromptResolver.ResolveSystemPrompt(_tempDir);

        Assert.Equal("Custom workspace system prompt.", loaded);
    }

    [Fact]
    public void ResolveSystemPrompt_ReturnsFallback_WhenFileDoesNotExist()
    {
        var loaded = WorkspacePromptResolver.ResolveSystemPrompt(_tempDir, defaultPrompt: "default-fallback");

        Assert.Equal("default-fallback", loaded);
    }

    [Fact]
    public void ResolveStageDirective_LoadsFromPromptsDirectory()
    {
        var promptsDir = Path.Combine(_tempDir, NightCodeForgeDirectories.Root, NightCodeForgeDirectories.Prompts);
        Directory.CreateDirectory(promptsDir);

        var promptFile = Path.Combine(promptsDir, "code-review.md");
        File.WriteAllText(promptFile, "Custom stage directive.");

        var loaded = WorkspacePromptResolver.ResolveStageDirective(_tempDir, "code-review", "default-directive");

        Assert.Equal("Custom stage directive.", loaded);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup in tests
        }
    }
}
