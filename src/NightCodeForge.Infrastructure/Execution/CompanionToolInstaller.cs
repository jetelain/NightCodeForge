using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Probes and auto-installs companion CLI tools (csnav, mdfetch, codecondenser) on the host or inside WSL2.
/// Guarantees that tool binaries exist before Docker creates volume mounts, avoiding empty root-owned mounts.
/// </summary>
public class CompanionToolInstaller : ICompanionToolInstaller
{
    private static readonly IReadOnlyList<CompanionToolSpec> DefaultKnownTools = new List<CompanionToolSpec>
    {
        new(
            ToolCommand: "csnav",
            PackageId: "Pmad.CSharpNav",
            ApplicableStacks: new[] { CodeStack.DotNet }),
        new(
            ToolCommand: "mdfetch",
            PackageId: "Pmad.MarkdownFetch",
            ApplicableStacks: new[] { CodeStack.Universal }),
        new(
            ToolCommand: "codecondenser",
            PackageId: "Pmad.CodeCondenser",
            ApplicableStacks: new[] { CodeStack.DotNet })
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<CompanionToolInstaller> _logger;
    private readonly ConcurrentDictionary<string, bool> _ensuredEnvironments = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CompanionToolSpec> KnownTools => DefaultKnownTools;

    public void ClearCache()
    {
        _ensuredEnvironments.Clear();
    }

    public CompanionToolInstaller(
        IProcessRunner processRunner,
        ILogger<CompanionToolInstaller> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsToolInstalledAsync(
        string command,
        bool useWsl = false,
        string? wslDistribution = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // Check via 'dotnet tool list -g'
        ProcessStartSpec spec;
        if (useWsl)
        {
            var args = !string.IsNullOrWhiteSpace(wslDistribution)
                ? $"-d {wslDistribution} dotnet tool list -g"
                : "dotnet tool list -g";

            spec = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                Arguments = args,
                TimeoutSeconds = 15
            };
        }
        else
        {
            spec = new ProcessStartSpec
            {
                FileName = "dotnet",
                Arguments = "tool list -g",
                TimeoutSeconds = 15
            };
        }

        try
        {
            var result = await _processRunner.RunAsync(spec, ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                // dotnet tool list -g output columns: Package Id, Version, Commands
                var lines = result.StandardOutput.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("Package Id", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("---"))
                    {
                        continue;
                    }

                    // Look for command in line
                    var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Any(t => string.Equals(t, command, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    // Check if package id matches tool
                    var known = DefaultKnownTools.FirstOrDefault(k => string.Equals(k.ToolCommand, command, StringComparison.OrdinalIgnoreCase));
                    if (known != null && tokens.Any(t => string.Equals(t, known.PackageId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe tool '{Command}' via 'dotnet tool list -g' (useWsl={UseWsl})", command, useWsl);
        }

        return false;
    }

    public async Task EnsureToolsInstalledAsync(
        string stack,
        bool useWsl = false,
        string? wslDistribution = null,
        CancellationToken ct = default)
    {
        var normalizedStack = CodeStack.NormalizeStack(stack);
        var cacheKey = $"{normalizedStack}:{useWsl}:{wslDistribution ?? "default"}";
        if (_ensuredEnvironments.ContainsKey(cacheKey))
        {
            return;
        }

        // Guarantee tools directory exists under user ownership prior to container volume mounting
        if (useWsl)
        {
            var mkdirArgs = !string.IsNullOrWhiteSpace(wslDistribution)
                ? $"-d {wslDistribution} -- bash -c \"mkdir -p ~/.dotnet/tools\""
                : "-- bash -c \"mkdir -p ~/.dotnet/tools\"";

            try
            {
                await _processRunner.RunAsync(new ProcessStartSpec
                {
                    FileName = "wsl.exe",
                    Arguments = mkdirArgs,
                    TimeoutSeconds = 10
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ensure ~/.dotnet/tools directory exists in WSL (distro={Distro})", wslDistribution ?? "default");
            }
        }
        else
        {
            var hostToolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            if (!Directory.Exists(hostToolsDir))
            {
                try
                {
                    Directory.CreateDirectory(hostToolsDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create host dotnet tools directory at {Path}", hostToolsDir);
                }
            }
        }

        var applicableTools = DefaultKnownTools
            .Where(t => t.ApplicableStacks.Any(s =>
                CodeStack.NormalizeStack(s) == CodeStack.Universal ||
                CodeStack.NormalizeStack(s) == normalizedStack))
            .ToList();

        foreach (var tool in applicableTools)
        {
            ct.ThrowIfCancellationRequested();

            var isInstalled = await IsToolInstalledAsync(tool.ToolCommand, useWsl, wslDistribution, ct);
            if (isInstalled)
            {
                _logger.LogDebug("Companion tool '{ToolCommand}' is already installed (useWsl={UseWsl}).", tool.ToolCommand, useWsl);
                continue;
            }

            _logger.LogInformation("Auto-installing companion tool '{ToolCommand}' ({PackageId}) [useWsl={UseWsl}, distro={Distro}]...",
                tool.ToolCommand, tool.PackageId, useWsl, wslDistribution ?? "default");

            ProcessStartSpec installSpec;
            if (useWsl)
            {
                var args = !string.IsNullOrWhiteSpace(wslDistribution)
                    ? $"-d {wslDistribution} dotnet tool install -g {tool.PackageId}"
                    : $"dotnet tool install -g {tool.PackageId}";

                installSpec = new ProcessStartSpec
                {
                    FileName = "wsl.exe",
                    Arguments = args,
                    TimeoutSeconds = 120
                };
            }
            else
            {
                installSpec = new ProcessStartSpec
                {
                    FileName = "dotnet",
                    Arguments = $"tool install -g {tool.PackageId}",
                    TimeoutSeconds = 120
                };
            }

            try
            {
                var installResult = await _processRunner.RunAsync(installSpec, ct);
                if (installResult.ExitCode != 0)
                {
                    // "already installed" message in stdout or stderr is acceptable
                    var combinedOutput = $"{installResult.StandardOutput}\n{installResult.StandardError}";
                    if (!combinedOutput.Contains("is already installed", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Failed to install companion tool '{ToolCommand}': ExitCode={ExitCode}, Error={Error}",
                            tool.ToolCommand, installResult.ExitCode, installResult.StandardError);
                    }
                    else
                    {
                        _logger.LogInformation("Companion tool '{ToolCommand}' was already installed.", tool.ToolCommand);
                    }
                }
                else
                {
                    _logger.LogInformation("Successfully installed companion tool '{ToolCommand}'.", tool.ToolCommand);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while installing companion tool '{ToolCommand}'", tool.ToolCommand);
            }
        }

        _ensuredEnvironments[cacheKey] = true;
    }
}
