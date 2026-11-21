using System.Globalization;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation and protocol-URI helpers for <c>OllamaRouterGatewayCard</c> and
/// <c>OllamaRouterClusterMonitor</c>, keeping Razor components free of inline logic.
/// </summary>
public static class OllamaRouterGatewayUiHelper
{
    public const string OpenAppUri = OllamaRouterProtocolUris.OpenApp;
    public const string SetupWizardUri = OllamaRouterProtocolUris.SetupWizard;

    public static string FormatSyncedModelsSummary(MappedRouterRecommendations mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);

        var architect = string.IsNullOrWhiteSpace(mapped.ArchitectModelId) ? "(inherit)" : mapped.ArchitectModelId;
        var coder = string.IsNullOrWhiteSpace(mapped.CoderModelId) ? "(inherit)" : mapped.CoderModelId;
        var defaultModel = string.IsNullOrWhiteSpace(mapped.DefaultModelId) ? "(auto)" : mapped.DefaultModelId;
        var contextText = mapped.ContextWindowTokens.ToString("N0", CultureInfo.InvariantCulture);

        return $"Architect: {architect} | Coder: {coder} | Default: {defaultModel} | Context: {contextText} tokens. Saved to config/config.json.";
    }
}
