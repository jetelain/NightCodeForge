using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Core.Domain;
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
                services.AddHttpClient<IOllamaRouterGatewayService, OllamaGatewayService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelCatalogService, OllamaModelCatalogService>().AddTransientHttpErrorRetry();
                break;
            case LlmProviderType.OllamaRouter:
                services.AddHttpClient<IOllamaRouterGatewayService, OllamaRouterGatewayService>().AddTransientHttpErrorRetry();
                services.AddHttpClient<ILlmModelCatalogService, OllamaRouterModelCatalogService>().AddTransientHttpErrorRetry();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "Unsupported LLM provider type.");
        }

        services.AddHttpClient<IOllamaModelPreloadService, OllamaModelPreloadService>().AddTransientHttpErrorRetry();
        return services;
    }
}
