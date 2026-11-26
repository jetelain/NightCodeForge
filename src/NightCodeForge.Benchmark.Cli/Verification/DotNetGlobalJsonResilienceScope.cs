using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Temporarily relaxes .NET <c>global.json</c> SDK <c>rollForward</c> policies to <c>latestMajor</c>
/// during benchmark agent execution and test verification while keeping tracked files hidden from
/// <c>git status</c> / <c>git diff</c>, then restores exact original bytes and git index state on disposal.
/// </summary>
public sealed class DotNetGlobalJsonResilienceScope : IDisposable
{
    private const string GlobalJsonFileName = "global.json";
    private const string GitDirectoryName = ".git";
    private const string GitExecutable = "git";
    private const string WslExecutable = "wsl.exe";
    private const string GitUpdateIndexCommand = "update-index";
    private const string GitAssumeUnchangedFlag = "--assume-unchanged";
    private const string GitNoAssumeUnchangedFlag = "--no-assume-unchanged";
    private const string GitPathspecSeparator = "--";
    private const int GitUpdateIndexTimeoutMs = 10_000;
    private const int GitKillWaitTimeoutMs = 1_000;
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

    private sealed record ModifiedGlobalJsonBackup(
        string FilePath,
        string RelativePosixPath,
        byte[] OriginalBytes,
        byte[] MitigatedBytes,
        bool MarkedAssumeUnchanged);

    private readonly string _workspacePath;
    private readonly IReadOnlyList<ModifiedGlobalJsonBackup> _backups;
    private readonly ILogger _logger;
    private bool _disposed;

    private DotNetGlobalJsonResilienceScope(
        string workspacePath,
        IReadOnlyList<ModifiedGlobalJsonBackup> backups,
        ILogger logger)
    {
        _workspacePath = workspacePath;
        _backups = backups;
        _logger = logger;
    }

    public int ModifiedFileCount => _backups.Count;

    public static DotNetGlobalJsonResilienceScope Apply(string workspacePath, string language, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!BenchmarkLanguages.IsCSharp(language) ||
            string.IsNullOrWhiteSpace(workspacePath) ||
            !Directory.Exists(workspacePath))
        {
            return new DotNetGlobalJsonResilienceScope(workspacePath ?? string.Empty, Array.Empty<ModifiedGlobalJsonBackup>(), logger);
        }

        var isGitRepo = IsGitRepository(workspacePath);
        var backups = new List<ModifiedGlobalJsonBackup>();
        foreach (var filePath in EnumerateGlobalJsonFiles(workspacePath, logger))
        {
            var backup = TryRelaxGlobalJsonFile(workspacePath, filePath, isGitRepo, logger);
            if (backup != null)
            {
                backups.Add(backup);
            }
        }

        return new DotNetGlobalJsonResilienceScope(workspacePath, backups, logger);
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
                if (File.Exists(backup.FilePath))
                {
                    var currentBytes = File.ReadAllBytes(backup.FilePath);
                    if (currentBytes.AsSpan().SequenceEqual(backup.MitigatedBytes))
                    {
                        File.WriteAllBytes(backup.FilePath, backup.OriginalBytes);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to restore original global.json at '{FilePath}'.", backup.FilePath);
            }

            if (backup.MarkedAssumeUnchanged &&
                !TryRunGitUpdateIndex(_workspacePath, GitNoAssumeUnchangedFlag, backup.RelativePosixPath, _logger))
            {
                _logger.LogWarning(
                    "Failed to clear git assume-unchanged flag on global.json at '{RelativePath}'.",
                    backup.RelativePosixPath);
            }
        }
    }

    private static ModifiedGlobalJsonBackup? TryRelaxGlobalJsonFile(
        string workspacePath,
        string filePath,
        bool isGitRepo,
        ILogger logger)
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
            var relativePosixPath = Path.GetRelativePath(workspacePath, filePath).Replace('\\', '/');
            var markedAssumeUnchanged = isGitRepo &&
                TryRunGitUpdateIndex(workspacePath, GitAssumeUnchangedFlag, relativePosixPath, logger);

            logger.LogDebug("Relaxed global.json SDK rollForward to '{RollForward}' at '{FilePath}'.",
                BenchmarkDefaults.DotNetSdkRollForwardLatestMajor, filePath);
            return new ModifiedGlobalJsonBackup(
                filePath,
                relativePosixPath,
                originalBytes,
                mitigatedBytes,
                markedAssumeUnchanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to update global.json at '{FilePath}'.", filePath);
            return null;
        }
    }

    private static bool IsGitRepository(string workspacePath)
    {
        var dotGitPath = Path.Combine(workspacePath, GitDirectoryName);
        return Directory.Exists(dotGitPath) || File.Exists(dotGitPath);
    }

    private static bool TryRunGitUpdateIndex(
        string workspacePath,
        string flag,
        string relativePosixPath,
        ILogger logger)
    {
        try
        {
            using var process = new Process();
            if (OperatingSystem.IsWindows() &&
                WslPathConverter.TryExtractWslUnc(workspacePath, out var distro, out var linuxDir))
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = WslExecutable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(distro))
                {
                    process.StartInfo.ArgumentList.Add("-d");
                    process.StartInfo.ArgumentList.Add(distro);
                }
                process.StartInfo.ArgumentList.Add("--exec");
                process.StartInfo.ArgumentList.Add(GitExecutable);
                process.StartInfo.ArgumentList.Add("-C");
                process.StartInfo.ArgumentList.Add(linuxDir);
                process.StartInfo.ArgumentList.Add(GitUpdateIndexCommand);
                process.StartInfo.ArgumentList.Add(flag);
                process.StartInfo.ArgumentList.Add(GitPathspecSeparator);
                process.StartInfo.ArgumentList.Add(relativePosixPath);
            }
            else
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = GitExecutable,
                    WorkingDirectory = workspacePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process.StartInfo.ArgumentList.Add(GitUpdateIndexCommand);
                process.StartInfo.ArgumentList.Add(flag);
                process.StartInfo.ArgumentList.Add(GitPathspecSeparator);
                process.StartInfo.ArgumentList.Add(relativePosixPath);
            }

            if (!process.Start())
            {
                logger.LogDebug("Failed to start git process for '{Flag}' on '{Path}'.", flag, relativePosixPath);
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(GitUpdateIndexTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(GitKillWaitTimeoutMs);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    logger.LogDebug(ex, "Failed to kill timed-out git update-index process for '{Path}'.", relativePosixPath);
                }

                try
                {
                    Task.WaitAll([stdoutTask, stderrTask], GitKillWaitTimeoutMs);
                }
                catch (AggregateException ex)
                {
                    logger.LogDebug(ex, "Stream draining failed after killing timed-out git update-index for '{Path}'.", relativePosixPath);
                }

                logger.LogWarning("git update-index {Flag} timed out for '{Path}'.", flag, relativePosixPath);
                return false;
            }

            Task.WaitAll([stdoutTask, stderrTask], GitUpdateIndexTimeoutMs);
            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Trim() : string.Empty;
                logger.LogDebug(
                    "git update-index {Flag} exited with {ExitCode} for '{Path}': {Stderr}",
                    flag,
                    process.ExitCode,
                    relativePosixPath,
                    stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            logger.LogDebug(ex, "Unable to run git update-index {Flag} for '{Path}'.", flag, relativePosixPath);
            return false;
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
