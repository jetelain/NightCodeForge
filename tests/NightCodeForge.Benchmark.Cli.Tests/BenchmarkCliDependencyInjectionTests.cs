using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Hosting;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Telemetry;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class BenchmarkCliDependencyInjectionTests
{
    [Theory]
    [InlineData(LlmProviderType.Ollama, "http://localhost:11434")]
    [InlineData(LlmProviderType.OllamaRouter, "http://localhost:11435")]
    [InlineData(LlmProviderType.OpenAiCompatible, "https://api.openai.com/v1")]
    public void AddNightCodeForgeBenchmarkCliServices_ResolvesRunnerAndHarnessesWithoutDatabaseOrSignalR(
        LlmProviderType providerType,
        string endpoint)
    {
        var services = new ServiceCollection();
        services.AddNightCodeForgeBenchmarkCliServices(new BenchmarkCliHostConfig(
            ProviderType: providerType,
            Endpoint: endpoint,
            ApiKey: providerType == LlmProviderType.OpenAiCompatible ? "test-key" : null,
            AllowedOpenAiModels: ["gpt-4o", "deepseek-chat"],
            BenchmarkRootDirectory: null));

        // Verify zero EF Core / AppDbContext registrations exist
        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(AppDbContext) ||
            d.ServiceType == typeof(IDbContextFactory<AppDbContext>));

        // Verify zero SignalR / Hangfire registrations exist
        Assert.DoesNotContain(services, d =>
            (d.ServiceType.Namespace?.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (d.ServiceType.Namespace?.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (d.ImplementationType?.Namespace?.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (d.ServiceType.FullName?.Contains("SignalRActivityBridgeService", StringComparison.OrdinalIgnoreCase) ?? false) ||
            d.ImplementationType == typeof(OperationsTelemetryService));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var runner = sp.GetRequiredService<ICrucibleBenchmarkRunner>();
        var loader = sp.GetRequiredService<IBenchmarkDatasetLoader>();
        var workspaceManager = sp.GetRequiredService<IBenchmarkWorkspaceManager>();
        var verifier = sp.GetRequiredService<IBenchmarkVerifier>();
        var provenanceProvider = sp.GetRequiredService<IBenchmarkProvenanceProvider>();
        var harnessRegistry = sp.GetRequiredService<IHarnessRegistry>();
        var conversationCoordinator = sp.GetRequiredService<ICrucibleConversationCoordinator>();
        var containerFactory = sp.GetRequiredService<IContainerEnvironmentFactory>();
        var taskRepo = sp.GetRequiredService<ITaskRepository>();
        var workspaceRepo = sp.GetRequiredService<IWorkspaceRepository>();
        var workflowRepo = sp.GetRequiredService<IWorkflowRepository>();
        var telemetryService = sp.GetRequiredService<IOperationsTelemetryService>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var chatClientFactory = sp.GetRequiredService<ILlmChatClientFactory>();
        var gatewayService = sp.GetRequiredService<ILlmGatewayService>();
        var catalogService = sp.GetRequiredService<ILlmModelCatalogService>();
        var attachmentRepo = sp.GetRequiredService<ITaskAttachmentRepository>();
        var webSearchService = sp.GetRequiredService<IWebSearchService>();
        var secretStore = sp.GetRequiredService<IApiKeySecretStore>();

        Assert.NotNull(runner);
        Assert.NotNull(loader);
        Assert.NotNull(workspaceManager);
        Assert.NotNull(verifier);
        Assert.NotNull(provenanceProvider);
        Assert.NotNull(containerFactory);
        Assert.NotNull(chatClient);
        Assert.NotNull(chatClientFactory);
        Assert.NotNull(gatewayService);
        Assert.NotNull(catalogService);
        Assert.NotNull(attachmentRepo);
        Assert.NotNull(webSearchService);
        Assert.NotNull(secretStore);
        Assert.Equal(typeof(InMemoryOperationsTelemetryService), telemetryService.GetType());
        Assert.IsType<InMemoryCrucibleConversationCoordinator>(conversationCoordinator);
        Assert.IsType<InMemoryTaskRepository>(taskRepo);
        Assert.IsType<InMemoryWorkspaceRepository>(workspaceRepo);
        Assert.IsType<InMemoryBenchmarkWorkflowRepository>(workflowRepo);
        Assert.NotNull(harnessRegistry.GetHarness(StandardHarnessIds.Crucible));

        if (providerType == LlmProviderType.OpenAiCompatible)
        {
            var llmOpts = sp.GetRequiredService<IOptions<LlmProviderOptions>>().Value;
            Assert.Equal(endpoint, llmOpts.EffectiveEndpoint);
            Assert.Contains(llmOpts.OpenAiCompatible.AllowedModels, m => m.ModelId == "gpt-4o");
            Assert.Contains(llmOpts.OpenAiCompatible.AllowedModels, m => m.ModelId == "deepseek-chat");
            Assert.NotNull(chatClientFactory.CreateClient("gpt-4o"));
        }
    }

    [Fact]
    public void AddNightCodeForgeBenchmarkCliServices_WhitespaceEndpoint_ThrowsArgumentException()
    {
        var services = new ServiceCollection();
        var config = new BenchmarkCliHostConfig(
            ProviderType: LlmProviderType.Ollama,
            Endpoint: "   ",
            ApiKey: null,
            AllowedOpenAiModels: [],
            BenchmarkRootDirectory: null);

        Assert.Throws<ArgumentException>(() => services.AddNightCodeForgeBenchmarkCliServices(config));
    }

    [Fact]
    public void EnvironmentBenchmarkApiKeySecretStore_RespectsPrecedenceWithoutTouchingKeyring()
    {
        var storeWithExplicit = new EnvironmentBenchmarkApiKeySecretStore(
            " explicit-key ",
            name => name == BenchmarkCliConstants.BenchmarkApiKeyEnvVar ? "bench-key" : "openai-key");
        Assert.False(storeWithExplicit.IsKeyringAvailable());
        Assert.Equal("explicit-key", storeWithExplicit.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));

        var storeWithBenchEnv = new EnvironmentBenchmarkApiKeySecretStore(
            null,
            name => name switch
            {
                BenchmarkCliConstants.BenchmarkApiKeyEnvVar => " bench-key ",
                ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable => "openai-key",
                _ => null
            });
        Assert.Equal("bench-key", storeWithBenchEnv.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));

        var storeWithOpenAiEnv = new EnvironmentBenchmarkApiKeySecretStore(
            null,
            name => name switch
            {
                ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable => " openai-key ",
                ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable => " ollama-key ",
                _ => null
            });
        Assert.Equal("openai-key", storeWithOpenAiEnv.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("ollama-key", storeWithOpenAiEnv.ResolveEffectiveOllamaWebSearchApiKey());
        Assert.True(storeWithOpenAiEnv.HasOllamaWebSearchApiKey());
    }

    [Fact]
    public async Task InMemoryBenchmarkWorkflowRepository_GetWorkflowsAsync_IncludesGlobalAndWorkspaceWorkflows()
    {
        var repo = new InMemoryBenchmarkWorkflowRepository();
        await repo.CreateWorkflowAsync(new WorkflowDefinition { Name = "Global", Code = "global", WorkspaceId = null });
        await repo.CreateWorkflowAsync(new WorkflowDefinition { Name = "Workspace 1", Code = "ws1", WorkspaceId = 1 });
        await repo.CreateWorkflowAsync(new WorkflowDefinition { Name = "Workspace 2", Code = "ws2", WorkspaceId = 2 });

        var ws1Workflows = await repo.GetWorkflowsAsync(workspaceId: 1);

        Assert.Equal(2, ws1Workflows.Count);
        Assert.Contains(ws1Workflows, w => w.Code == "global");
        Assert.Contains(ws1Workflows, w => w.Code == "ws1");
        Assert.DoesNotContain(ws1Workflows, w => w.Code == "ws2");
    }

    [Fact]
    public void AddNightCodeForgeExecutionInfrastructure_ProvidesGitAndContainerServicesWithoutDb()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightCodeForgeExecutionInfrastructure();

        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(AppDbContext) ||
            d.ServiceType == typeof(IDbContextFactory<AppDbContext>));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        Assert.NotNull(provider.GetRequiredService<IGitCliRunner>());
        Assert.NotNull(provider.GetRequiredService<IGitWorkspaceManager>());
        Assert.NotNull(provider.GetRequiredService<IProcessRunner>());
        Assert.NotNull(provider.GetRequiredService<IHostSdkDiscoveryService>());
        Assert.NotNull(provider.GetRequiredService<IWorkspacePathResolver>());
        Assert.NotNull(provider.GetRequiredService<IContainerEnvironmentFactory>());
        Assert.NotNull(provider.GetRequiredService<IExecutionEnvironmentProvider>());
        Assert.NotNull(provider.GetRequiredService<NightCodeForgeContainerExecutor>());
        Assert.Equal(typeof(InMemoryOperationsTelemetryService), provider.GetRequiredService<IOperationsTelemetryService>().GetType());
    }
}
