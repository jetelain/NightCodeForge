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
    string? CoverageCommand = null,
    string? DisplayName = null,
    IReadOnlyList<string>? DefaultSkills = null)
{
    public IReadOnlyList<string> DefaultSkills { get; init; } = DefaultSkills ?? Array.Empty<string>();
}

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
    public string? NcfVersion { get; set; }
    public string? BaseBranch { get; set; }
    public string? ParseWarning { get; set; }
    public WorkspaceSkillsConfig? Skills { get; set; }
}

public class WorkspaceSkillsConfig
{
    /// <summary>Master switch for global skills injection. Default: true when omitted.</summary>
    public bool? Enabled { get; set; }

    /// <summary>List of skill identifiers explicitly disabled for this repository.</summary>
    public List<string>? DisabledSkills { get; set; }
}

/// <summary>
/// Constants and normalization helper for supported technology stacks.
/// </summary>
public static class CodeStack
{
    public const string DotNet = "dotnet";
    public const string Node = "node";
    public const string Python = "python";
    public const string Rust = "rust";
    public const string Go = "go";
    public const string Universal = "universal";

    public static readonly IReadOnlyList<string> All = new[]
    {
        DotNet,
        Node,
        Python,
        Rust,
        Go,
        Universal
    };

    /// <summary>
    /// Normalizes a tech stack identifier or common alias (e.g. "csharp", "ts", "py")
    /// to standard stack constants. Defaults to Universal if null or unrecognized.
    /// </summary>
    public static string NormalizeStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return Universal;
        return stack.Trim().ToLowerInvariant() switch
        {
            "dotnet" or "csharp" or "cs" or "fsharp" or "fs" or ".net" => DotNet,
            "node" or "npm" or "typescript" or "javascript" or "ts" or "js" => Node,
            "python" or "py" => Python,
            "rust" or "cargo" or "rs" => Rust,
            "go" or "golang" => Go,
            "universal" => Universal,
            var other => other
        };
    }
}

public static class CodeStackPresets
{
    public static readonly CodeStackPreset DotNet = new(
        Name: CodeStack.DotNet,
        BuildCommand: "dotnet build",
        TestCommand: "dotnet test --no-build",
        FormatCommand: "dotnet format",
        SastCommand: null,
        CoverageCommand: WorkflowDefaults.DefaultCoverageTestCommand,
        DisplayName: ".NET / C#",
        DefaultSkills: new[] { "csharp-nav", "codecondenser", "mdfetch" });

    public static readonly CodeStackPreset Node = new(
        Name: CodeStack.Node,
        BuildCommand: "npm run build",
        TestCommand: "npm test",
        FormatCommand: null,
        SastCommand: null,
        CoverageCommand: "npm test -- --coverage",
        DisplayName: "Node.js / TypeScript",
        DefaultSkills: new[] { "mdfetch" });

    public static readonly CodeStackPreset Python = new(
        Name: CodeStack.Python,
        BuildCommand: "python -m py_compile",
        TestCommand: "pytest",
        FormatCommand: "ruff format .",
        SastCommand: null,
        CoverageCommand: "pytest --cov",
        DisplayName: "Python",
        DefaultSkills: new[] { "mdfetch" });

    public static readonly CodeStackPreset Rust = new(
        Name: CodeStack.Rust,
        BuildCommand: "cargo build",
        TestCommand: "cargo test",
        FormatCommand: "cargo fmt",
        SastCommand: null,
        CoverageCommand: "cargo tarpaulin",
        DisplayName: "Rust",
        DefaultSkills: new[] { "mdfetch" });

    public static readonly CodeStackPreset Go = new(
        Name: CodeStack.Go,
        BuildCommand: "go build ./...",
        TestCommand: "go test ./...",
        FormatCommand: "gofmt -w .",
        SastCommand: null,
        CoverageCommand: "go test -coverprofile=coverage.out ./...",
        DisplayName: "Go",
        DefaultSkills: new[] { "mdfetch" });

    /// <summary>
    /// Attempts to resolve a CodeStackPreset by name or alias without silent fallback.
    /// </summary>
    public static bool TryResolve(string? stackName, out CodeStackPreset? preset)
    {
        var normalized = CodeStack.NormalizeStack(stackName);
        switch (normalized)
        {
            case CodeStack.DotNet:
                preset = DotNet;
                return true;
            case CodeStack.Node:
                preset = Node;
                return true;
            case CodeStack.Python:
                preset = Python;
                return true;
            case CodeStack.Rust:
                preset = Rust;
                return true;
            case CodeStack.Go:
                preset = Go;
                return true;
            default:
                preset = null;
                return false;
        }
    }

    /// <summary>
    /// Resolves active preset from a stack name or alias. Defaults to DotNet if unresolved.
    /// </summary>
    public static CodeStackPreset Resolve(string? stackName)
    {
        if (TryResolve(stackName, out var preset) && preset != null)
        {
            return preset;
        }

        return DotNet;
    }

    /// <summary>
    /// Deterministically detects the technology stack preset of a repository by inspecting
    /// marker files at the root level and subdirectories (up to 2 levels deep).
    /// Returns null if undetectable (fail-fast, no silent fallback).
    /// </summary>
    public static CodeStackPreset? Detect(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        // 1. Root-level checks (primary priority)
        var rootMatch = CheckDirectoryMarkers(directoryPath);
        if (rootMatch != null) return rootMatch;

        // 2. Subdirectories (up to 2 levels deep, ignoring hidden and build/artifact folders)
        try
        {
            var firstLevelDirs = EnumerateEligibleDirectories(directoryPath).ToList();

            // Check level 1
            foreach (var subDir in firstLevelDirs)
            {
                var match = CheckDirectoryMarkers(subDir);
                if (match != null) return match;
            }

            // Check level 2 (e.g. src/MyApp or backend/service)
            foreach (var subDir in firstLevelDirs)
            {
                foreach (var secondLevelDir in EnumerateEligibleDirectories(subDir))
                {
                    var match = CheckDirectoryMarkers(secondLevelDir);
                    if (match != null) return match;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory permission or path access errors treated as undetectable
        }

        return null;
    }

    private static IEnumerable<string> EnumerateEligibleDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Where(subDir =>
            {
                var name = Path.GetFileName(subDir);
                return !name.StartsWith('.') &&
                       !name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("target", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("dist", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("build", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("venv", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static CodeStackPreset? CheckDirectoryMarkers(string dir)
    {
        try
        {
            if (HasDotNetFiles(dir)) return DotNet;
            if (File.Exists(Path.Combine(dir, "Cargo.toml"))) return Rust;
            if (File.Exists(Path.Combine(dir, "go.mod"))) return Go;
            if (File.Exists(Path.Combine(dir, "package.json"))) return Node;
            if (File.Exists(Path.Combine(dir, "pyproject.toml")) ||
                File.Exists(Path.Combine(dir, "setup.py")) ||
                File.Exists(Path.Combine(dir, "requirements.txt"))) return Python;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore access errors
        }

        return null;
    }

    private static bool HasDotNetFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*.sln").Any() ||
                   Directory.EnumerateFiles(path, "*.slnx").Any() ||
                   Directory.EnumerateFiles(path, "*.csproj").Any() ||
                   Directory.EnumerateFiles(path, "*.fsproj").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
