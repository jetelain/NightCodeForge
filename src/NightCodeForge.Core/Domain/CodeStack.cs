using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Predefined command and toolset presets for standard technology stacks.
/// </summary>
public record CodeStackPreset(
    string Name,
    string BuildCommand,
    string TestCommand,
    string? FormatCommand = null,
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
    public string? FormatCommand { get; set; }
    public string? SastCommand { get; set; }
    public string? CoverageCommand { get; set; }
    public int? BuildTimeoutSeconds { get; set; }
    public int? TestTimeoutSeconds { get; set; }
    public bool? AllowNoTests { get; set; }
    public string? ParseWarning { get; set; }
}

public static class CodeStackPresets
{
    public static readonly CodeStackPreset DotNet = new(
        Name: "dotnet",
        BuildCommand: "dotnet build",
        TestCommand: "dotnet test --no-build",
        FormatCommand: "dotnet format",
        SastCommand: null,
        CoverageCommand: WorkflowDefaults.DefaultCoverageTestCommand);

    public static readonly CodeStackPreset Node = new(
        Name: "node",
        BuildCommand: "npm run build",
        TestCommand: "npm test",
        FormatCommand: null,
        SastCommand: null,
        CoverageCommand: "npm test -- --coverage");

    public static readonly CodeStackPreset Python = new(
        Name: "python",
        BuildCommand: "python -m py_compile",
        TestCommand: "pytest",
        FormatCommand: "ruff format .",
        SastCommand: null,
        CoverageCommand: "pytest --cov");

    public static readonly CodeStackPreset Rust = new(
        Name: "rust",
        BuildCommand: "cargo build",
        TestCommand: "cargo test",
        FormatCommand: "cargo fmt",
        SastCommand: null,
        CoverageCommand: "cargo tarpaulin");

    public static readonly CodeStackPreset Go = new(
        Name: "go",
        BuildCommand: "go build ./...",
        TestCommand: "go test ./...",
        FormatCommand: "gofmt -w .",
        SastCommand: null,
        CoverageCommand: "go test -coverprofile=coverage.out ./...");

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
    public static WorkspaceProjectConfig ResolveConfig(string? workspacePath, PipelineDataBag? dataBag = null, ILogger? logger = null)
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
                    catch (Exception ex)
                    {
                        var warningMsg = $"Failed to read or parse workspace configuration '{path}': {ex.Message}";
                        config.ParseWarning = warningMsg;
                        logger?.LogWarning(ex, "Failed to read or parse workspace configuration '{Path}'", path);
                        dataBag?.Set("config.parse_warning", warningMsg);
                    }
                }
            }
        }

        // 2. Preset resolution
        var preset = Resolve(config.CodeStack);

        config.BuildCommand ??= preset.BuildCommand;
        config.TestCommand ??= preset.TestCommand;
        config.FormatCommand ??= preset.FormatCommand;
        config.SastCommand ??= preset.SastCommand;
        config.CoverageCommand ??= preset.CoverageCommand;

        // 3. Pipeline DataBag dynamic overrides
        if (dataBag != null)
        {
            if (dataBag.TryGet<string>(PipelineDataBagKeys.BuildCommand, out var bCmd) || dataBag.TryGet<string>("BuildCommand", out bCmd))
                config.BuildCommand = bCmd;
            if (dataBag.TryGet<string>(PipelineDataBagKeys.TestCommand, out var tCmd) || dataBag.TryGet<string>("TestCommand", out tCmd))
                config.TestCommand = tCmd;
            if (dataBag.TryGet<string>(PipelineDataBagKeys.FormatCommand, out var fCmd) || dataBag.TryGet<string>("FormatCommand", out fCmd))
                config.FormatCommand = fCmd;
            if (dataBag.TryGet<string>(PipelineDataBagKeys.SastCommand, out var sCmd) || dataBag.TryGet<string>("SastCommand", out sCmd))
                config.SastCommand = sCmd;
            if (dataBag.TryGet<string>(PipelineDataBagKeys.CoverageCommand, out var cCmd) || dataBag.TryGet<string>("CoverageCommand", out cCmd))
                config.CoverageCommand = cCmd;
            if ((dataBag.TryGet<int>(PipelineDataBagKeys.BuildTimeoutSeconds, out var bSec) || dataBag.TryGet<int>("BuildTimeoutSeconds", out bSec)) && bSec > 0)
                config.BuildTimeoutSeconds = bSec;
            if ((dataBag.TryGet<int>(PipelineDataBagKeys.TestTimeoutSeconds, out var tSec) || dataBag.TryGet<int>("TestTimeoutSeconds", out tSec)) && tSec > 0)
                config.TestTimeoutSeconds = tSec;
            if (dataBag.TryGet<bool>(PipelineDataBagKeys.AllowNoTests, out var aTests) || dataBag.TryGet<bool>("AllowNoTests", out aTests))
                config.AllowNoTests = aTests;
        }

        config.BuildTimeoutSeconds ??= WorkflowDefaults.DefaultBuildTimeoutSeconds;
        config.TestTimeoutSeconds ??= WorkflowDefaults.DefaultTestTimeoutSeconds;

        return config;
    }
}
