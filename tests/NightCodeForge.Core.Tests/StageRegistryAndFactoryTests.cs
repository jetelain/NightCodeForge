using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class StageRegistryAndFactoryTests
{
    private class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public void AddService<T>(T service) where T : notnull => _services[typeof(T)] = service;
        public object? GetService(Type serviceType)
        {
            if (_services.TryGetValue(serviceType, out var s)) return s;
            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                var loggerType = typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()[0]);
                var instanceField = loggerType.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceField != null) return instanceField.GetValue(null);
                var instanceProp = loggerType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return instanceProp?.GetValue(null);
            }
            return null;
        }
    }

    private class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CommandResult(0, "Build succeeded", string.Empty, TimeSpan.FromMilliseconds(50)));
        }
    }

    private readonly TestServiceProvider _serviceProvider;
    private readonly WorkflowStageRegistry _registry;
    private readonly WorkflowStageFactory _factory;

    public StageRegistryAndFactoryTests()
    {
        _serviceProvider = new TestServiceProvider();
        var fakeEnv = new FakeExecutionEnvironment();
        _serviceProvider.AddService<IExecutionEnvironment>(fakeEnv);
        _serviceProvider.AddService<IExecutionEnvironmentProvider>(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(fakeEnv));
        _serviceProvider.AddService<IOllamaModelPreloadService>(new NightCodeForge.Core.Tests.Fixtures.FakePreloadService());
        _serviceProvider.AddService<IPromptTemplateRenderer>(new ScribanPromptTemplateRenderer());
        _serviceProvider.AddService<TimeProvider>(TimeProvider.System);
        _serviceProvider.AddService<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(new NightCodeForge.Core.Execution.BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));
        _serviceProvider.AddService<IAgentActivityStreamer>(new Mock<IAgentActivityStreamer>().Object);
        _serviceProvider.AddService<ITaskRepository>(new Mock<ITaskRepository>().Object);
        _registry = new WorkflowStageRegistry();
        _factory = new WorkflowStageFactory(_registry, _serviceProvider);
    }

    [Fact]
    public void Registry_ContainsAllDefaultStandardImplementationTypes()
    {
        var registered = _registry.RegisteredTypes;

        Assert.Contains("agentic", registered);
        Assert.Contains("deterministic-build", registered);
        Assert.Contains("deterministic-test", registered);
        Assert.Contains("deterministic-test-with-coverage", registered);
        Assert.Contains("deterministic-sast", registered);
        Assert.Contains("deterministic-ratchet", registered);
        Assert.Contains("deterministic-composite", registered);
        Assert.Contains("deterministic-script", registered);
    }

    [Theory]
    [InlineData("agentic", typeof(GenericAgenticStage))]
    [InlineData("deterministic-build", typeof(DeterministicBuildStage))]
    [InlineData("deterministic-test", typeof(DeterministicTestStage))]
    [InlineData("deterministic-test-with-coverage", typeof(DeterministicTestWithCoverageGate))]
    [InlineData("deterministic-sast", typeof(DeterministicSastStage))]
    [InlineData("deterministic-ratchet", typeof(DeterministicWarningRatchetStage))]
    [InlineData("deterministic-composite", typeof(CompositeDeterministicStage))]
    [InlineData("deterministic-script", typeof(DeterministicScriptStage))]
    public void Factory_CreateStage_ResolvesAllStandardImplementationTypes(string implementationType, Type expectedType)
    {
        var stageDefinition = new StageDefinition
        {
            StageId = $"test-{implementationType}",
            DisplayName = $"Test {implementationType}",
            QueueTier = StageQueueTier.Implementation,
            Kind = implementationType.StartsWith("agentic", StringComparison.OrdinalIgnoreCase)
                ? WorkflowStageKind.Agentic
                : WorkflowStageKind.Deterministic,
            CsharpImplementationType = implementationType,
            ConfigurationJson = "{}"
        };

        var stage = _factory.CreateStage(stageDefinition);

        Assert.NotNull(stage);
        Assert.IsType(expectedType, stage);
        Assert.False(string.IsNullOrWhiteSpace(stage.StageId));
        Assert.False(string.IsNullOrWhiteSpace(stage.DisplayName));
    }

    [Fact]
    public void Registry_RegisterHandler_CaseInsensitiveMatching()
    {
        var stageDefinition = new StageDefinition
        {
            StageId = "test-case-insensitive",
            DisplayName = "Case Insensitive Test",
            QueueTier = StageQueueTier.Implementation,
            Kind = WorkflowStageKind.Agentic,
            CsharpImplementationType = "AGENTIC", // UPPERCASE
            ConfigurationJson = "{}"
        };

        var stage = _factory.CreateStage(stageDefinition);

        Assert.NotNull(stage);
        Assert.IsType<GenericAgenticStage>(stage);
    }

    [Fact]
    public void Registry_RegisterHandler_ThrowsOnNullOrEmptyType()
    {
        Assert.Throws<ArgumentException>(() => _registry.RegisterHandler(string.Empty, (sp, def) => null!));
        Assert.Throws<ArgumentException>(() => _registry.RegisterHandler("   ", (sp, def) => null!));
        Assert.Throws<ArgumentNullException>(() => _registry.RegisterHandler("valid-type", null!));
    }

    [Fact]
    public void Registry_RegisterHandler_AllowsOverwritingExistingRegistration()
    {
        var customCalled = false;
        _registry.RegisterHandler("agentic", (sp, def) =>
        {
            customCalled = true;
            return new GenericAgenticStage(def.StageId, "Overridden", new AgenticStageConfig(), sp.GetRequiredService<IPromptTemplateRenderer>(), sp.GetRequiredService<TimeProvider>());
        });

        var stage = _factory.CreateStage(new StageDefinition
        {
            StageId = "custom-agentic",
            DisplayName = "Original",
            QueueTier = StageQueueTier.Implementation,
            CsharpImplementationType = "agentic"
        });

        Assert.True(customCalled);
        Assert.Equal("Overridden", stage.DisplayName);
    }

    [Fact]
    public void Registry_TryGetFactory_ReturnsTrueForExisting_AndFalseForUnregistered()
    {
        Assert.True(_registry.TryGetFactory("agentic", out var factory));
        Assert.NotNull(factory);

        Assert.False(_registry.TryGetFactory("unknown-type-xyz", out var missingFactory));
        Assert.Null(missingFactory);

        Assert.False(_registry.TryGetFactory(string.Empty, out var emptyFactory));
        Assert.Null(emptyFactory);
    }

    [Fact]
    public void Factory_CreateStage_ThrowsOnUnregisteredType_WithHelpfulMessage()
    {
        var definition = new StageDefinition
        {
            StageId = "bad-stage",
            DisplayName = "Bad Stage",
            QueueTier = StageQueueTier.Implementation,
            CsharpImplementationType = "custom-quantum-solver"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => _factory.CreateStage(definition));

        Assert.Contains("Unknown stage C# implementation type 'custom-quantum-solver'", ex.Message);
        Assert.Contains("Available registered types:", ex.Message);
        Assert.Contains("agentic", ex.Message);
        Assert.Contains("deterministic-build", ex.Message);
    }

    [Fact]
    public void Factory_CreateStage_ThrowsOnNullStageDefinition()
    {
        Assert.Throws<ArgumentNullException>(() => _factory.CreateStage(null!));
    }

    [Fact]
    public void Factory_CreateStage_AgenticStage_HydratesPropertiesCorrectly()
    {
        var config = new AgenticStageConfig
        {
            AgentProfileId = "specialized-reviewer",
            RequiredMode = AgentMode.Review,
            StageDirective = "Inspect all security annotations.",
            PromptTemplate = "Task: {{ task.title }}",
            LoopBackStageId = "implementation",
            MaxRetries = 4,
            TimeoutMinutes = 20
        };

        var definition = new StageDefinition
        {
            StageId = "security-review",
            DisplayName = "Security Review",
            QueueTier = StageQueueTier.CodeReview,
            Kind = WorkflowStageKind.Agentic,
            CsharpImplementationType = "agentic",
            ConfigurationJson = JsonSerializer.Serialize(config)
        };

        var stage = _factory.CreateStage(definition);
        var agentic = Assert.IsType<GenericAgenticStage>(stage);

        Assert.Equal("security-review", agentic.StageId);
        Assert.Equal("Security Review", agentic.DisplayName);
        Assert.Equal(StageQueueTier.CodeReview, agentic.QueueTier);
        Assert.Equal(AgentMode.Review, agentic.RequiredMode);
        Assert.Equal("specialized-reviewer", agentic.AgentProfileId);
        Assert.Equal("Inspect all security annotations.", agentic.StageDirective);
        Assert.Equal("Task: {{ task.title }}", agentic.PromptTemplate);
        Assert.Equal("implementation", agentic.LoopBackStageId);
        Assert.Equal(4, agentic.MaxRetries);
        Assert.Equal(20, agentic.TimeoutMinutes);
    }
}
