using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly ILogger<LlmConfigurationWriter> _logger;

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

        var configFilePath = await UserConfigJsonFile.UpdateAsync(
            _hostEnvironment.ContentRootPath,
            _logger,
            root =>
            {
                var updatedOptions = GetBaseOptionsForUpdate(root);
                updateAction(updatedOptions);

                if (updatedOptions.OpenAiCompatible?.AllowedModels != null)
                {
                    updatedOptions.OpenAiCompatible.AllowedModels = SanitizeAllowedModels(updatedOptions.OpenAiCompatible.AllowedModels);
                }

                // Remove any alternate-casing section if present
                UserConfigJsonFile.FindAndNormalizeSection(root, LlmProviderOptions.SectionName);

                var llmNode = JsonSerializer.SerializeToNode(updatedOptions, UserConfigJsonFile.JsonOptions) as JsonObject;
                if (llmNode != null)
                {
                    root[LlmProviderOptions.SectionName] = llmNode;
                }
            },
            cancellationToken);

        _logger.LogInformation("Updated LLM provider configuration saved to {Path}", configFilePath);
    }

    public async Task SaveAllowedModelsAsync(IEnumerable<OpenAiAllowedModel> allowedModels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedModels);

        var configFilePath = await UserConfigJsonFile.UpdateAsync(
            _hostEnvironment.ContentRootPath,
            _logger,
            root =>
            {
                var llmNode = UserConfigJsonFile.FindAndNormalizeSection(root, LlmProviderOptions.SectionName);
                if (llmNode == null)
                {
                    llmNode = new JsonObject();
                    root[LlmProviderOptions.SectionName] = llmNode;
                }

                var openAiNode = UserConfigJsonFile.FindAndNormalizeSection(llmNode, nameof(LlmProviderOptions.OpenAiCompatible));
                if (openAiNode == null)
                {
                    openAiNode = new JsonObject();
                    llmNode[nameof(LlmProviderOptions.OpenAiCompatible)] = openAiNode;
                }

                var sanitizedModels = SanitizeAllowedModels(allowedModels);
                openAiNode[nameof(OpenAiCompatibleOptions.AllowedModels)] = JsonSerializer.SerializeToNode(sanitizedModels, UserConfigJsonFile.JsonOptions);
            },
            cancellationToken);

        _logger.LogInformation("Allowed models configuration saved to {Path}", configFilePath);
    }

    private LlmProviderOptions GetBaseOptionsForUpdate(JsonObject root)
    {
        var baseOptions = CloneCurrentOptions();
        var existingLlmNode = UserConfigJsonFile.FindAndNormalizeSection(root, LlmProviderOptions.SectionName);
        if (existingLlmNode != null)
        {
            try
            {
                var baseNode = JsonSerializer.SerializeToNode(baseOptions, UserConfigJsonFile.JsonOptions) as JsonObject ?? new JsonObject();
                MergeJsonNodes(baseNode, existingLlmNode);

                // Preserve legacy ModelId if DefaultModelId was not explicitly set in config.json
                if (existingLlmNode.TryGetPropertyValue("ModelId", out var legacyModelId) &&
                    !existingLlmNode.ContainsKey("DefaultModelId") &&
                    legacyModelId != null)
                {
                    baseNode["DefaultModelId"] = legacyModelId.DeepClone();
                }

                return baseNode.Deserialize<LlmProviderOptions>(UserConfigJsonFile.JsonOptions) ?? baseOptions;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize existing LlmProvider section from configuration file; falling back to memory baseline.");
            }
        }

        return baseOptions;
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

    private LlmProviderOptions CloneCurrentOptions()
    {
        var current = _options.Value;
        var json = JsonSerializer.Serialize(current, UserConfigJsonFile.JsonOptions);
        return JsonSerializer.Deserialize<LlmProviderOptions>(json, UserConfigJsonFile.JsonOptions) ?? new LlmProviderOptions();
    }
}
