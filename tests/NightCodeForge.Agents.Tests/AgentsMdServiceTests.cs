using NightCodeForge.Agents.Context;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

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
        Assert.Contains("Incremental Compilation Invariant", guidelines);
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

    [Theory]
    [InlineData("pytest", false)]
    [InlineData("dotnet test", false)]
    [InlineData("dotnet test --no-build", true)]
    [InlineData("dotnet test --NO-BUILD", true)]
    public void FormatExecutionGuidelines_ConditionallyIncludesNoBuildWarning(string testCommand, bool expectNoBuildWarning)
    {
        var service = new AgentsMdService();
        var projectConfig = new WorkspaceProjectConfig
        {
            BuildCommand = "custom build",
            TestCommand = testCommand
        };

        var guidelines = service.FormatExecutionGuidelines(projectConfig, new Workspace());

        if (expectNoBuildWarning)
        {
            Assert.Contains("Never run test commands with `--no-build`", guidelines, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("--no-build", guidelines, StringComparison.OrdinalIgnoreCase);
        }
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

    [Fact]
    public void Step1164_FormatExecutionGuidelines_StripsNoIncremental_AndOmitsBuildCommandWhenNullOrEmpty()
    {
        // Deterministic stage defaults MUST keep --no-incremental intact.
        Assert.Equal("dotnet build --no-incremental", DeterministicStageConstants.DefaultBuildCommand);
        Assert.Equal("dotnet build --no-incremental", CodeStackPresets.DotNet.BuildCommand);

        Assert.Equal("dotnet build", AgentsMdService.StripNoIncrementalFlag("dotnet build --no-incremental"));
        Assert.Equal("dotnet build src/App.sln", AgentsMdService.StripNoIncrementalFlag("dotnet build src/App.sln --no-incremental"));
        Assert.Equal("dotnet build && dotnet test", AgentsMdService.StripNoIncrementalFlag("dotnet build --no-incremental&& dotnet test"));
        Assert.Equal("dotnet build ; dotnet test", AgentsMdService.StripNoIncrementalFlag("dotnet build --no-incremental; dotnet test"));
        Assert.Equal("dotnet build --no-incremental-custom", AgentsMdService.StripNoIncrementalFlag("dotnet build --no-incremental-custom"));

        var service = new AgentsMdService();

        var dotnetGuidelines = service.FormatExecutionGuidelines(
            new WorkspaceProjectConfig
            {
                CodeStack = CodeStack.DotNet,
                BuildCommand = "dotnet build src/App.sln --no-incremental",
                TestCommand = "dotnet test src/App.sln --no-build"
            },
            new Workspace());
        Assert.Contains("Build Command: `dotnet build src/App.sln`", dotnetGuidelines, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-incremental", dotnetGuidelines, StringComparison.OrdinalIgnoreCase);

        foreach (var emptyBuild in new string?[] { null, "", "   ", "--no-incremental" })
        {
            var nodeGuidelines = service.FormatExecutionGuidelines(
                new WorkspaceProjectConfig
                {
                    CodeStack = CodeStack.Node,
                    BuildCommand = emptyBuild,
                    TestCommand = "npx vitest run -c vitest.unit.config.ts"
                },
                new Workspace());
            Assert.DoesNotContain("Build Command:", nodeGuidelines, StringComparison.Ordinal);
            Assert.DoesNotContain("Incremental Compilation Invariant", nodeGuidelines, StringComparison.Ordinal);
            Assert.Contains("Test Command: `npx vitest run -c vitest.unit.config.ts`", nodeGuidelines, StringComparison.Ordinal);
        }
    }
}
