using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Acp;

/// <summary>
/// Discovers and probes ACP harness providers (starting with Google Antigravity <c>agy acp</c>)
/// across WSL2 and host environments, caching detected version, account tier, authentication state, and models.
/// </summary>
public class AcpHarnessCatalogService : IAcpHarnessCatalogService
{
    public const string AntigravityDisplayName = "Google Antigravity CLI (agy acp)";
    public const string CustomDisplayName = "Custom ACP Executable";
    private const int DiscoveryCommandTimeoutSeconds = 10;
    private const int HandshakeProbeTimeoutSeconds = 10;
    private const string WslAgyBinaryPath = "~/.local/bin/agy";
    private const string WslOAuthTokenPath = "~/.gemini/antigravity-cli/antigravity-oauth-token";
    private const string GeminiApiKeyEnvVar = "GEMINI_API_KEY";

    public static readonly IReadOnlyList<string> DefaultThoughtLevels = ["low", "medium", "high"];

    public static readonly IReadOnlyList<AcpModelOption> DefaultAntigravityModels =
    [
        new("gemini-4-argon", "Gemini 4 Argon", DefaultThoughtLevels),
        new("gemini-3.8-flash", "Gemini 3.8 Flash", DefaultThoughtLevels),
        new("claude-opus-5-5", "Claude Opus 5.5", DefaultThoughtLevels),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", DefaultThoughtLevels),
        new("gpt-oss-120b-medium", "GPT-OSS 120B", ["medium"])
    ];

    private readonly IProcessRunner _processRunner;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly Func<string, bool> _fileExists;
    private readonly ILogger<AcpHarnessCatalogService> _logger;
    private readonly ConcurrentDictionary<string, AcpCatalogProviderStatus> _cache = new(StringComparer.OrdinalIgnoreCase);

    [ActivatorUtilitiesConstructor]
    public AcpHarnessCatalogService(
        IProcessRunner processRunner,
        IWslMetadataService wslMetadataService,
        ILogger<AcpHarnessCatalogService> logger)
        : this(processRunner, wslMetadataService, File.Exists, logger)
    {
    }

    public AcpHarnessCatalogService(
        IProcessRunner processRunner,
        IWslMetadataService wslMetadataService,
        Func<string, bool> fileExists,
        ILogger<AcpHarnessCatalogService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<AcpCatalogProviderStatus>> GetCatalogStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var antigravity = await ProbeProviderAsync(KnownAcpCatalogIds.Antigravity, forceRefresh: false, cancellationToken);
        return [antigravity];
    }

    public async Task<AcpCatalogProviderStatus> ProbeProviderAsync(
        string catalogId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);

        if (!forceRefresh && _cache.TryGetValue(catalogId, out var cached))
        {
            return cached;
        }

        AcpCatalogProviderStatus status;
        if (string.Equals(catalogId, KnownAcpCatalogIds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            status = await ProbeAntigravityAsync(cancellationToken);
        }
        else if (string.Equals(catalogId, KnownAcpCatalogIds.Custom, StringComparison.OrdinalIgnoreCase))
        {
            status = new AcpCatalogProviderStatus(
                KnownAcpCatalogIds.Custom,
                CustomDisplayName,
                IsInstalled: true,
                IsAuthenticated: true,
                DetectedExecutablePath: null,
                ExecutionTargetDescription: "Custom HarnessConfigurationJson",
                ServerVersion: null,
                AccountTierName: null,
                DiagnosticMessage: null,
                AvailableModels: []);
        }
        else
        {
            throw new ArgumentException($"Unknown ACP catalog provider '{catalogId}'.", nameof(catalogId));
        }

        _cache[catalogId] = status;
        return status;
    }

    private async Task<AcpCatalogProviderStatus> ProbeAntigravityAsync(CancellationToken cancellationToken)
    {
        var (detectedPath, isWsl, wslDistro) = await DiscoverAgyBinaryAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(detectedPath))
        {
            return new AcpCatalogProviderStatus(
                KnownAcpCatalogIds.Antigravity,
                AntigravityDisplayName,
                IsInstalled: false,
                IsAuthenticated: false,
                DetectedExecutablePath: null,
                ExecutionTargetDescription: null,
                ServerVersion: null,
                AccountTierName: null,
                DiagnosticMessage: "Antigravity CLI (agy) was not detected in WSL2 (~/.local/bin/agy) or on the host PATH.",
                AvailableModels: DefaultAntigravityModels);
        }

        var executionTarget = isWsl
            ? $"WSL2 ({(!string.IsNullOrWhiteSpace(wslDistro) ? wslDistro : "default")}) + Container Bind-Mount"
            : "Host OS";

        var loginHint = isWsl
            ? $"wsl -e {WslAgyBinaryPath} login"
            : "agy login";

        var hasToken = await CheckAuthenticationTokenAsync(isWsl, wslDistro, cancellationToken);

        var probe = await ProbeAcpHandshakeAsync(detectedPath, isWsl, wslDistro, cancellationToken);
        if (probe.ProbeSucceeded)
        {
            if (!probe.IsAuthenticated)
            {
                return new AcpCatalogProviderStatus(
                    KnownAcpCatalogIds.Antigravity,
                    AntigravityDisplayName,
                    IsInstalled: true,
                    IsAuthenticated: false,
                    DetectedExecutablePath: detectedPath,
                    ExecutionTargetDescription: executionTarget,
                    ServerVersion: probe.ServerVersion,
                    AccountTierName: probe.AccountTierName,
                    DiagnosticMessage: probe.DiagnosticMessage ?? $"Authentication required. Run '{loginHint}' to authenticate.",
                    AvailableModels: probe.Models.Count > 0 ? probe.Models : DefaultAntigravityModels);
            }

            return new AcpCatalogProviderStatus(
                KnownAcpCatalogIds.Antigravity,
                AntigravityDisplayName,
                IsInstalled: true,
                IsAuthenticated: true,
                DetectedExecutablePath: detectedPath,
                ExecutionTargetDescription: executionTarget,
                ServerVersion: probe.ServerVersion,
                AccountTierName: probe.AccountTierName,
                DiagnosticMessage: probe.DiagnosticMessage,
                AvailableModels: probe.Models.Count > 0 ? probe.Models : DefaultAntigravityModels);
        }

        // Fallback to token file / environment check if the live handshake probe could not start or timed out
        return new AcpCatalogProviderStatus(
            KnownAcpCatalogIds.Antigravity,
            AntigravityDisplayName,
            IsInstalled: true,
            IsAuthenticated: hasToken,
            DetectedExecutablePath: detectedPath,
            ExecutionTargetDescription: executionTarget,
            ServerVersion: probe.ServerVersion,
            AccountTierName: probe.AccountTierName,
            DiagnosticMessage: hasToken
                ? probe.DiagnosticMessage
                : $"Authentication required. Run '{loginHint}' to sign in.",
            AvailableModels: DefaultAntigravityModels);
    }

    private async Task<(string? Path, bool IsWsl, string? WslDistro)> DiscoverAgyBinaryAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var distro = _wslMetadataService.GetDefaultDistributionName();
            var wslPath = await ProbeWslForAgyAsync(distro, cancellationToken);
            if (!string.IsNullOrWhiteSpace(wslPath))
            {
                return (wslPath, true, distro);
            }

            var hostExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini",
                "bin",
                "agy.exe");
            if (_fileExists(hostExe))
            {
                return (hostExe, false, null);
            }

            var whereResult = await SafeRunAsync(new ProcessStartSpec
            {
                FileName = "where.exe",
                ArgumentList = ["agy"],
                TimeoutSeconds = DiscoveryCommandTimeoutSeconds
            }, cancellationToken);

            var whereFirstLine = FirstNonEmptyLine(whereResult?.StandardOutput);
            if (whereResult is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(whereFirstLine))
            {
                return (whereFirstLine, false, null);
            }

            return (null, false, null);
        }

        var posixResult = await SafeRunAsync(new ProcessStartSpec
        {
            FileName = "sh",
            ArgumentList = ["-c", $"if [ -f {WslAgyBinaryPath} ]; then echo {WslAgyBinaryPath}; elif command -v agy >/dev/null 2>&1; then command -v agy; fi"],
            TimeoutSeconds = DiscoveryCommandTimeoutSeconds
        }, cancellationToken);

        var posixPath = FirstNonEmptyLine(posixResult?.StandardOutput);
        return posixResult is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(posixPath)
            ? (posixPath, false, null)
            : (null, false, null);
    }

    private async Task<string?> ProbeWslForAgyAsync(string? distro, CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(distro))
        {
            args.AddRange(["-d", distro]);
        }
        args.AddRange(["--", "sh", "-c", $"if [ -f {WslAgyBinaryPath} ]; then echo {WslAgyBinaryPath}; elif command -v agy >/dev/null 2>&1; then command -v agy; fi"]);

        var result = await SafeRunAsync(new ProcessStartSpec
        {
            FileName = "wsl.exe",
            ArgumentList = args,
            TimeoutSeconds = DiscoveryCommandTimeoutSeconds
        }, cancellationToken);

        if (result is not { ExitCode: 0 })
        {
            return null;
        }

        return FirstNonEmptyLine(result.StandardOutput);
    }

    private async Task<bool> CheckAuthenticationTokenAsync(bool isWsl, string? wslDistro, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GeminiApiKeyEnvVar)))
        {
            return true;
        }

        if (isWsl)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(wslDistro))
            {
                args.AddRange(["-d", wslDistro]);
            }
            args.AddRange(["--", "sh", "-c", $"test -f {WslOAuthTokenPath}"]);

            var result = await SafeRunAsync(new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = args,
                TimeoutSeconds = DiscoveryCommandTimeoutSeconds
            }, cancellationToken);

            return result is { ExitCode: 0 };
        }

        if (OperatingSystem.IsWindows())
        {
            var hostTokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini",
                "antigravity-cli",
                "antigravity-oauth-token");
            return _fileExists(hostTokenPath);
        }

        var posixTokenCheck = await SafeRunAsync(new ProcessStartSpec
        {
            FileName = "sh",
            ArgumentList = ["-c", $"test -f {WslOAuthTokenPath}"],
            TimeoutSeconds = DiscoveryCommandTimeoutSeconds
        }, cancellationToken);

        return posixTokenCheck is { ExitCode: 0 };
    }

    private async Task<AcpHandshakeProbeResult> ProbeAcpHandshakeAsync(
        string detectedPath,
        bool isWsl,
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        ProcessStartSpec spec;
        if (isWsl)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(wslDistro))
            {
                args.AddRange(["-d", wslDistro]);
            }
            if (detectedPath.StartsWith("~/", StringComparison.Ordinal))
            {
                args.AddRange(["--", "sh", "-c", $"exec \"$HOME/{detectedPath[2..]}\" acp {AcpProcessLauncher.DangerouslySkipPermissionsFlag}"]);
            }
            else
            {
                args.AddRange(["--", detectedPath, "acp", AcpProcessLauncher.DangerouslySkipPermissionsFlag]);
            }
            spec = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = args,
                TimeoutSeconds = HandshakeProbeTimeoutSeconds
            };
        }
        else
        {
            spec = new ProcessStartSpec
            {
                FileName = ExternalProcessAgentHarnessBase.ExpandHostTildePath(detectedPath),
                ArgumentList = ["acp", AcpProcessLauncher.DangerouslySkipPermissionsFlag],
                TimeoutSeconds = HandshakeProbeTimeoutSeconds
            };
        }

        IProcessStreamSession? session = null;
        try
        {
            session = _processRunner.StartSession(spec);
            if (session == null)
            {
                return AcpHandshakeProbeResult.Failed();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(HandshakeProbeTimeoutSeconds));
            var ct = timeoutCts.Token;

            var initReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "init-1",
                method = "initialize",
                @params = new
                {
                    protocolVersion = 1,
                    clientCapabilities = new
                    {
                        fs = new { readTextFile = true, writeTextFile = true },
                        terminal = true
                    }
                }
            });
            await session.StandardInput.WriteLineAsync(initReq.AsMemory(), ct);

            string? serverVersion = null;
            string? tierName = null;
            JsonNode? initResponse = null;

            while (!ct.IsCancellationRequested)
            {
                var line = await session.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (TryParseJsonLine(line, out var node) && node?["id"]?.ToString() == "init-1")
                {
                    initResponse = node;
                    break;
                }
            }

            if (initResponse == null)
            {
                return AcpHandshakeProbeResult.Failed("ACP initialize handshake did not return a response.");
            }

            if (initResponse["error"] is JsonNode initErr)
            {
                return AcpHandshakeProbeResult.Failed(initErr["message"]?.ToString() ?? initErr.ToJsonString());
            }

            var initResult = initResponse["result"];
            serverVersion = initResult?["agentInfo"]?["version"]?.ToString();
            tierName = initResult?["_meta"]?["antigravity"]?["tier"]?["name"]?.ToString();

            var cwd = isWsl || !OperatingSystem.IsWindows() ? "/tmp" : Path.GetTempPath();
            var sessionReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "session-1",
                method = "session/new",
                @params = new
                {
                    cwd,
                    mcpServers = Array.Empty<object>()
                }
            });
            await session.StandardInput.WriteLineAsync(sessionReq.AsMemory(), ct);

            JsonNode? sessionResponse = null;
            while (!ct.IsCancellationRequested)
            {
                var line = await session.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (TryParseJsonLine(line, out var node) && node?["id"]?.ToString() == "session-1")
                {
                    sessionResponse = node;
                    break;
                }
            }

            if (sessionResponse == null)
            {
                return new AcpHandshakeProbeResult(
                    ProbeSucceeded: true,
                    IsAuthenticated: false,
                    ServerVersion: serverVersion,
                    AccountTierName: tierName,
                    DiagnosticMessage: "ACP session/new probe terminated without response.",
                    Models: DefaultAntigravityModels);
            }

            if (sessionResponse["error"] is JsonNode errNode)
            {
                var errMsg = errNode["message"]?.ToString() ?? errNode.ToJsonString();
                return new AcpHandshakeProbeResult(
                    ProbeSucceeded: true,
                    IsAuthenticated: false,
                    ServerVersion: serverVersion,
                    AccountTierName: tierName,
                    DiagnosticMessage: $"Authentication required: {errMsg}. Run '{(isWsl ? $"wsl -e {WslAgyBinaryPath} login" : "agy login")}'.",
                    Models: DefaultAntigravityModels);
            }

            var sessionResult = sessionResponse["result"];
            tierName ??= sessionResult?["_meta"]?["antigravity"]?["tier"]?["name"]?.ToString();
            var models = ParseModelsFromSessionResult(sessionResult);

            return new AcpHandshakeProbeResult(
                ProbeSucceeded: true,
                IsAuthenticated: true,
                ServerVersion: serverVersion,
                AccountTierName: tierName,
                DiagnosticMessage: null,
                Models: models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACP live probe failed for '{ExecutablePath}'", detectedPath);
            return AcpHandshakeProbeResult.Failed(ex.Message);
        }
        finally
        {
            if (session != null)
            {
                try
                {
                    session.Kill();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to kill temporary ACP probe process");
                }

                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose temporary ACP probe session");
                }
            }
        }
    }

    public static IReadOnlyList<AcpModelOption> ParseModelsFromSessionResult(JsonNode? sessionResult)
    {
        if (sessionResult == null)
        {
            return DefaultAntigravityModels;
        }

        var thoughtLevels = new List<string>();
        var modelOptions = new List<(string ModelId, string DisplayName)>();

        if (sessionResult["configOptions"] is JsonArray configOptions)
        {
            foreach (var cfg in configOptions)
            {
                var cfgId = cfg?["id"]?.ToString();
                if (string.Equals(cfgId, "thought_level", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractConfigOptionChoices(cfg, (val, _) =>
                    {
                        if (!string.IsNullOrWhiteSpace(val) && !thoughtLevels.Contains(val, StringComparer.OrdinalIgnoreCase))
                        {
                            thoughtLevels.Add(val);
                        }
                    });
                }
                else if (string.Equals(cfgId, "model", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractConfigOptionChoices(cfg, (val, name) =>
                    {
                        if (!string.IsNullOrWhiteSpace(val) && !modelOptions.Any(m => string.Equals(m.ModelId, val, StringComparison.OrdinalIgnoreCase)))
                        {
                            modelOptions.Add((val, string.IsNullOrWhiteSpace(name) ? FormatModelDisplayName(val) : name));
                        }
                    });
                }
            }
        }

        var availableThoughtMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var availableModelEntries = new List<(string ModelId, string DisplayName)>();

        if (sessionResult["models"]?["availableModels"] is JsonArray availableModels)
        {
            foreach (var item in availableModels)
            {
                var rawId = item is JsonValue
                    ? item.ToString()
                    : (item?["modelId"]?.ToString() ?? item?["id"]?.ToString());
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    continue;
                }

                var rawName = item is JsonObject ? item["name"]?.ToString() : null;
                availableModelEntries.Add((rawId, rawName ?? FormatModelDisplayName(rawId)));

                var (baseId, level) = SplitCombinedModelAndThoughtLevel(rawId);
                if (level != null)
                {
                    AddThoughtLevelMapping(availableThoughtMap, baseId, level);
                    AddThoughtLevelMapping(availableThoughtMap, rawId, level);
                }
            }
        }

        if (modelOptions.Count > 0)
        {
            var fallbackLevels = thoughtLevels.Count > 0 ? (IReadOnlyList<string>)thoughtLevels : DefaultThoughtLevels;
            return modelOptions
                .Select(m =>
                {
                    IReadOnlyList<string> levels;
                    if (availableThoughtMap.TryGetValue(m.ModelId, out var specific) && specific.Count > 0)
                    {
                        levels = specific;
                    }
                    else if (SplitCombinedModelAndThoughtLevel(m.ModelId).ThoughtLevel is { } suffixLevel)
                    {
                        levels = [suffixLevel];
                    }
                    else
                    {
                        levels = DefaultAntigravityModels.FirstOrDefault(d => string.Equals(d.ModelId, m.ModelId, StringComparison.OrdinalIgnoreCase))?.SupportedThoughtLevels ?? fallbackLevels;
                    }
                    return new AcpModelOption(m.ModelId, m.DisplayName, levels);
                })
                .ToList();
        }

        if (availableModelEntries.Count > 0)
        {
            var fallbackLevels = thoughtLevels.Count > 0 ? (IReadOnlyList<string>)thoughtLevels : DefaultThoughtLevels;
            return availableModelEntries
                .Select(m => new AcpModelOption(m.ModelId, m.DisplayName, fallbackLevels))
                .ToList();
        }

        return DefaultAntigravityModels;
    }

    private static void ExtractConfigOptionChoices(JsonNode? cfg, Action<string, string?> onChoice)
    {
        var choices = cfg?["options"] as JsonArray ?? cfg?["values"] as JsonArray;
        if (choices == null)
        {
            return;
        }

        foreach (var opt in choices)
        {
            if (opt is JsonValue)
            {
                var v = opt.ToString();
                onChoice(v, null);
            }
            else if (opt is JsonObject obj)
            {
                var val = obj["value"]?.ToString() ?? obj["id"]?.ToString();
                var name = obj["name"]?.ToString() ?? obj["label"]?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    onChoice(val, name);
                }
            }
        }
    }

    private static void AddThoughtLevelMapping(Dictionary<string, List<string>> map, string modelKey, string level)
    {
        if (!map.TryGetValue(modelKey, out var list))
        {
            list = [];
            map[modelKey] = list;
        }

        if (!list.Contains(level, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(level);
        }
    }

    private static (string BaseModelId, string? ThoughtLevel) SplitCombinedModelAndThoughtLevel(string modelId)
    {
        foreach (var suffix in new[] { "-low", "-medium", "-high" })
        {
            if (modelId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (modelId[..^suffix.Length], suffix[1..].ToLowerInvariant());
            }
        }

        return (modelId, null);
    }

    private static string FormatModelDisplayName(string modelId) => modelId switch
    {
        "gemini-4-argon" => "Gemini 4 Argon",
        "gemini-3.8-flash" => "Gemini 3.8 Flash",
        "claude-opus-5-5" => "Claude Opus 5.5",
        "claude-sonnet-5-5" => "Claude Sonnet 5.5",
        "gpt-oss-120b-medium" => "GPT-OSS 120B",
        _ => modelId
    };

    private async Task<ProcessExecutionResult?> SafeRunAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            return await _processRunner.RunAsync(spec, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACP discovery command '{FileName}' failed", spec.FileName);
            return null;
        }
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static bool TryParseJsonLine(string line, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(line);
            return node != null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private sealed record AcpHandshakeProbeResult(
        bool ProbeSucceeded,
        bool IsAuthenticated,
        string? ServerVersion,
        string? AccountTierName,
        string? DiagnosticMessage,
        IReadOnlyList<AcpModelOption> Models)
    {
        public static AcpHandshakeProbeResult Failed(string? diagnosticMessage = null)
            => new(false, false, null, null, diagnosticMessage, DefaultAntigravityModels);
    }
}
