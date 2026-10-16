using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Agents.Tests;

public class CodeReviewLoopbackAndPromptOverrideTests
{
    [Fact]
    public void CanonicalTools_RequestChanges_ThrowsRequestChangesException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tools-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tools = new CanonicalTools(tempDir, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());
            var ex = Assert.Throws<RequestChangesException>(() => tools.RequestChanges("Feature incomplete", "Missing tests"));
            Assert.Equal("Feature incomplete", ex.Feedback);
            Assert.Equal("Missing tests", ex.Reason);
            Assert.Equal("implementation", ex.TargetStageId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LenientToolDispatcher_DispatchesRequestChanges_AndAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dispatcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tools = new CanonicalTools(tempDir, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());
            var dispatcher = new LenientToolDispatcher(tools);

            // Test canonical name
            var ex1 = await Assert.ThrowsAsync<RequestChangesException>(() =>
                dispatcher.DispatchAsync("request_changes", "{\"feedback\": \"Re-run tests\"}"));
            Assert.Equal("Re-run tests", ex1.Feedback);

            // Test alias reject_implementation
            var ex2 = await Assert.ThrowsAsync<RequestChangesException>(() =>
                dispatcher.DispatchAsync("reject_implementation", "{\"feedback\": \"Missing Auth module\", \"reason\": \"No code written\"}"));
            Assert.Equal("Missing Auth module", ex2.Feedback);
            Assert.Equal("No code written", ex2.Reason);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WorkspacePromptResolver_DiscoversPromptsAndDirectives_FromNightCodeForgeDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "prompt-override-test-" + Guid.NewGuid().ToString("N"));
        var promptsDir = Path.Combine(tempDir, ".nightcodeforge", "prompts");
        Directory.CreateDirectory(promptsDir);

        try
        {
            File.WriteAllText(Path.Combine(promptsDir, "code-review-fix.md"), "Custom Review Directive for Team");
            File.WriteAllText(Path.Combine(promptsDir, "system.md"), "Custom System Persona for Team");
            File.WriteAllText(Path.Combine(promptsDir, "implementation.bootstrap.md"), "Custom Bootstrap Prompt for Impl");

            var directive = WorkspacePromptResolver.ResolveStageDirective(tempDir, "code-review-fix", "Default Directive");
            Assert.Equal("Custom Review Directive for Team", directive);

            var systemPrompt = WorkspacePromptResolver.ResolveSystemPrompt(tempDir, "Default System");
            Assert.Equal("Custom System Persona for Team", systemPrompt);

            var bootstrap = WorkspacePromptResolver.ResolveBootstrapPromptOverride(tempDir, "implementation");
            Assert.Equal("Custom Bootstrap Prompt for Impl", bootstrap);

            // Fallbacks when file doesn't exist
            var fallbackDirective = WorkspacePromptResolver.ResolveStageDirective(tempDir, "nonexistent", "Default Directive");
            Assert.Equal("Default Directive", fallbackDirective);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private class MockChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>> _streamHandler;

        public MockChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>> streamHandler)
        {
            _streamHandler = streamHandler;
        }

        public ChatClientMetadata Metadata => new("mock", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return _streamHandler(chatMessages, options);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenRequestChangesToolInvoked_ReturnsLoopbackToImplementation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-req-change-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = new List<AIContent>
                    {
                        new FunctionCallContent("call_1", "request_changes", new Dictionary<string, object?>
                        {
                            ["feedback"] = "Missing unit tests for edge cases",
                            ["reason"] = "Incomplete implementation"
                        })
                    }
                };
                await Task.CompletedTask;
            }

            var mockClient = new MockChatClient(StreamGen);
            var harness = new CrucibleAgentHarness(mockClient, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());

            var task = new ForgeTask { Id = 1, Title = "Test ForgeTask", Description = "Test" };
            var bag = new PipelineDataBag();
            var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 })
            {
                DataBag = bag
            };

            var context = new AgentSessionContext
            {
                ForgeTask = task,
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                Profile = new AgentProfile { Id = "reviewer", Mode = AgentMode.Review },
                StageId = "code-review-fix",
                RequiredMode = AgentMode.Review,
                WorkspacePath = tempDir,
                PipelineContext = pipelineContext
            };

            var result = await harness.RunStageAsync(context);

            Assert.Equal("implementation", result.TargetRouting);
            Assert.Contains("Missing unit tests", result.Summary);
            Assert.Equal("Missing unit tests for edge cases", bag.Get<string>("ReviewerFeedback"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenSpecialEndMessageOutput_ExtractsFeedbackAndRoutesToImplementation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-special-msg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [new TextContent("I inspected the diff.\n<request_changes>\nThe whole AuthService is missing.\n</request_changes>")]
                };
                await Task.CompletedTask;
            }

            var mockClient = new MockChatClient(StreamGen);
            var harness = new CrucibleAgentHarness(mockClient, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());

            var task = new ForgeTask { Id = 2, Title = "Test ForgeTask 2", Description = "Test" };
            var bag = new PipelineDataBag();
            var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 })
            {
                DataBag = bag
            };

            var context = new AgentSessionContext
            {
                ForgeTask = task,
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                Profile = new AgentProfile { Id = "reviewer", Mode = AgentMode.Review },
                StageId = "code-review-fix",
                RequiredMode = AgentMode.Review,
                WorkspacePath = tempDir,
                PipelineContext = pipelineContext
            };

            var result = await harness.RunStageAsync(context);

            Assert.Equal("implementation", result.TargetRouting);
            Assert.Equal("The whole AuthService is missing.", bag.Get<string>("ReviewerFeedback"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenericAgenticStage_ConfiguredForReview_RequiresReviewModeAndLoopback()
    {
        var config = new AgenticStageConfig
        {
            RequiredMode = AgentMode.Review,
            LoopBackStageId = "implementation",
            StageDirective = "You are a Principal Code Reviewer and Quality Gatekeeper.\nrequest_changes"
        };
        var stage = new GenericAgenticStage("code-review-fix", "Code Review & Fix", config);
        Assert.Equal(AgentMode.Review, stage.RequiredMode);
        Assert.Equal("implementation", stage.LoopBackStageId);
        Assert.Contains("Principal Code Reviewer", stage.StageDirective);
        Assert.Contains("request_changes", stage.StageDirective);
    }

    [Fact]
    public async Task CrucibleAgentHarness_InAgentModeFull_ProhibitsRequestChangesTool()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-full-prohibit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            int turn = 0;
            async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
            {
                turn++;
                if (turn == 1)
                {
                    yield return new ChatResponseUpdate
                    {
                        Contents = new List<AIContent>
                        {
                            new FunctionCallContent("call_1", "request_changes", new Dictionary<string, object?>
                            {
                                ["feedback"] = "I shouldn't be allowed to call this in full mode"
                            })
                        }
                    };
                }
                else
                {
                    yield return new ChatResponseUpdate
                    {
                        Contents = [new TextContent("Acknowledged security error.")]
                    };
                }
                await Task.CompletedTask;
            }

            var mockClient = new MockChatClient(StreamGen);
            var harness = new CrucibleAgentHarness(mockClient, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());

            var task = new ForgeTask { Id = 3, Title = "Test ForgeTask 3", Description = "Test" };
            var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 });

            var context = new AgentSessionContext
            {
                ForgeTask = task,
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                Profile = new AgentProfile { Id = "coder", Mode = AgentMode.Full },
                StageId = "implementation",
                RequiredMode = AgentMode.Full,
                WorkspacePath = tempDir,
                PipelineContext = pipelineContext
            };

            var result = await harness.RunStageAsync(context);

            // In Full mode, request_changes is prohibited, so it did NOT trigger loopback routing
            Assert.Null(result.TargetRouting);
            Assert.Contains("Acknowledged security error", result.Summary);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenTaskMarkdownPathPresent_IncludesPathAttributeInTaskScope()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-doc-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            List<ChatMessage>? receivedMessages = null;
            async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
            {
                receivedMessages = msgs.ToList();
                if (!string.IsNullOrEmpty(opts?.Instructions))
                {
                    receivedMessages.Insert(0, new ChatMessage(ChatRole.System, opts.Instructions));
                }
                yield return new ChatResponseUpdate
                {
                    Contents = [new TextContent("Done.")]
                };
                await Task.CompletedTask;
            }

            var mockClient = new MockChatClient(StreamGen);
            var harness = new CrucibleAgentHarness(mockClient, new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());

            var task = new ForgeTask { Id = 4, Title = "Doc Path ForgeTask", Description = "Testing doc path" };
            var bag = new PipelineDataBag();
            bag.Set("TaskMarkdownPath", "docs/tasks/2026-10-14-doc-path-task.md");
            var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 })
            {
                DataBag = bag
            };

            var context = new AgentSessionContext
            {
                ForgeTask = task,
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                Profile = new AgentProfile { Id = "coder", Mode = AgentMode.Full },
                StageId = "implementation",
                RequiredMode = AgentMode.Full,
                WorkspacePath = tempDir,
                PipelineContext = pipelineContext
            };

            await harness.RunStageAsync(context);

            Assert.NotNull(receivedMessages);
            var systemMsg = receivedMessages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
            Assert.NotNull(systemMsg);
            Assert.Contains("path=\"docs/tasks/2026-10-14-doc-path-task.md\"", systemMsg);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void StageDirectives_ReflectTaskDocumentEditing_AndTwoTierReviewPolicy()
    {
        var reviewStage = new GenericAgenticStage(
            "task-review",
            "ForgeTask Review",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Full,
                StageDirective = "1. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in docs/tasks/)... 4. Do not write or modify production code in this stage."
            });
        Assert.Contains("docs/tasks/", reviewStage.StageDirective);
        Assert.Contains("Do not write or modify production code", reviewStage.StageDirective);

        var implStage = new GenericAgenticStage(
            "implementation",
            "Implementation",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Full,
                StageDirective = "1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/..."
            });
        Assert.Contains("docs/tasks/", implStage.StageDirective);

        var codeReviewStage = new GenericAgenticStage(
            "code-review-fix",
            "Code Review & Fix",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Review,
                LoopBackStageId = "implementation",
                StageDirective = "2. TIER 1 - MINOR POLISH: ... 3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features..."
            });
        Assert.Contains("TIER 1 - MINOR POLISH", codeReviewStage.StageDirective);
        Assert.Contains("TIER 2 - REJECTION GATE", codeReviewStage.StageDirective);
        Assert.Contains("Rejection is strictly reserved for major tasks or missing features", codeReviewStage.StageDirective);
    }
}
