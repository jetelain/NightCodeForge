using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Hubs;
using NightCodeForge.Infrastructure.Telemetry;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Telemetry;

public class OperationsTelemetryServiceTests
{
    private readonly Mock<IHubContext<OrchestratorHub, IOrchestratorHubClient>> _mockHubContext;
    private readonly Mock<IOrchestratorHubClient> _mockHubClient;
    private readonly OperationsTelemetryService _service;

    public OperationsTelemetryServiceTests()
    {
        _mockHubContext = new Mock<IHubContext<OrchestratorHub, IOrchestratorHubClient>>();
        var mockClients = new Mock<IHubClients<IOrchestratorHubClient>>();
        _mockHubClient = new Mock<IOrchestratorHubClient>();

        mockClients.Setup(c => c.All).Returns(_mockHubClient.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _service = new OperationsTelemetryService(
            _mockHubContext.Object,
            NullLogger<OperationsTelemetryService>.Instance,
            maxLlmCalls: 5,
            maxShellCommands: 5);
    }

    [Fact]
    public void RecordLlmCallStarted_AndCompleted_UpdatesSnapshotAndActiveState()
    {
        var call = new LlmCallRecord(
            CallId: "call-1",
            Model: "qwen2.5-coder:32b",
            TargetBackend: "Remote",
            StartTime: DateTime.UtcNow,
            DurationMs: 0,
            PromptTokens: 100,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: "Write a unit test",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null);

        _service.RecordLlmCallStarted(call);

        var active = _service.GetActiveLlmCall();
        Assert.NotNull(active);
        Assert.Equal("call-1", active.CallId);
        Assert.Equal(LlmCallStatus.Running, active.Status);

        _service.UpdateLlmCallProgress("call-1", 50, 1000, 50.0, 25);
        var activeUpdated = _service.GetActiveLlmCall();
        Assert.NotNull(activeUpdated);
        Assert.Equal(50, activeUpdated.CompletionTokens);
        Assert.Equal(25, activeUpdated.ReasoningTokens);
        Assert.Equal(50.0, activeUpdated.TokensPerSecond);

        var completed = activeUpdated with
        {
            DurationMs = 2000,
            CompletionTokens = 100,
            Status = LlmCallStatus.Completed,
            CompletionPreview = "Done!"
        };
        _service.RecordLlmCallCompleted(completed);

        var activeAfterComplete = _service.GetActiveLlmCall();
        Assert.Null(activeAfterComplete);

        var recent = _service.GetRecentLlmCalls();
        Assert.Single(recent);
        Assert.Equal(LlmCallStatus.Completed, recent[0].Status);
    }

    [Fact]
    public void RingBuffer_BoundedCapacity_EvictsOldestRecords()
    {
        for (int i = 1; i <= 8; i++)
        {
            var call = new LlmCallRecord(
                CallId: $"call-{i}",
                Model: "model",
                TargetBackend: "Local",
                StartTime: DateTime.UtcNow.AddSeconds(i),
                DurationMs: 100,
                PromptTokens: 10,
                CompletionTokens: 10,
                TokensPerSecond: 10,
                PromptPreview: $"Prompt {i}",
                CompletionPreview: "OK",
                Status: LlmCallStatus.Completed,
                ErrorMessage: null);

            _service.RecordLlmCallCompleted(call);
        }

        var recent = _service.GetRecentLlmCalls(10);
        // Max capacity was set to 5
        Assert.Equal(5, recent.Count);
        // Newest calls are kept (8, 7, 6, 5, 4)
        Assert.Equal("call-8", recent[0].CallId);
        Assert.Equal("call-4", recent[^1].CallId);
    }

    [Fact]
    public void ShellCommands_RecordsOutput_AndBoundedEviction()
    {
        for (int i = 1; i <= 7; i++)
        {
            var cmd = new ShellCommandRecord(
                CommandId: $"cmd-{i}",
                CommandLine: $"dotnet test #{i}",
                WorkingDirectory: "/workspace",
                StartTime: DateTime.UtcNow.AddSeconds(i),
                DurationMs: 0,
                ExitCode: null,
                Status: ShellCommandStatus.Running,
                OutputPreview: null,
                ErrorPreview: null);

            _service.RecordShellCommandStarted(cmd);
            _service.UpdateShellCommandOutput($"cmd-{i}", "Passing tests...\n", false);

            var completed = cmd with
            {
                DurationMs = 500,
                ExitCode = 0,
                Status = ShellCommandStatus.Completed
            };
            _service.RecordShellCommandCompleted(completed);
        }

        var recent = _service.GetRecentShellCommands(10);
        Assert.Equal(5, recent.Count);
        Assert.Equal("cmd-7", recent[0].CommandId);
        Assert.Equal("cmd-3", recent[^1].CommandId);
    }

    [Fact]
    public void ConcurrentOperations_AreThreadSafe()
    {
        var threads = new List<Thread>();
        for (int t = 0; t < 10; t++)
        {
            var threadId = t;
            var th = new Thread(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var id = $"call-{threadId}-{i}";
                    var call = new LlmCallRecord(
                        CallId: id,
                        Model: "model",
                        TargetBackend: "Auto",
                        StartTime: DateTime.UtcNow,
                        DurationMs: 50,
                        PromptTokens: 10,
                        CompletionTokens: 20,
                        TokensPerSecond: 10,
                        PromptPreview: "test",
                        CompletionPreview: null,
                        Status: LlmCallStatus.Running,
                        ErrorMessage: null);

                    _service.RecordLlmCallStarted(call);
                    _service.UpdateLlmCallProgress(id, 25, 100, 25.0);
                    _service.RecordLlmCallCompleted(call with { Status = LlmCallStatus.Completed });

                    var active = _service.GetActiveLlmCall();
                    var recent = _service.GetRecentLlmCalls(5);
                    Assert.NotNull(recent);
                }
            });
            threads.Add(th);
        }

        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();

        var finalRecent = _service.GetRecentLlmCalls(10);
        Assert.True(finalRecent.Count <= 5);
    }
}
