using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class Step920PromptStabilizerAndTemporalContextTests
{
    private class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }

    private class RecordingChatClient : IChatClient
    {
        public List<IEnumerable<ChatMessage>> CapturedTurnMessages { get; } = new();
        public List<ChatOptions?> CapturedOptions { get; } = new();

        public ChatClientMetadata Metadata => new("RecordingClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedTurnMessages.Add(chatMessages.ToList());
            CapturedOptions.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Acknowledged.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedTurnMessages.Add(chatMessages.ToList());
            CapturedOptions.Add(options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Acknowledged.");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void PromptPrefixStabilizer_Layer1AndLayer2_ByteIdenticalAcrossDifferentDates()
    {
        var stabilizer = new PromptPrefixStabilizer();

        // Simulate layer generation across two different dates
        var layer1DayA = stabilizer.BuildLayer1GlobalRules(
            projectGuidelines: "Follow C# standards",
            skillsCatalog: "<skills>\n- bash\n</skills>");
        var layer2DayA = stabilizer.BuildLayer2StagePersona("Execute task review and ask clarifying questions.");

        var layer1DayB = stabilizer.BuildLayer1GlobalRules(
            projectGuidelines: "Follow C# standards",
            skillsCatalog: "<skills>\n- bash\n</skills>");
        var layer2DayB = stabilizer.BuildLayer2StagePersona("Execute task review and ask clarifying questions.");

        Assert.Equal(layer1DayA, layer1DayB, StringComparer.Ordinal);
        Assert.Equal(layer2DayA, layer2DayB, StringComparer.Ordinal);

        var layersA = stabilizer.BuildLayers("Review directive", "<task id=\"1\" />");
        var layersB = stabilizer.BuildLayers("Review directive", "<task id=\"1\" />");

        Assert.Equal(layersA.Layer1GlobalRules, layersB.Layer1GlobalRules, StringComparer.Ordinal);
        Assert.Equal(layersA.Layer2StagePersona, layersB.Layer2StagePersona, StringComparer.Ordinal);
    }

    [Fact]
    public void PromptPrefixStabilizer_Layer1AndLayer2_ContainNoDateOrTimestampPatterns()
    {
        var stabilizer = new PromptPrefixStabilizer();
        var layer1 = stabilizer.BuildLayer1GlobalRules();
        var layer2 = stabilizer.BuildLayer2StagePersona("Execute implementation.");

        // Strict ISO-8601 date pattern (YYYY-MM-DD)
        var datePattern = new Regex(@"\b\d{4}-\d{2}-\d{2}\b");
        // Time pattern (HH:mm:ss)
        var timePattern = new Regex(@"\b\d{2}:\d{2}(?::\d{2})?\b");

        Assert.DoesNotMatch(datePattern, layer1);
        Assert.DoesNotMatch(timePattern, layer1);

        Assert.DoesNotMatch(datePattern, layer2);
        Assert.DoesNotMatch(timePattern, layer2);
    }

    [Fact]
    public void CrucibleAgentHarness_BuildTaskScope_IncludesDateAttributeWhenProvided()
    {
        var task = new ForgeTask { Id = 101, Title = "Add Multiply Feature", Description = "Implement multiplier service." };

        var taskScope = CrucibleAgentHarness.BuildTaskScope(task, "docs/tasks/task-101.md", "2026-10-18");

        Assert.Contains("date=\"2026-10-18\"", taskScope);
        Assert.Contains("id=\"101\"", taskScope);
        Assert.Contains("title=\"Add Multiply Feature\"", taskScope);
        Assert.Contains("path=\"docs/tasks/task-101.md\"", taskScope);
    }

    [Fact]
    public void CrucibleAgentHarness_BuildTaskScope_OmitsDateAttributeWhenNullOrWhitespace()
    {
        var task = new ForgeTask { Id = 102, Title = "Refactor Math", Description = "Clean up math code." };

        var taskScopeNull = CrucibleAgentHarness.BuildTaskScope(task, "docs/tasks/task-102.md", null);
        var taskScopeEmpty = CrucibleAgentHarness.BuildTaskScope(task, "docs/tasks/task-102.md", "  ");

        Assert.DoesNotContain("date=", taskScopeNull);
        Assert.DoesNotContain("date=", taskScopeEmpty);
    }

    [Fact]
    public async Task CrucibleAgentHarness_RunStageAsync_PassesCurrentDateFromTimeProviderToTaskScope()
    {
        var recordingClient = new RecordingChatClient();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = recordingClient,
            PathResolver = TestPathResolver.Create(),
            TimeProvider = fakeTime
        }.Build();

        var task = new ForgeTask { Id = 205, Title = "Date Injection Test", Description = "Check date attribute" };
        var workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() };
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = workspace,
            Profile = new AgentProfile { Id = "test-agent", Mode = AgentMode.Full },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            WorkspacePath = Directory.GetCurrentDirectory(),
            TaskMarkdownPath = "docs/tasks/2026-10-18-feature.md"
        };

        var result = await harness.RunStageAsync(context);

        Assert.NotNull(result);
        Assert.NotEmpty(recordingClient.CapturedTurnMessages);

        // Verify that the prompt sent to the LLM (which includes the system instructions/prefix) contains the task date
        var effectiveInstructions = recordingClient.CapturedOptions.FirstOrDefault()?.Instructions
            ?? recordingClient.CapturedTurnMessages.FirstOrDefault()?.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        Assert.NotNull(effectiveInstructions);
        Assert.Contains("date=\"2026-10-18\"", effectiveInstructions);
    }

    [Fact]
    public async Task IdeationService_ConverseAsync_InjectsCurrentDateIntoInitialUserPrompt()
    {
        var recordingClient = new RecordingChatClient();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 15, 0, TimeSpan.Zero));

        var ideationService = new TestIdeationServiceBuilder
        {
            BaseChatClient = recordingClient,
            PathResolver = TestPathResolver.Create(),
            TimeProvider = fakeTime
        }.Build();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "IdeationWS",
            RepositoryPath = Directory.GetCurrentDirectory()
        };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(1));

        var result = await ideationService.ConverseAsync(
            workspace,
            "Please explore the codebase and propose backlog tasks.");

        Assert.NotNull(result);
        Assert.NotEmpty(recordingClient.CapturedTurnMessages);

        var firstTurnMessages = recordingClient.CapturedTurnMessages[0];
        var userMessage = firstTurnMessages.FirstOrDefault(m => m.Role == ChatRole.User);
        Assert.NotNull(userMessage);

        var text = userMessage.Text ?? string.Empty;
        Assert.StartsWith("Please explore the codebase and propose backlog tasks.", text);
        Assert.Contains("[Context]:", text);
        Assert.Contains("- Current Date: 2026-10-19", text);
        // Verify no timestamps (no 09:15)
        Assert.DoesNotContain("09:15", text);
    }

    [Fact]
    public async Task IdeationService_ConverseAsync_DoesNotDuplicateDate_WhenConversationHistoryAlreadyContainsDate()
    {
        var recordingClient = new RecordingChatClient();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 15, 0, TimeSpan.Zero));

        var ideationService = new TestIdeationServiceBuilder
        {
            BaseChatClient = recordingClient,
            PathResolver = TestPathResolver.Create(),
            TimeProvider = fakeTime
        }.Build();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "IdeationWS",
            RepositoryPath = Directory.GetCurrentDirectory()
        };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(1));

        var history = new List<(string Role, string Content)>
        {
            ("user", "Hello agent\n\n[Context]:\n- Current Date: 2026-10-19"),
            ("assistant", "Hello! How can I help you plan tasks today?")
        };

        var result = await ideationService.ConverseAsync(
            workspace,
            "Now please propose a caching task.",
            conversationHistory: history);

        Assert.NotNull(result);
        Assert.NotEmpty(recordingClient.CapturedTurnMessages);

        var turnMessages = recordingClient.CapturedTurnMessages[0];
        // The last message is the new user prompt
        var lastUserMsg = turnMessages.LastOrDefault(m => m.Role == ChatRole.User);
        Assert.NotNull(lastUserMsg);

        // Turn 2 prompt should NOT have another duplicate [Context]:\n- Current Date: block appended
        Assert.Equal("Now please propose a caching task.", lastUserMsg.Text);
    }

    [Fact]
    public void WorkflowStageRegistry_AgenticHandler_WithoutTimeProvider_ThrowsInvalidOperationException()
    {
        var registry = new WorkflowStageRegistry();
        var spMock = new Moq.Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(NightCodeForge.Core.Workflow.IPromptTemplateRenderer)))
            .Returns(new NightCodeForge.Core.Workflow.ScribanPromptTemplateRenderer());
        spMock.Setup(sp => sp.GetService(typeof(TimeProvider)))
            .Returns((TimeProvider?)null);

        var stageFactory = new NightCodeForge.Core.Workflow.WorkflowStageFactory(registry, spMock.Object);
        var def = new StageDefinition
        {
            StageId = "test-agentic",
            DisplayName = "Test Agentic",
            CsharpImplementationType = "agentic"
        };

        Assert.Throws<InvalidOperationException>(() => stageFactory.CreateStage(def));
    }

    [Fact]
    public async Task IdeationService_ConverseAsync_InjectsUnderExistingContextBlock_WhenUserPromptContainsContextBlock()
    {
        var recordingClient = new RecordingChatClient();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 15, 0, TimeSpan.Zero));

        var ideationService = new TestIdeationServiceBuilder
        {
            BaseChatClient = recordingClient,
            PathResolver = TestPathResolver.Create(),
            TimeProvider = fakeTime
        }.Build();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "IdeationWS",
            RepositoryPath = Directory.GetCurrentDirectory()
        };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(1));

        var promptWithContext = "Investigate auth system.\n\n[Context]:\n- Priority: High\n\n[Requirements]:\nMust support OAuth2.";
        var result = await ideationService.ConverseAsync(workspace, promptWithContext);

        Assert.NotNull(result);
        Assert.NotEmpty(recordingClient.CapturedTurnMessages);

        var firstTurnMessages = recordingClient.CapturedTurnMessages[0];
        var userMessage = firstTurnMessages.FirstOrDefault(m => m.Role == ChatRole.User);
        Assert.NotNull(userMessage);

        var text = userMessage.Text ?? string.Empty;
        var contextIdx = text.IndexOf("[Context]:", StringComparison.OrdinalIgnoreCase);
        var dateIdx = text.IndexOf("- Current Date: 2026-10-19", StringComparison.OrdinalIgnoreCase);
        var reqIdx = text.IndexOf("[Requirements]:", StringComparison.OrdinalIgnoreCase);

        Assert.True(contextIdx >= 0);
        Assert.True(dateIdx > contextIdx);
        Assert.True(reqIdx > dateIdx);
    }
}
