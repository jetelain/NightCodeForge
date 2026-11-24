using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Temporarily relaxes .NET <c>global.json</c> SDK <c>rollForward</c> policies to <c>latestMajor</c>
/// during benchmark agent execution and test verification, then restores the exact original file bytes
/// before patch extraction or subsequent git operations.
/// </summary>
public sealed class DotNetGlobalJsonResilienceScope : IDisposable
{
    private const string GlobalJsonFileName = "global.json";
    private const string SdkPropertyName = "sdk";
    private const string VersionPropertyName = "version";
    private const string RollForwardPropertyName = "rollForward";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ParseDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "node_modules",
        ".venv",
        "__pycache__",
        BenchmarkPaths.BenchmarkResultDirRelative
    };

    private sealed record ModifiedGlobalJsonBackup(string FilePath, byte[] OriginalBytes, byte[] MitigatedBytes);

    private readonly IReadOnlyList<ModifiedGlobalJsonBackup> _backups;
    private readonly ILogger _logger;
    private bool _disposed;

    private DotNetGlobalJsonResilienceScope(IReadOnlyList<ModifiedGlobalJsonBackup> backups, ILogger logger)
    {
        _backups = backups;
        _logger = logger;
    }

    public int ModifiedFileCount => _backups.Count;

    public static DotNetGlobalJsonResilienceScope Apply(string workspacePath, string language, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.Equals(BenchmarkLanguages.Normalize(language), BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(workspacePath) ||
            !Directory.Exists(workspacePath))
        {
            return new DotNetGlobalJsonResilienceScope(Array.Empty<ModifiedGlobalJsonBackup>(), logger);
        }

        var backups = new List<ModifiedGlobalJsonBackup>();
        foreach (var filePath in EnumerateGlobalJsonFiles(workspacePath, logger))
        {
            var backup = TryRelaxGlobalJsonFile(filePath, logger);
            if (backup != null)
            {
                backups.Add(backup);
            }
        }

        return new DotNetGlobalJsonResilienceScope(backups, logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var backup in _backups)
        {
            try
            {
                if (!File.Exists(backup.FilePath))
                {
                    continue;
                }

                var currentBytes = File.ReadAllBytes(backup.FilePath);
                if (currentBytes.AsSpan().SequenceEqual(backup.MitigatedBytes))
                {
                    File.WriteAllBytes(backup.FilePath, backup.OriginalBytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to restore original global.json at '{FilePath}'.", backup.FilePath);
            }
        }
    }

    private static ModifiedGlobalJsonBackup? TryRelaxGlobalJsonFile(string filePath, ILogger logger)
    {
        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Unable to read global.json at '{FilePath}'.", filePath);
            return null;
        }

        JsonObject rootObj;
        JsonObject sdkObj;
        string? existingRollForwardKey;
        string serialized;
        try
        {
            var utf8Span = StripUtf8Bom(originalBytes);
            var rootNode = JsonNode.Parse(utf8Span, nodeOptions: null, documentOptions: ParseDocumentOptions);

            if (rootNode is not JsonObject parsedRoot ||
                !TryGetPropertyIgnoreCase(parsedRoot, SdkPropertyName, out _, out var sdkNode) ||
                sdkNode is not JsonObject parsedSdk)
            {
                return null;
            }

            rootObj = parsedRoot;
            sdkObj = parsedSdk;

            if (!TryGetPropertyIgnoreCase(sdkObj, VersionPropertyName, out _, out var versionNode) ||
                versionNode is not JsonValue versionVal ||
                !versionVal.TryGetValue<string>(out var versionStr) ||
                string.IsNullOrWhiteSpace(versionStr))
            {
                return null;
            }

            if (TryGetPropertyIgnoreCase(sdkObj, RollForwardPropertyName, out existingRollForwardKey, out var rollForwardNode) &&
                rollForwardNode is JsonValue rollForwardVal &&
                rollForwardVal.TryGetValue<string>(out var rollForwardStr) &&
                string.Equals(rollForwardStr.Trim(), BenchmarkDefaults.DotNetSdkRollForwardLatestMajor, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var targetKey = existingRollForwardKey ?? RollForwardPropertyName;
            sdkObj[targetKey] = BenchmarkDefaults.DotNetSdkRollForwardLatestMajor;
            serialized = rootObj.ToJsonString(WriteJsonOptions) + "\n";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogDebug(ex, "Skipping invalid JSON in global.json at '{FilePath}'.", filePath);
            return null;
        }
        var mitigatedBytes = Utf8NoBom.GetBytes(serialized);

        try
        {
            File.WriteAllBytes(filePath, mitigatedBytes);
            logger.LogDebug("Relaxed global.json SDK rollForward to '{RollForward}' at '{FilePath}'.",
                BenchmarkDefaults.DotNetSdkRollForwardLatestMajor, filePath);
            return new ModifiedGlobalJsonBackup(filePath, originalBytes, mitigatedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to update global.json at '{FilePath}'.", filePath);
            return null;
        }
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsSpan(3)
            : bytes.AsSpan();

    private static bool TryGetPropertyIgnoreCase(
        JsonObject obj,
        string propertyName,
        out string? actualKey,
        out JsonNode? value)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = kvp.Key;
                value = kvp.Value;
                return true;
            }
        }

        actualKey = null;
        value = null;
        return false;
    }

    private static IEnumerable<string> EnumerateGlobalJsonFiles(string workspacePath, ILogger logger)
    {
        var pending = new Stack<string>();
        pending.Push(workspacePath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping inaccessible directory '{Directory}' while scanning for global.json.", current);
                continue;
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), GlobalJsonFileName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping inaccessible subdirectories under '{Directory}' while scanning for global.json.", current);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);
                if (SkippedDirectories.Contains(dirName))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Skipping inaccessible directory '{Directory}' while scanning for global.json.", subDir);
                    continue;
                }

                pending.Push(subDir);
            }
        }
    }
}
