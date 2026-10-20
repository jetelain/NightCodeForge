namespace NightCodeForge.Cli.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Cli.Common;
using NightCodeForge.Core.Domain;

public static class ConfigBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GenerateConfigJson(CodeStackPreset preset, string baseBranch, string? existingJson = null)
    {
        WorkspaceProjectConfig config;

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                config = JsonSerializer.Deserialize<WorkspaceProjectConfig>(existingJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? new WorkspaceProjectConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Existing .nightcodeforge/config.json is corrupted or invalid JSON: {ex.Message}", ex);
            }
        }
        else
        {
            config = new WorkspaceProjectConfig();
        }

        config.CodeStack = preset.Name;
        config.BuildCommand ??= preset.BuildCommand;
        config.TestCommand ??= preset.TestCommand;
        config.FormatCommand ??= preset.FormatCommand;
        config.BuildTimeoutSeconds ??= Constants.DefaultCliCommandTimeoutSeconds;
        config.TestTimeoutSeconds ??= Constants.DefaultCliCommandTimeoutSeconds;
        config.NcfVersion = Constants.NcfVersion;
        config.BaseBranch = baseBranch;

        return JsonSerializer.Serialize(config, JsonOptions) + "\n";
    }

    public static void BootstrapConfig(string workspaceRoot, CodeStackPreset preset, string baseBranch, bool dryRun = false, Action<string>? log = null)
    {
        var configDir = Path.Combine(workspaceRoot, ".nightcodeforge");
        var configPath = Path.Combine(configDir, "config.json");

        string? existingContent = null;
        if (File.Exists(configPath))
        {
            existingContent = File.ReadAllText(configPath);
        }

        var json = GenerateConfigJson(preset, baseBranch, existingContent);

        if (dryRun)
        {
            log?.Invoke($"[dry-run] Would write {configPath}:\n{json}");
        }
        else
        {
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            File.WriteAllText(configPath, json);
            log?.Invoke($"[ncf] Bootstrapped configuration at {configPath}");
        }
    }
}
