namespace NightCodeForge.Installer.Core.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Shared thread-safe, Native AOT-compatible atomic reader/writer for NightCodeForge's
/// <c>&lt;InstallDir&gt;/config/config.json</c> user configuration overlay.
/// </summary>
internal static class InstallerUserConfigWriter
{
    private static readonly SemaphoreSlim ConfigFileLock = new(1, 1);

    public static async Task<string> UpdateConfigAsync(
        string installDirectory,
        Action<JsonObject, string> updateAction,
        Func<string, Exception?, Exception> createException,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(updateAction);
        ArgumentNullException.ThrowIfNull(createException);

        var configDir = Path.Combine(installDirectory, InstallerConstants.ConfigDirectoryName);
        var configFilePath = Path.Combine(configDir, InstallerConstants.UserConfigFileName);

        await ConfigFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var root = await ReadOrCreateRootObjectAsync(configFilePath, createException, cancellationToken)
                    .ConfigureAwait(false);

                updateAction(root, configFilePath);

                Directory.CreateDirectory(configDir);
                var tempFilePath = Path.Combine(configDir, $"{InstallerConstants.UserConfigFileName}.{Guid.NewGuid():N}.tmp");

                try
                {
                    await using (var stream = new FileStream(
                        tempFilePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        InstallerConstants.DownloadBufferSizeBytes,
                        useAsync: true))
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            root,
                            InstallerJsonContext.Default.JsonObject,
                            cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await MoveConfigWithRetryAsync(tempFilePath, configFilePath, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (File.Exists(tempFilePath))
                    {
                        try
                        {
                            File.Delete(tempFilePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Justification: Best-effort cleanup of temp config file must not mask primary failure.
                            _ = ex;
                        }
                    }
                }

                return configFilePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw createException(
                    $"Failed to read or update configuration file '{configFilePath}': {ex.Message}",
                    ex);
            }
        }
        finally
        {
            ConfigFileLock.Release();
        }
    }

    public static JsonObject MergeAndNormalizeSection(
        JsonObject parent,
        string canonicalKey,
        string configFilePath,
        Func<string, Exception?, Exception> createException)
    {
        var matchingEntries = parent
            .Where(kvp => string.Equals(kvp.Key, canonicalKey, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        if (matchingEntries.Count == 0)
        {
            var created = new JsonObject();
            parent[canonicalKey] = created;
            return created;
        }

        foreach (var (key, _) in matchingEntries)
        {
            parent.Remove(key);
        }

        var merged = new JsonObject();
        foreach (var (_, value) in matchingEntries)
        {
            if (value is null)
            {
                continue;
            }

            if (value is not JsonObject sectionObject)
            {
                throw createException(
                    $"Configuration section '{canonicalKey}' in '{configFilePath}' must be a JSON object.",
                    null);
            }

            foreach (var (childKey, childValue) in sectionObject)
            {
                RemoveMatchingKeys(merged, childKey);
                merged[childKey] = childValue?.DeepClone();
            }
        }

        parent[canonicalKey] = merged;
        return merged;
    }

    public static void SetStringProperty(JsonObject parent, string key, string value)
    {
        RemoveMatchingKeys(parent, key);
        parent[key] = JsonValue.Create(value);
    }

    public static void SetOptionalStringProperty(JsonObject parent, string key, string? value)
    {
        RemoveMatchingKeys(parent, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent[key] = JsonValue.Create(value);
        }
    }

    public static void SetIntProperty(JsonObject parent, string key, int value)
    {
        RemoveMatchingKeys(parent, key);
        parent[key] = JsonValue.Create(value);
    }

    public static void RemoveMatchingKeys(JsonObject parent, string targetKey)
    {
        var keysToRemove = parent
            .Select(kvp => kvp.Key)
            .Where(key => string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            parent.Remove(key);
        }
    }

    private static async Task MoveConfigWithRetryAsync(
        string tempFilePath,
        string configFilePath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempFilePath, configFilePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < InstallerConstants.ConfigFileMoveRetryAttempts)
            {
                await Task.Delay(InstallerConstants.ConfigFileMoveRetryDelayMs, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<JsonObject> ReadOrCreateRootObjectAsync(
        string configFilePath,
        Func<string, Exception?, Exception> createException,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return new JsonObject();
        }

        var existingJson = await File.ReadAllTextAsync(configFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return new JsonObject();
        }

        try
        {
            var parsed = JsonNode.Parse(
                existingJson,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            if (parsed is not JsonObject root)
            {
                throw createException(
                    $"Configuration file '{configFilePath}' does not contain a root JSON object.",
                    null);
            }

            return root;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw createException(
                $"Configuration file '{configFilePath}' contains invalid JSON and cannot be safely updated.",
                ex);
        }
    }
}
