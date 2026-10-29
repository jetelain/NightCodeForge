using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Infrastructure.Inference;
using NightCodeForge.Infrastructure.Resilience;

namespace NightCodeForge.Infrastructure;

/// <summary>Provider-specific registration of LLM inference services.</summary>
public static class InferenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway and catalog implementations matching <paramref name="providerType"/>.
    /// The provider is fixed at startup; switching requires a restart.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeInference(this IServiceCollection services, LlmProviderType providerType)
    {
        services.AddOptions<LlmEndpointOptions>();
        services.AddMemoryCache();
        services.TryAddSingleton<ILlmContextWindowCache, LlmContextWindowCache>();
        services.TryAddSingleton<IOpenAiModelAllowlistCache, OpenAiModelAllowlistCache>();
        services.TryAddSingleton<ILlmConfigurationWriter, LlmConfigurationWriter>();

        switch (providerType)
        {
            case LlmProviderType.Ollama:
                services.TryAddSingleton<IDefaultModelSelector, OllamaDefaultModelSelector>();
                services.AddHttpClient<ILlmGatewayService, OllamaGatewayService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelCatalogService, OllamaModelCatalogService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelPreloadService, OllamaModelPreloadService>().AddTransientHttpErrorRetry();
                break;
            case LlmProviderType.OllamaRouter:
                services.TryAddSingleton<IDefaultModelSelector, OllamaDefaultModelSelector>();
                services.AddHttpClient<ILlmGatewayService, OllamaRouterGatewayService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelCatalogService, OllamaRouterModelCatalogService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelPreloadService, OllamaModelPreloadService>().AddTransientHttpErrorRetry();
                break;
            case LlmProviderType.OpenAiCompatible:
                services.TryAddSingleton<IDefaultModelSelector, OpenAiDefaultModelSelector>();
                services.AddHttpClient<ILlmGatewayService, OpenAiCompatibleGatewayService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelCatalogService, OpenAiCompatibleModelCatalogService>().AddTransientHttpErrorRetry();
                services.TryAddSingleton<ILlmModelPreloadService, NoOpModelPreloadService>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "Unsupported LLM provider type.");
        }

        // Forward legacy interfaces to primary interfaces for backward compatibility
        services.TryAddTransient<IOllamaRouterGatewayService>(sp => (IOllamaRouterGatewayService)sp.GetRequiredService<ILlmGatewayService>());

        return services;
    }
}
