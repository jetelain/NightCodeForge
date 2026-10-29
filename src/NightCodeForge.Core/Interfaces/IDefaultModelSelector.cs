namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Strategy for selecting the optimal default model from available models in a catalog.
/// Implemented polymorphically per provider.
/// </summary>
public interface IDefaultModelSelector
{
    AvailableModelInfo? SelectDefaultModel(IEnumerable<AvailableModelInfo> candidates, string? explicitDefaultModelId = null);
}

