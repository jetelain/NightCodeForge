using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Provides synchronized, atomic read-modify-write operations on the user-owned <c>config/config.json</c> file
/// while preserving unrelated root sections.
/// </summary>
public static class UserConfigJsonFile
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public static async Task<string> UpdateAsync(
        string contentRootPath,
        ILogger logger,
        Action<JsonObject> mutateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mutateRoot);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var configFilePath = HostConfigurationSources.GetFullPath(contentRootPath);
            var root = await ReadOrCreateConfigJsonAsync(configFilePath, logger, cancellationToken);

            mutateRoot(root);

            await WriteConfigJsonAsync(configFilePath, root, logger, cancellationToken);
            return configFilePath;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static JsonObject? FindAndNormalizeSection(JsonObject parent, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        string? existingKey = null;
        JsonNode? existingNode = null;

        foreach (var (key, value) in parent)
        {
            if (string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = key;
                existingNode = value;
                break;
            }
        }

        if (existingKey != null && existingKey != targetKey)
        {
            parent.Remove(existingKey);
            if (existingNode != null)
            {
                parent[targetKey] = existingNode;
            }
        }

        return parent[targetKey] as JsonObject;
    }

    private static async Task<JsonObject> ReadOrCreateConfigJsonAsync(
        string path,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(text);
            return node as JsonObject ?? throw new InvalidOperationException(
                $"Configuration file at '{path}' does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Configuration file at '{Path}' contains invalid JSON.", path);
            throw new InvalidOperationException(
                $"Configuration file at '{path}' contains invalid JSON and cannot be safely updated.", ex);
        }
    }

    private static async Task WriteConfigJsonAsync(
        string path,
        JsonObject root,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var jsonString = root.ToJsonString(JsonOptions);
        var tempFilePath = Path.Combine(directory ?? ".", $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempFilePath, jsonString, cancellationToken);
            File.Move(tempFilePath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Best-effort cleanup of temporary configuration file '{TempPath}' failed.", tempFilePath);
                }
            }
        }
    }
}

