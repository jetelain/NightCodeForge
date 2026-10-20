namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;
using NightCodeForge.Core.Domain;

public class ConfigBootstrapperTests
{
    [Fact]
    public void GenerateConfigJson_ProducesValidJson_ConformingToWorkspaceProjectConfig()
    {
        var preset = CodeStackPresets.DotNet;
        var json = ConfigBootstrapper.GenerateConfigJson(preset, "main");

        Assert.Contains("\"codeStack\": \"dotnet\"", json);
        Assert.Contains("\"buildCommand\": \"dotnet build\"", json);
        Assert.Contains("\"testCommand\": \"dotnet test --no-build\"", json);
        Assert.Contains("\"formatCommand\": \"dotnet format\"", json);
        Assert.Contains("\"buildTimeoutSeconds\": 180", json);
        Assert.Contains("\"testTimeoutSeconds\": 180", json);
        Assert.Contains($"\"ncfVersion\": \"{Constants.NcfVersion}\"", json);
        Assert.Contains("\"baseBranch\": \"main\"", json);
    }

    [Fact]
    public void ResolveConfig_ParsesGeneratedJson_Seamlessly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-bootstrap-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ConfigBootstrapper.BootstrapConfig(tempDir, CodeStackPresets.Python, "develop");

            var resolved = CodeStackPresets.ResolveConfig(tempDir);
            Assert.NotNull(resolved);
            Assert.Equal("python", resolved.CodeStack);
            Assert.Equal("python -m py_compile", resolved.BuildCommand);
            Assert.Equal("pytest", resolved.TestCommand);
            Assert.Equal("ruff format .", resolved.FormatCommand);
            Assert.Equal(180, resolved.BuildTimeoutSeconds);
            Assert.Equal(180, resolved.TestTimeoutSeconds);
            Assert.Equal(Constants.NcfVersion, resolved.NcfVersion);
            Assert.Equal("develop", resolved.BaseBranch);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BootstrapConfig_PreservesExistingCustomCommandsAndTimeouts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-bootstrap-preserve-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(tempDir, ".nightcodeforge");
        Directory.CreateDirectory(configDir);

        var existingConfig = @"{
  ""codeStack"": ""rust"",
  ""buildCommand"": ""cargo build --release"",
  ""testCommand"": ""cargo test --all"",
  ""buildTimeoutSeconds"": 360,
  ""testTimeoutSeconds"": 400
}";
        File.WriteAllText(Path.Combine(configDir, "config.json"), existingConfig);

        try
        {
            ConfigBootstrapper.BootstrapConfig(tempDir, CodeStackPresets.Rust, "master");

            var resolved = CodeStackPresets.ResolveConfig(tempDir);
            Assert.NotNull(resolved);
            Assert.Equal("cargo build --release", resolved.BuildCommand);
            Assert.Equal("cargo test --all", resolved.TestCommand);
            Assert.Equal(360, resolved.BuildTimeoutSeconds);
            Assert.Equal(400, resolved.TestTimeoutSeconds);
            Assert.Equal(Constants.NcfVersion, resolved.NcfVersion);
            Assert.Equal("master", resolved.BaseBranch);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenerateConfigJson_ThrowsInvalidOperationException_OnCorruptJson()
    {
        var corruptJson = "{ this is not valid json :";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigBootstrapper.GenerateConfigJson(CodeStackPresets.DotNet, "main", corruptJson));

        Assert.Contains("corrupted or invalid JSON", ex.Message);
    }
}
