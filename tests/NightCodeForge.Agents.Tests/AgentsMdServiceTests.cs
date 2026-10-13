using NightCodeForge.Agents.Context;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class AgentsMdServiceTests
{
    [Fact]
    public void FormatExecutionGuidelines_DefaultFallbacks_Produces120sLimits()
    {
        var service = new AgentsMdService();

        var guidelines = service.FormatExecutionGuidelines(null, null);

        Assert.Contains("<execution_guidelines>", guidelines);
        Assert.Contains("Build Command: `dotnet build` (Timeout: 120s)", guidelines);
        Assert.Contains("Test Command: `dotnet test --no-build` (Timeout: 120s)", guidelines);
        Assert.Contains("CRITICAL RESOURCE PROTECTION", guidelines);
        Assert.Contains("infinite loops", guidelines);
    }

    [Fact]
    public void FormatExecutionGuidelines_CustomWorkspaceAndProjectConfig_ReflectsCustomTimeouts()
    {
        var service = new AgentsMdService();
        var projectConfig = new WorkspaceProjectConfig
        {
            BuildCommand = "npm run build",
            TestCommand = "npm test",
            BuildTimeoutSeconds = 90,
            TestTimeoutSeconds = 180
        };
        var workspace = new Workspace
        {
            BuildTimeoutSeconds = 60 // Workspace level overrides project config if set
        };

        var guidelines = service.FormatExecutionGuidelines(projectConfig, workspace);

        Assert.Contains("Build Command: `npm run build` (Timeout: 60s)", guidelines);
        Assert.Contains("Test Command: `npm test` (Timeout: 180s)", guidelines);
    }

    [Fact]
    public void FormatProjectContextSection_CombinesAgentsMdRulesAndExecutionGuidelines()
    {
        var service = new AgentsMdService();
        var projectConfig = new WorkspaceProjectConfig
        {
            BuildCommand = "dotnet build -c Release",
            TestCommand = "dotnet test -c Release",
            BuildTimeoutSeconds = 200,
            TestTimeoutSeconds = 300
        };

        var section = service.FormatProjectContextSection("Always follow C# 12 conventions.", projectConfig);

        Assert.Contains("<project_context>", section);
        Assert.Contains("Always follow C# 12 conventions.", section);
        Assert.Contains("</project_context>", section);
        Assert.Contains("<execution_guidelines>", section);
        Assert.Contains("Timeout: 200s", section);
        Assert.Contains("Timeout: 300s", section);
    }

    [Fact]
    public async Task IngestRulesAsync_DiscoversAgentsMdFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-agentsmd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "AGENTS.md"), "Rule 1: Fast tests only.");

            var service = new AgentsMdService();
            var rules = await service.IngestRulesAsync(tempDir);

            Assert.Equal("Rule 1: Fast tests only.", rules);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
