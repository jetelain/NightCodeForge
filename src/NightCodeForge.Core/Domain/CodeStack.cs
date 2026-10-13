using System.Text.Json;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Predefined command and toolset presets for standard technology stacks.
/// </summary>
public record CodeStackPreset(
    string Name,
    string BuildCommand,
    string TestCommand,
    string? SastCommand = null,
    string? CoverageCommand = null);

/// <summary>
/// Project-level configuration discovered from .nightcodeforge/config.json or nightcodeforge.json.
/// </summary>
public class WorkspaceProjectConfig
{
    public string? CodeStack { get; set; }
    public string? BuildCommand { get; set; }
    public string? TestCommand { get; set; }
    public string? SastCommand { get; set; }
    public string? CoverageCommand { get; set; }
}

public static class CodeStackPresets
{
    public static readonly CodeStackPreset DotNet = new(
        Name: "dotnet",
        BuildCommand: "dotnet build",
        TestCommand: "dotnet test --no-build",
        SastCommand: "dotnet format --verify-no-changes");

    public static readonly CodeStackPreset Node = new(
        Name: "node",
        BuildCommand: "npm run build",
        TestCommand: "npm test",
        SastCommand: "npm run lint");

    public static readonly CodeStackPreset Python = new(
        Name: "python",
        BuildCommand: "python -m py_compile",
        TestCommand: "pytest",
        SastCommand: "ruff check .");

    public static readonly CodeStackPreset Rust = new(
        Name: "rust",
        BuildCommand: "cargo build",
        TestCommand: "cargo test",
        SastCommand: "cargo clippy -- -D warnings");

    public static readonly CodeStackPreset Go = new(
        Name: "go",
        BuildCommand: "go build ./...",
        TestCommand: "go test ./...",
        SastCommand: "golangci-lint run");

    public static CodeStackPreset Resolve(string? stackName)
    {
        return (stackName?.Trim().ToLowerInvariant()) switch
        {
            "node" or "npm" or "javascript" or "typescript" or "ts" or "js" => Node,
            "python" or "py" => Python,
            "rust" or "cargo" or "rs" => Rust,
            "go" or "golang" => Go,
            _ => DotNet
        };
    }

    /// <summary>
    /// Resolves active build, test, and SAST commands considering PipelineDataBag overrides,
    /// workspace configuration files (.nightcodeforge/config.json, nightcodeforge.json), and defaults.
    /// </summary>
    public static WorkspaceProjectConfig ResolveConfig(string? workspacePath, PipelineDataBag? dataBag = null)
    {
        var config = new WorkspaceProjectConfig();

        // 1. Try reading workspace config file
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            var candidatePaths = new[]
            {
                Path.Combine(workspacePath, ".nightcodeforge", "config.json"),
                Path.Combine(workspacePath, "nightcodeforge.json")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var parsed = JsonSerializer.Deserialize<WorkspaceProjectConfig>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (parsed != null)
                        {
                            config = parsed;
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore invalid JSON file
                    }
                }
            }
        }

        // 2. Preset resolution
        var preset = Resolve(config.CodeStack);

        config.BuildCommand ??= preset.BuildCommand;
        config.TestCommand ??= preset.TestCommand;
        config.SastCommand ??= preset.SastCommand;
        config.CoverageCommand ??= preset.CoverageCommand;

        // 3. Pipeline DataBag dynamic overrides
        if (dataBag != null)
        {
            if (dataBag.TryGet<string>("BuildCommand", out var bCmd) && !string.IsNullOrWhiteSpace(bCmd))
                config.BuildCommand = bCmd;
            if (dataBag.TryGet<string>("TestCommand", out var tCmd) && !string.IsNullOrWhiteSpace(tCmd))
                config.TestCommand = tCmd;
            if (dataBag.TryGet<string>("SastCommand", out var sCmd) && !string.IsNullOrWhiteSpace(sCmd))
                config.SastCommand = sCmd;
            if (dataBag.TryGet<string>("CoverageCommand", out var cCmd) && !string.IsNullOrWhiteSpace(cCmd))
                config.CoverageCommand = cCmd;
        }

        return config;
    }
}
