namespace NightCodeForge.Core.Domain;

/// <summary>
/// Defines harness, model, and reasoning overrides applied to a pipeline stage profile
/// when executing a task of a specific <see cref="TaskDifficulty"/> (Difficult or Expert).
/// </summary>
public class DifficultyStageOverride
{
    public string HarnessId { get; set; } = StandardHarnessIds.Crucible;
    public string ModelName { get; set; } = string.Empty;
    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.High;
    public int? ThinkingTokenBudget { get; set; }
    public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public string? HarnessConfigurationJson { get; set; }

    public DifficultyStageOverride Clone() => new()
    {
        HarnessId = HarnessId,
        ModelName = ModelName,
        ThinkingEffort = ThinkingEffort,
        ThinkingTokenBudget = ThinkingTokenBudget,
        RoutingTarget = RoutingTarget,
        ContextWindowTokens = ContextWindowTokens,
        MaxOutputTokens = MaxOutputTokens,
        HarnessConfigurationJson = HarnessConfigurationJson
    };
}
