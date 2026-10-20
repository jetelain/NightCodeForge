namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Core.Domain;

public class CodeStackDetectionTests
{
    [Fact]
    public void Detect_DotNet_WhenCsprojExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "MyApp.csproj"), "<Project />");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("dotnet", detected.Name);
            Assert.Equal("dotnet build", detected.BuildCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_DotNet_WhenSlnExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "App.sln"), "");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("dotnet", detected.Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_Node_WhenPackageJsonExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "package.json"), "{}");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("node", detected.Name);
            Assert.Equal("npm run build", detected.BuildCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_Python_WhenPyprojectTomlExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "pyproject.toml"), "");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("python", detected.Name);
            Assert.Equal("pytest", detected.TestCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_Rust_WhenCargoTomlExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Cargo.toml"), "");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("rust", detected.Name);
            Assert.Equal("cargo build", detected.BuildCommand);
            Assert.Equal("cargo fmt", detected.FormatCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_Go_WhenGoModExistsInRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "go.mod"), "module mygo");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("go", detected.Name);
            Assert.Equal("go build ./...", detected.BuildCommand);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_SubdirectoryDiscovery_FindsStackInSubfolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-subdir-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "src", "MyRustApp");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Cargo.toml"), "");

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.NotNull(detected);
            Assert.Equal("rust", detected.Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Detect_EmptyDirectory_ReturnsNull_FailFast()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.Null(detected);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("dotnet", "dotnet")]
    [InlineData("csharp", "dotnet")]
    [InlineData("cs", "dotnet")]
    [InlineData("node", "node")]
    [InlineData("npm", "node")]
    [InlineData("typescript", "node")]
    [InlineData("python", "python")]
    [InlineData("py", "python")]
    [InlineData("rust", "rust")]
    [InlineData("cargo", "rust")]
    [InlineData("go", "go")]
    [InlineData("golang", "go")]
    public void TryResolve_KnownStacks_ReturnsTrueAndPreset(string input, string expectedName)
    {
        var success = CodeStackPresets.TryResolve(input, out var preset);
        Assert.True(success);
        Assert.NotNull(preset);
        Assert.Equal(expectedName, preset.Name);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cobol")]
    [InlineData("fortran")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_UnknownStacks_ReturnsFalseAndNull(string? input)
    {
        var success = CodeStackPresets.TryResolve(input, out var preset);
        Assert.False(success);
        Assert.Null(preset);
    }

    [Fact]
    public void Presets_HaveAccurateDisplayNames()
    {
        Assert.Equal(".NET / C#", CodeStackPresets.DotNet.DisplayName);
        Assert.Equal("Node.js / TypeScript", CodeStackPresets.Node.DisplayName);
        Assert.Equal("Python", CodeStackPresets.Python.DisplayName);
        Assert.Equal("Rust", CodeStackPresets.Rust.DisplayName);
        Assert.Equal("Go", CodeStackPresets.Go.DisplayName);
    }

    [Fact]
    public void Detect_IgnoresVenvAndPycacheSubdirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-venv-" + Guid.NewGuid().ToString("N"));
        var venvDir = Path.Combine(tempDir, "venv", "site-packages");
        Directory.CreateDirectory(venvDir);
        File.WriteAllText(Path.Combine(venvDir, "pyproject.toml"), "");

        try
        {
            // Empty project with only a virtualenv should not be detected as python
            var detected = CodeStackPresets.Detect(tempDir);
            Assert.Null(detected);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
