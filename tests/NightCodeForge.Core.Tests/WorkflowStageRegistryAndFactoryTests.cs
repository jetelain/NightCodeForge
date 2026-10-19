using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class WorkflowStageRegistryAndFactoryTests
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
                var instanceProp = loggerType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return instanceProp?.GetValue(null);
            }
            return null;
        }
    }

    private class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public CommandResult Result { get; set; } = new CommandResult(1, string.Empty, "error CS1002: ; expected [proj.csproj]", TimeSpan.FromMilliseconds(10));

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }
    }

    private readonly TestServiceProvider _serviceProvider;
    private readonly WorkflowStageRegistry _registry;
    private readonly WorkflowStageFactory _factory;

    public WorkflowStageRegistryAndFactoryTests()
    {
        _serviceProvider = new TestServiceProvider();
        var fakeEnv = new FakeExecutionEnvironment();
        _serviceProvider.AddService<IExecutionEnvironment>(fakeEnv);
        _serviceProvider.AddService<IExecutionEnvironmentProvider>(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(fakeEnv));
        _serviceProvider.AddService<IOllamaModelPreloadService>(new NightCodeForge.Core.Tests.Fixtures.FakePreloadService());
        _serviceProvider.AddService<IPromptTemplateRenderer>(new ScribanPromptTemplateRenderer());
        _serviceProvider.AddService<TimeProvider>(TimeProvider.System);
        _serviceProvider.AddService<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(new NightCodeForge.Core.Execution.BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));
        _registry = new WorkflowStageRegistry();
        _factory = new WorkflowStageFactory(_registry, _serviceProvider);
    }

    [Fact]
    public void CreateStage_AgenticUnifiedType_HydratesGenericAgenticStageCorrectly()
    {
        var agenticConfig = new AgenticStageConfig
        {
            AgentProfileId = "architect-agent",
            RequiredMode = AgentMode.ReadOnly,
            StageDirective = "Review the architecture and ask questions.",
            PromptTemplate = "Execute {{ stage.display_name }} on task #{{ task.id }}",
            LoopBackStageId = "task-review",
            MaxRetries = 5,
            TimeoutMinutes = 45
        };

        var definition = new StageDefinition
        {
            StageId = "custom-review",
            DisplayName = "Custom Architecture Review",
            Kind = WorkflowStageKind.Agentic,
            CsharpImplementationType = "agentic",
            ConfigurationJson = JsonSerializer.Serialize(agenticConfig)
        };

        var stage = _factory.CreateStage(definition);

        Assert.IsType<GenericAgenticStage>(stage);
        var generic = (GenericAgenticStage)stage;
        Assert.Equal("custom-review", generic.StageId);
        Assert.Equal("Custom Architecture Review", generic.DisplayName);
        Assert.Equal(WorkflowStageKind.Agentic, generic.Kind);
        Assert.Equal(AgentMode.ReadOnly, generic.RequiredMode);
        Assert.Equal("Review the architecture and ask questions.", generic.StageDirective);
        Assert.Equal("Execute {{ stage.display_name }} on task #{{ task.id }}", generic.PromptTemplate);
        Assert.Equal("architect-agent", generic.AgentProfileId);
        Assert.Equal("task-review", generic.LoopBackStageId);
        Assert.Equal(5, generic.MaxRetries);
        Assert.Equal(45, generic.TimeoutMinutes);
    }

    [Theory]
    [InlineData("deterministic-build", typeof(DeterministicBuildStage))]
    [InlineData("deterministic-test", typeof(DeterministicTestStage))]
    [InlineData("deterministic-test-with-coverage", typeof(DeterministicTestWithCoverageGate))]
    [InlineData("deterministic-sast", typeof(DeterministicSastStage))]
    [InlineData("deterministic-ratchet", typeof(DeterministicWarningRatchetStage))]
    [InlineData("deterministic-composite", typeof(CompositeDeterministicStage))]
    [InlineData("deterministic-script", typeof(DeterministicScriptStage))]
    public void CreateStage_DeterministicBuiltInTypes_InstantiatesCorrectClasses(string implementationType, Type expectedType)
    {
        var definition = new StageDefinition
        {
            StageId = implementationType,
            DisplayName = $"Test {implementationType}",
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = implementationType,
            ConfigurationJson = "{}"
        };

        var stage = _factory.CreateStage(definition);

        Assert.IsType(expectedType, stage);
    }

    [Fact]
    public void CreateStage_UnknownImplementationType_ThrowsInvalidOperationExceptionWithHelpfulListing()
    {
        var definition = new StageDefinition
        {
            StageId = "unknown-gate",
            DisplayName = "Unknown Gate",
            CsharpImplementationType = "non-existent-type"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => _factory.CreateStage(definition));

        Assert.Contains("Unknown stage C# implementation type 'non-existent-type'", ex.Message);
        Assert.Contains("agentic", ex.Message);
        Assert.Contains("deterministic-build", ex.Message);
    }

    [Fact]
    public void RegisterHandler_CustomHandler_ResolvesSuccessfully()
    {
        _registry.RegisterHandler("custom-mock-stage", (sp, def) =>
            new GenericAgenticStage(def.StageId, def.DisplayName, new AgenticStageConfig { StageDirective = "Custom" }, sp.GetRequiredService<IPromptTemplateRenderer>(), sp.GetRequiredService<TimeProvider>()));

        var definition = new StageDefinition
        {
            StageId = "my-custom-stage",
            DisplayName = "My Custom Stage",
            CsharpImplementationType = "custom-mock-stage"
        };

        var stage = _factory.CreateStage(definition);

        Assert.IsType<GenericAgenticStage>(stage);
        Assert.Equal("my-custom-stage", stage.StageId);
        Assert.Equal("Custom", stage.StageDirective);
    }

    [Fact]
    public async Task CompositeDeterministicStage_ThrowsIfSubStageIsNotDeterministic()
    {
        var customRegistry = new WorkflowStageRegistry();
        // Register an agentic handler under a sub-stage name
        customRegistry.RegisterHandler("deterministic-not-really", (sp, def) =>
            new GenericAgenticStage(def.StageId, def.DisplayName, new AgenticStageConfig(), sp.GetRequiredService<IPromptTemplateRenderer>(), sp.GetRequiredService<TimeProvider>()));

        var config = new DeterministicStageConfig
        {
            ExtraParameters = new Dictionary<string, string> { { "Stages", "not-really" } }
        };

        var definition = new StageDefinition
        {
            StageId = "bad-composite",
            DisplayName = "Bad Composite",
            CsharpImplementationType = "deterministic-composite",
            ConfigurationJson = JsonSerializer.Serialize(config)
        };

        var stage = new CompositeDeterministicStage(definition, _serviceProvider, customRegistry);
        var pipelineContext = new PipelineContext(
            workspace: new Workspace { RepositoryPath = "C:\\dummy\\repo" },
            task: new ForgeTask { Id = 1, Title = "Test Task" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(pipelineContext, CancellationToken.None));
        Assert.Contains("can only execute deterministic sub-stages", ex.Message);
    }

    [Fact]
    public async Task CompositeDeterministicStage_ThrowsIfSubStageIsUnknown()
    {
        var config = new DeterministicStageConfig
        {
            ExtraParameters = new Dictionary<string, string> { { "Stages", "unregistered-substage" } }
        };

        var definition = new StageDefinition
        {
            StageId = "unknown-composite",
            DisplayName = "Unknown Composite",
            CsharpImplementationType = "deterministic-composite",
            ConfigurationJson = JsonSerializer.Serialize(config)
        };

        var stage = new CompositeDeterministicStage(definition, _serviceProvider, _registry);
        var pipelineContext = new PipelineContext(
            workspace: new Workspace { RepositoryPath = "C:\\dummy\\repo" },
            task: new ForgeTask { Id = 1, Title = "Test Task" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(pipelineContext, CancellationToken.None));
        Assert.Contains("Unknown deterministic sub-stage 'unregistered-substage'", ex.Message);
    }

    [Fact]
    public async Task CompositeDeterministicStage_WhenSubStageFailsAndFailOnErrorIsTrue_ReturnsFailure()
    {
        var sp = new TestServiceProvider();
        var fakeEnv1 = new FakeExecutionEnvironment
        {
            Result = new CommandResult(1, string.Empty, "error CS1002: ; expected [proj.csproj]", TimeSpan.FromMilliseconds(10))
        };
        sp.AddService<IExecutionEnvironment>(fakeEnv1);
        sp.AddService<IExecutionEnvironmentProvider>(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(fakeEnv1));
        sp.AddService<IOllamaModelPreloadService>(new NightCodeForge.Core.Tests.Fixtures.FakePreloadService());
        sp.AddService<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(new NightCodeForge.Core.Execution.BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));

        var config = new DeterministicStageConfig
        {
            Action = "CompositeVerification",
            FailOnError = true,
            LoopBackStageId = "implementation",
            ExtraParameters = new Dictionary<string, string> { { "Stages", "build" } }
        };

        var definition = new StageDefinition
        {
            StageId = "composite-gate",
            DisplayName = "Composite Gate",
            CsharpImplementationType = "deterministic-composite",
            ConfigurationJson = JsonSerializer.Serialize(config)
        };

        var stage = new CompositeDeterministicStage(definition, sp, _registry);
        var pipelineContext = new PipelineContext(
            workspace: new Workspace { RepositoryPath = "C:\\dummy\\repo" },
            task: new ForgeTask { Id = 1, Title = "Test Task" });

        var result = await stage.ExecuteAsync(pipelineContext, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", result.LoopBackStageId);
    }

    [Fact]
    public async Task CompositeDeterministicStage_WhenFailOnErrorIsFalse_ReturnsSuccessEvenIfSubStageFails()
    {
        var sp = new TestServiceProvider();
        var fakeEnv2 = new FakeExecutionEnvironment
        {
            Result = new CommandResult(1, string.Empty, "error CS1002: ; expected [proj.csproj]", TimeSpan.FromMilliseconds(10))
        };
        sp.AddService<IExecutionEnvironment>(fakeEnv2);
        sp.AddService<IExecutionEnvironmentProvider>(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(fakeEnv2));
        sp.AddService<IOllamaModelPreloadService>(new NightCodeForge.Core.Tests.Fixtures.FakePreloadService());
        sp.AddService<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(new NightCodeForge.Core.Execution.BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));

        var config = new DeterministicStageConfig
        {
            Action = "CompositeBaseline",
            FailOnError = false,
            LoopBackStageId = "implementation",
            ExtraParameters = new Dictionary<string, string> { { "Stages", "build" } }
        };

        var definition = new StageDefinition
        {
            StageId = "composite-baseline",
            DisplayName = "Composite Baseline",
            CsharpImplementationType = "deterministic-composite",
            ConfigurationJson = JsonSerializer.Serialize(config)
        };

        var stage = new CompositeDeterministicStage(definition, sp, _registry);
        var pipelineContext = new PipelineContext(
            workspace: new Workspace { RepositoryPath = "C:\\dummy\\repo" },
            task: new ForgeTask { Id = 1, Title = "Test Task" });

        var result = await stage.ExecuteAsync(pipelineContext, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
