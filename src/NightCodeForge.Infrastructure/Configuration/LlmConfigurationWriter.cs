using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;

namespace NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Persists mutated LLM provider options and allowed models into the persistent user
/// config/config.json file using System.Text.Json.Nodes.JsonObject to preserve other settings.
/// </summary>
public class LlmConfigurationWriter : ILlmConfigurationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHostEnvironment _hostEnvironment;
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly ILogger<LlmConfigurationWriter> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public LlmConfigurationWriter(
        IHostEnvironment hostEnvironment,
        IOptions<LlmProviderOptions> options,
        ILogger<LlmConfigurationWriter> logger)
    {
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SaveProviderConfigurationAsync(Action<LlmProviderOptions> updateAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateAction);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var configFilePath = HostConfigurationSources.GetFullPath(_hostEnvironment.ContentRootPath);
            var root = await ReadOrCreateConfigJsonAsync(configFilePath, cancellationToken);

            var updatedOptions = GetBaseOptionsForUpdate(root);
            updateAction(updatedOptions);

            if (updatedOptions.OpenAiCompatible?.AllowedModels != null)
            {
                updatedOptions.OpenAiCompatible.AllowedModels = SanitizeAllowedModels(updatedOptions.OpenAiCompatible.AllowedModels);
            }

            // Remove any alternate-casing section if present
            FindAndNormalizeSection(root, LlmProviderOptions.SectionName);

            var llmNode = JsonSerializer.SerializeToNode(updatedOptions, JsonOptions) as JsonObject;
            if (llmNode != null)
            {
                root[LlmProviderOptions.SectionName] = llmNode;
            }

            await WriteConfigJsonAsync(configFilePath, root, cancellationToken);
            _logger.LogInformation("Updated LLM provider configuration saved to {Path}", configFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAllowedModelsAsync(IEnumerable<OpenAiAllowedModel> allowedModels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedModels);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var configFilePath = HostConfigurationSources.GetFullPath(_hostEnvironment.ContentRootPath);
            var root = await ReadOrCreateConfigJsonAsync(configFilePath, cancellationToken);

            var llmNode = FindAndNormalizeSection(root, LlmProviderOptions.SectionName);
            if (llmNode == null)
            {
                llmNode = new JsonObject();
                root[LlmProviderOptions.SectionName] = llmNode;
            }

            var openAiNode = FindAndNormalizeSection(llmNode, nameof(LlmProviderOptions.OpenAiCompatible));
            if (openAiNode == null)
            {
                openAiNode = new JsonObject();
                llmNode[nameof(LlmProviderOptions.OpenAiCompatible)] = openAiNode;
            }

            var sanitizedModels = SanitizeAllowedModels(allowedModels);
            openAiNode[nameof(OpenAiCompatibleOptions.AllowedModels)] = JsonSerializer.SerializeToNode(sanitizedModels, JsonOptions);

            await WriteConfigJsonAsync(configFilePath, root, cancellationToken);
            _logger.LogInformation("Allowed models configuration saved to {Path}", configFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private LlmProviderOptions GetBaseOptionsForUpdate(JsonObject root)
    {
        var baseOptions = CloneCurrentOptions();
        var existingLlmNode = FindAndNormalizeSection(root, LlmProviderOptions.SectionName);
        if (existingLlmNode != null)
        {
            try
            {
                var baseNode = JsonSerializer.SerializeToNode(baseOptions, JsonOptions) as JsonObject ?? new JsonObject();
                MergeJsonNodes(baseNode, existingLlmNode);

                // Preserve legacy ModelId if DefaultModelId was not explicitly set in config.json
                if (existingLlmNode.TryGetPropertyValue("ModelId", out var legacyModelId) &&
                    !existingLlmNode.ContainsKey("DefaultModelId") &&
                    legacyModelId != null)
                {
                    baseNode["DefaultModelId"] = legacyModelId.DeepClone();
                }

                return baseNode.Deserialize<LlmProviderOptions>(JsonOptions) ?? baseOptions;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize existing LlmProvider section from configuration file; falling back to memory baseline.");
            }
        }

        return baseOptions;
    }

    private static JsonObject? FindAndNormalizeSection(JsonObject parent, string targetKey)
    {
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

    private static List<OpenAiAllowedModel> SanitizeAllowedModels(IEnumerable<OpenAiAllowedModel?>? allowedModels)
    {
        if (allowedModels == null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OpenAiAllowedModel>();

        foreach (var m in allowedModels)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.ModelId))
            {
                continue;
            }

            var trimmedId = m.ModelId.Trim();
            if (!seen.Add(trimmedId))
            {
                continue;
            }

            m.ModelId = trimmedId;
            if (!string.IsNullOrWhiteSpace(m.DisplayName))
            {
                m.DisplayName = m.DisplayName.Trim();
            }

            if (m.ContextWindowTokens <= 0)
            {
                m.ContextWindowTokens = LlmProviderOptions.DefaultContextWindowTokens;
            }

            if (m.MaxOutputTokens <= 0)
            {
                m.MaxOutputTokens = LlmProviderOptions.DefaultMaxOutputTokens;
            }

            result.Add(m);
        }

        return result;
    }

    private static void MergeJsonNodes(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceSubObj && target[key] is JsonObject targetSubObj)
            {
                MergeJsonNodes(targetSubObj, sourceSubObj);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private async Task<JsonObject> ReadOrCreateConfigJsonAsync(string path, CancellationToken cancellationToken)
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
            _logger.LogError(ex, "Configuration file at '{Path}' contains invalid JSON.", path);
            throw new InvalidOperationException(
                $"Configuration file at '{path}' contains invalid JSON and cannot be safely updated.", ex);
        }
    }

    private static async Task WriteConfigJsonAsync(string path, JsonObject root, CancellationToken cancellationToken)
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
                catch
                {
                    // Best-effort cleanup of temporary file
                }
            }
        }
    }

    private LlmProviderOptions CloneCurrentOptions()
    {
        var current = _options.Value;
        var json = JsonSerializer.Serialize(current, JsonOptions);
        return JsonSerializer.Deserialize<LlmProviderOptions>(json, JsonOptions) ?? new LlmProviderOptions();
    }
}
