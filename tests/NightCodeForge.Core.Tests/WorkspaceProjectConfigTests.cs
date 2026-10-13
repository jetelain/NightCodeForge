using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class WorkspaceProjectConfigTests
{
    [Fact]
    public void ResolveConfig_Defaults_ProvidesStandardDotNetCommandsAndDefaultTimeouts()
    {
        var config = CodeStackPresets.ResolveConfig(null);

        Assert.Equal("dotnet build", config.BuildCommand);
        Assert.Equal("dotnet test --no-build", config.TestCommand);
        Assert.Equal(120, config.BuildTimeoutSeconds);
        Assert.Equal(120, config.TestTimeoutSeconds);
    }

    [Fact]
    public async Task ResolveConfig_ReadsConfigFile_WithCustomTimeoutsAndCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-config-test-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(tempDir, ".nightcodeforge");
        Directory.CreateDirectory(configDir);

        try
        {
            var configJson = @"{
  ""codeStack"": ""python"",
  ""buildCommand"": ""poetry install"",
  ""testCommand"": ""poetry run pytest"",
  ""buildTimeoutSeconds"": 180,
  ""testTimeoutSeconds"": 240
}";
            await File.WriteAllTextAsync(Path.Combine(configDir, "config.json"), configJson);

            var config = CodeStackPresets.ResolveConfig(tempDir);

            Assert.Equal("python", config.CodeStack);
            Assert.Equal("poetry install", config.BuildCommand);
            Assert.Equal("poetry run pytest", config.TestCommand);
            Assert.Equal(180, config.BuildTimeoutSeconds);
            Assert.Equal(240, config.TestTimeoutSeconds);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveConfig_DataBagOverrides_TakePrecedence()
    {
        var bag = new PipelineDataBag();
        bag.Set("BuildCommand", "cargo build");
        bag.Set("TestCommand", "cargo test");
        bag.Set("BuildTimeoutSeconds", 300);
        bag.Set("TestTimeoutSeconds", 450);

        var config = CodeStackPresets.ResolveConfig(null, bag);

        Assert.Equal("cargo build", config.BuildCommand);
        Assert.Equal("cargo test", config.TestCommand);
        Assert.Equal(300, config.BuildTimeoutSeconds);
        Assert.Equal(450, config.TestTimeoutSeconds);
    }
}
