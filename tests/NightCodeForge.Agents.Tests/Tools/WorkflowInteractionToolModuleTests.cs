using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class WorkflowInteractionToolModuleTests
{
    [Fact]
    public void Constructors_And_IAgentToolModuleContract_ExposeInterruptionControllerAndFunctions()
    {
        var defaultModule = new WorkflowInteractionToolModule();
        Assert.Null(defaultModule.InterruptionController);

        Assert.Throws<ArgumentNullException>(() => new WorkflowInteractionToolModule(null!));

        using var controller = new ToolTurnInterruptionController();
        var moduleWithController = new WorkflowInteractionToolModule(controller);
        Assert.Same(controller, moduleWithController.InterruptionController);

        IAgentToolModule asInterface = defaultModule;
        asInterface.InterruptionController = controller;
        Assert.Same(controller, defaultModule.InterruptionController);

        var functions = defaultModule.CreateFunctions();
        Assert.Equal([CanonicalToolNames.Ask, CanonicalToolNames.RequestChanges], functions.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task CreateFunctions_SchemaOnlyWithNullModule_ThrowsMissingModuleInstanceMessageOnInvoke()
    {
        var functions = WorkflowInteractionToolModule.CreateFunctions(module: null);
        var askFn = functions.Single(f => f.Name == CanonicalToolNames.Ask);
        var rcFn = functions.Single(f => f.Name == CanonicalToolNames.RequestChanges);

        var askEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await askFn.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(WorkflowInteractionToolModule.MissingModuleInstanceMessage, askEx.Message);

        var rcEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rcFn.InvokeAsync(new AIFunctionArguments
            {
                ["feedback"] = "Needs tests",
                ["reason"] = "Coverage"
            }));
        Assert.Equal(WorkflowInteractionToolModule.MissingModuleInstanceMessage, rcEx.Message);
    }

    [Fact]
    public async Task AskAsync_ValidatesEmptyAndBlankQuestions_BeforeInvokingHandlersOrThrowing()
    {
        var module = new WorkflowInteractionToolModule();

        var nullResult = await module.AskAsync(null);
        Assert.StartsWith("Error: At least one question must be provided", nullResult);

        var emptyResult = await module.AskAsync([]);
        Assert.StartsWith("Error: At least one question must be provided", emptyResult);

        var blankPromptResult = await module.AskAsync(
        [
            new QuestionItem("Valid question?"),
            new QuestionItem("   ")
        ]);
        Assert.Equal("Error: Question at index 1 has a blank or empty prompt.", blankPromptResult);
    }

    [Fact]
    public async Task AskAsync_SupportsAskHandler_InterruptionController_AndFallbackException()
    {
        var questions = new[] { new QuestionItem("Use gRPC or REST?", "Transport protocol", ["gRPC", "REST"]) };
        var module = new WorkflowInteractionToolModule();

        // 1. Neither AskHandler nor InterruptionController -> throws HumanInterruptionException
        var domainEx = await Assert.ThrowsAsync<HumanInterruptionException>(() => module.AskAsync(questions));
        Assert.Single(domainEx.Questions);
        Assert.Equal("Use gRPC or REST?", domainEx.Questions[0].Prompt);

        // 2. InterruptionController set (no AskHandler) -> triggers HumanClarificationInterruption and throws OperationCanceledException
        using var controller = new ToolTurnInterruptionController();
        module.InterruptionController = controller;
        await Assert.ThrowsAsync<OperationCanceledException>(() => module.AskAsync(questions));
        var captured = Assert.IsType<HumanClarificationInterruption>(controller.CapturedInterruption);
        Assert.Single(captured.Questions);
        Assert.Equal("Use gRPC or REST?", captured.Questions[0].Prompt);

        // 3. AskHandler set -> executes in-place without triggering InterruptionController
        using var controller2 = new ToolTurnInterruptionController();
        module.InterruptionController = controller2;
        module.AskHandler = (qs, _) => Task.FromResult($"Answered: {qs[0].Prompt} = gRPC");

        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);
        var fnResult = await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = questions });
        Assert.Equal("Answered: Use gRPC or REST? = gRPC", fnResult?.ToString());
        Assert.False(controller2.HasInterruption);
    }

    [Fact]
    public async Task RequestChanges_InvokesCallback_AndSupportsControllerOrFallbackException()
    {
        var module = new WorkflowInteractionToolModule();
        string? callbackFeedback = null;
        string? callbackReason = null;
        module.RequestChangesHandler = (fb, r) =>
        {
            callbackFeedback = fb;
            callbackReason = r;
        };

        // 1. Without InterruptionController -> invokes callback and throws RequestChangesException
        var rcEx = Assert.Throws<RequestChangesException>(() => module.RequestChanges("Add null check", "NRE risk"));
        Assert.Equal("Add null check", rcEx.Feedback);
        Assert.Equal("NRE risk", rcEx.Reason);
        Assert.Equal("Add null check", callbackFeedback);
        Assert.Equal("NRE risk", callbackReason);

        // 2. With InterruptionController via AIFunction -> invokes callback and unwinds with ReviewerChangesRequestedInterruption
        using var controller = new ToolTurnInterruptionController();
        module.InterruptionController = controller;
        var rcFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.RequestChanges);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await rcFn.InvokeAsync(new AIFunctionArguments
            {
                ["feedback"] = "Fix SQL injection",
                ["reason"] = "Security"
            }));

        Assert.Equal("Fix SQL injection", callbackFeedback);
        Assert.Equal("Security", callbackReason);
        var captured = Assert.IsType<ReviewerChangesRequestedInterruption>(controller.CapturedInterruption);
        Assert.Equal("Fix SQL injection", captured.Feedback);
        Assert.Equal("Security", captured.Reason);
    }

    [Fact]
    public async Task AskAIFunction_AcceptsObjectOptionsAndMixedPayloads_AndPreservesJsonSchema()
    {
        var module = new WorkflowInteractionToolModule();
        IReadOnlyList<QuestionItem>? captured = null;
        module.AskHandler = (qs, _) =>
        {
            captured = qs;
            return Task.FromResult("ok");
        };

        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);

        var schemaText = askFn.JsonSchema.GetRawText();
        Assert.Contains("questions", schemaText);
        Assert.Contains("prompt", schemaText);
        Assert.Contains("rationale", schemaText);
        Assert.Contains("options", schemaText);

        using var doc = JsonDocument.Parse("""
        [
            {
                "prompt": "Timeout?",
                "rationale": "Resilience",
                "options": [
                    { "label": "5 seconds", "description": "5000 ms" },
                    "10 seconds"
                ]
            }
        ]
        """);

        var result = await askFn.InvokeAsync(new AIFunctionArguments
        {
            ["questions"] = doc.RootElement
        });

        Assert.Equal("ok", result?.ToString());
        Assert.NotNull(captured);
        var q = Assert.Single(captured);
        Assert.Equal("Timeout?", q.Prompt);
        Assert.Equal("Resilience", q.Rationale);
        Assert.Equal(["5 seconds — 5000 ms", "10 seconds"], q.Options);
    }

    [Fact]
    public async Task AskAIFunction_OnInvalidBindingArguments_ReturnsActionableDiagnosticInsteadOfFunctionFailed()
    {
        var module = new WorkflowInteractionToolModule();
        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);

        // 1. Direct AIFunction invocation with wrong scalar type for 'questions'
        var directResult = await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = 42 });
        var directText = directResult?.ToString() ?? string.Empty;
        Assert.StartsWith("Error: Invalid arguments for tool 'ask'", directText);
        Assert.Contains("questions", directText);
        Assert.Contains("prompt", directText);

        // 2. End-to-end through FunctionInvokingChatClient (must NOT collapse to "Error: Function failed.")
        var fakeInner = new SingleToolCallChatClient(
            new FunctionCallContent(
                "call-bad-ask",
                CanonicalToolNames.Ask,
                new Dictionary<string, object?>
                {
                    ["questions"] = JsonDocument.Parse("[123]").RootElement
                }));
        using var invokingClient = new FunctionInvokingChatClient(fakeInner);
        var response = await invokingClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Clarify")],
            new ChatOptions { Tools = [askFn] });

        var toolResult = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        var toolResultText = toolResult.Result?.ToString() ?? string.Empty;

        Assert.DoesNotContain("Error: Function failed.", toolResultText);
        Assert.StartsWith("Error: Invalid arguments for tool 'ask'", toolResultText);
        Assert.Contains("questions", toolResultText);
        Assert.Contains("Expected parameters:", toolResultText);
    }

    [Fact]
    public async Task DiagnosticBindingAIFunction_PropagatesCancellationAndDomainExceptions()
    {
        var cancellingFn = DiagnosticBindingAIFunction.Wrap(AIFunctionFactory.Create(
            (string input) =>
            {
                throw new OperationCanceledException("Cancelled by controller");
            },
            "cancel_test",
            "Test cancellation propagation"));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cancellingFn.InvokeAsync(new AIFunctionArguments { ["input"] = "x" }));
    }

    [Fact]
    public async Task DiagnosticBindingAIFunction_OnReadWithInvalidOffset_IdentifiesOffsetInsteadOfPath()
    {
        var readFn = CanonicalAIFunctionFactory.CreateAllCanonicalFunctions(tools: null)
            .Single(f => f.Name == CanonicalToolNames.Read);

        using var offsetDoc = JsonDocument.Parse("\"ten\"");
        var result = await readFn.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "Program.cs",
            ["offset"] = offsetDoc.RootElement
        });

        var text = result?.ToString() ?? string.Empty;
        Assert.StartsWith("Error: Invalid arguments for tool 'read' (parameter 'offset'", text);
        Assert.DoesNotContain("(parameter 'path'", text);
    }

    [Fact]
    public async Task DiagnosticBindingAIFunction_OnMissingRequiredParameter_IdentifiesMissingParameterName()
    {
        var module = new WorkflowInteractionToolModule();
        var rcFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.RequestChanges);

        var result = await rcFn.InvokeAsync(new AIFunctionArguments
        {
            ["reason"] = "Coverage"
        });

        var text = result?.ToString() ?? string.Empty;
        Assert.StartsWith("Error: Invalid arguments for tool 'request_changes' (parameter 'feedback')", text);
        Assert.DoesNotContain("(parameter 'arguments')", text);
    }

    [Fact]
    public async Task DiagnosticBindingAIFunction_PropagatesExceptionsThrownInsideToolBody()
    {
        var argExFn = DiagnosticBindingAIFunction.Wrap(AIFunctionFactory.Create(
            (string input) =>
            {
                throw new ArgumentNullException("internalParam", "Internal null guard");
            },
            "internal_arg_null",
            "Test internal ArgumentNullException"));

        var jsonExFn = DiagnosticBindingAIFunction.Wrap(AIFunctionFactory.Create(
            (string input) =>
            {
                throw new JsonException("Malformed internal payload");
            },
            "internal_json_ex",
            "Test internal JsonException"));

        var notSupportedFn = DiagnosticBindingAIFunction.Wrap(AIFunctionFactory.Create(
            (string input) =>
            {
                throw new NotSupportedException("Unsupported internal operation");
            },
            "internal_not_supported",
            "Test internal NotSupportedException"));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await argExFn.InvokeAsync(new AIFunctionArguments { ["input"] = "ok" }));
        await Assert.ThrowsAsync<JsonException>(async () =>
            await jsonExFn.InvokeAsync(new AIFunctionArguments { ["input"] = "ok" }));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await notSupportedFn.InvokeAsync(new AIFunctionArguments { ["input"] = "ok" }));

        // Also verify propagation inside AskHandler after LenientAskAIFunction normalizes object options.
        var module = new WorkflowInteractionToolModule
        {
            AskHandler = (_, _) => throw new ArgumentException("Handler validation bug", "handlerArg")
        };
        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);
        using var doc = JsonDocument.Parse("""[{"prompt": "Pick?", "options": [{"label": "A", "description": "First"}]}]""");

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = doc.RootElement }));
        Assert.Equal("handlerArg", ex.ParamName);
    }

    [Fact]
    public void DiagnosticBindingAIFunction_Wrap_DoesNotDoubleWrapDirectOrDelegatedFunctions()
    {
        var module = new WorkflowInteractionToolModule();
        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);
        var rcFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.RequestChanges);

        Assert.Same(askFn, DiagnosticBindingAIFunction.Wrap(askFn));
        Assert.Same(rcFn, DiagnosticBindingAIFunction.Wrap(rcFn));
        Assert.NotNull(askFn.GetService<DiagnosticBindingAIFunction>());
    }

    [Fact]
    public async Task AskAIFunction_WithCaseMismatchedQuestionsKey_NormalizesAndPreservesContextAndServices()
    {
        var module = new WorkflowInteractionToolModule();
        IReadOnlyList<QuestionItem>? captured = null;
        module.AskHandler = (qs, _) =>
        {
            captured = qs;
            return Task.FromResult("ok");
        };

        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);
        var sp = new DummyServiceProvider();
        var ctx = new Dictionary<object, object?> { ["traceId"] = "abc-123" };

        // 1. JsonElement with capitalized "Questions"
        using var doc = JsonDocument.Parse("""[{"prompt": "Target?", "options": [{"label": "Dev", "description": "Local"}]}]""");
        var args1 = new AIFunctionArguments
        {
            Services = sp,
            Context = ctx,
            ["Questions"] = doc.RootElement
        };
        var res1 = await askFn.InvokeAsync(args1);
        Assert.Equal("ok", res1?.ToString());
        Assert.NotNull(captured);
        Assert.Equal("Target?", Assert.Single(captured).Prompt);
        Assert.Equal(["Dev — Local"], captured[0].Options);

        // 2. Typed List<QuestionItem> with capitalized "Questions"
        captured = null;
        var args2 = new AIFunctionArguments
        {
            Services = sp,
            Context = ctx,
            ["Questions"] = new List<QuestionItem> { new("Typed?", null, ["Yes", "No"]) }
        };
        var res2 = await askFn.InvokeAsync(args2);
        Assert.Equal("ok", res2?.ToString());
        Assert.NotNull(captured);
        Assert.Equal("Typed?", Assert.Single(captured).Prompt);
        Assert.Equal(["Yes", "No"], captured[0].Options);
    }

    [Fact]
    public async Task AskAIFunction_WithBlankPromptAndObjectOptions_ReportsBlankPromptIndexWithoutDroppingQuestions()
    {
        var module = new WorkflowInteractionToolModule();
        bool handlerCalled = false;
        module.AskHandler = (_, _) =>
        {
            handlerCalled = true;
            return Task.FromResult("unexpected");
        };

        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);

        // 1. Batch where question 0 has blank prompt + object options and question 1 is valid: must NOT drop question 0
        using var batchDoc = JsonDocument.Parse("""
        [
            {
                "prompt": "   ",
                "rationale": "Timeout budget",
                "options": [{ "label": "5s", "description": "5000 ms" }]
            },
            {
                "prompt": "Which database?",
                "options": ["SQLite", "Postgres"]
            }
        ]
        """);

        var batchResult = await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = batchDoc.RootElement });
        Assert.Equal("Error: Question at index 0 has a blank or empty prompt.", batchResult?.ToString());
        Assert.False(handlerCalled);

        // 2. Schema-only null module with object options still throws MissingModuleInstanceMessage
        var schemaOnlyAskFn = WorkflowInteractionToolModule.CreateFunctions(module: null)
            .Single(f => f.Name == CanonicalToolNames.Ask);
        using var validObjectOptionsDoc = JsonDocument.Parse("""
        [{"prompt": "Timeout?", "options": [{"label": "5s", "description": "5000 ms"}]}]
        """);
        var missingEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await schemaOnlyAskFn.InvokeAsync(new AIFunctionArguments { ["questions"] = validObjectOptionsDoc.RootElement }));
        Assert.Equal(WorkflowInteractionToolModule.MissingModuleInstanceMessage, missingEx.Message);
    }

    [Fact]
    public async Task AskAIFunction_WithMixedValidAndInvalidArrayElements_ReturnsBindingDiagnosticWithPath()
    {
        var module = new WorkflowInteractionToolModule();
        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);

        var clrMixedList = new List<object?>
        {
            new Dictionary<string, object?> { ["prompt"] = "Valid question?" },
            123
        };

        var result = await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = clrMixedList });
        var text = result?.ToString() ?? string.Empty;

        Assert.StartsWith("Error: Invalid arguments for tool 'ask' (parameter 'questions', path '$[1]')", text);
        Assert.DoesNotContain("System.Collections.Generic.List`1", text);
    }

    [Fact]
    public async Task DiagnosticBindingAIFunction_ResolvesLocalSchemaRefsInExpectedShape()
    {
        var refSchemaFn = DiagnosticBindingAIFunction.Wrap(new CustomRefSchemaAIFunction());

        var result = await refSchemaFn.InvokeAsync(new AIFunctionArguments());
        var text = result?.ToString() ?? string.Empty;

        Assert.StartsWith("Error: Invalid arguments for tool 'ref_tool' (parameter 'mode')", text);
        Assert.Contains("mode: enum(fast|safe)", text);
        Assert.DoesNotContain("mode: any", text);
    }

    private sealed class CustomRefSchemaAIFunction : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "mode": { "$ref": "#/$defs/ExecutionMode" }
            },
            "required": ["mode"],
            "$defs": {
                "ExecutionMode": {
                    "type": "string",
                    "enum": ["fast", "safe"]
                }
            }
        }
        """).RootElement.Clone();

        public override string Name => "ref_tool";
        public override string Description => "Tests local $ref formatting";
        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new JsonException("Invalid mode value.");
    }

    private sealed class DummyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class SingleToolCallChatClient(FunctionCallContent call) : IChatClient
    {
        private int _turn;

        public ChatClientMetadata Metadata { get; } = new("SingleToolCallChatClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_turn++ == 0)
            {
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [call])]));
            }

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
