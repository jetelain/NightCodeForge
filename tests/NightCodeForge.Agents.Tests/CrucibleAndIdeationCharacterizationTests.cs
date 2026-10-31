using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Pre-refactoring characterization tests locking down shared context, skills catalog,
/// dynamic context window discovery, and read-only tool scoping across
/// <see cref="Crucible.CrucibleAgentHarness"/> and <see cref="IdeationService"/>.
/// </summary>
public class CrucibleAndIdeationCharacterizationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ncf-char-" + Guid.NewGuid().ToString("N"));

    public CrucibleAndIdeationCharacterizationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // 1. Conditional Skills Catalog Injection
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrucibleAgentHarness_InjectsSkillsCatalog_IfAndOnlyIfLoadSkillPermitted(bool includeLoadSkill)
    {
        var mockSkillsService = new Mock<IWorkspaceSkillsService>();
        mockSkillsService
            .Setup(s => s.GetActiveSkillsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SkillDescriptor("my-skill", "My skill desc", "/skills/my-skill", "/skills/my-skill/SKILL.md") });
        mockSkillsService
            .Setup(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("<skills>\n- my-skill: My skill desc\n</skills>");

        var mockSkillsFactory = new Mock<IWorkspaceSkillsServiceFactory>();
        mockSkillsFactory
            .Setup(f => f.Create(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(mockSkillsService.Object);

        ChatOptions? capturedOptions = null;
        var client = new CapturingStreamingChatClient(( _, opts) =>
        {
            capturedOptions = opts;
            return [new ChatResponseUpdate(ChatRole.Assistant, "Done.")];
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            SkillsServiceFactory = mockSkillsFactory.Object,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "transcripts"))
        }.Build();

        var permittedTools = includeLoadSkill
            ? new List<string> { CanonicalToolNames.Read, CanonicalToolNames.LoadSkill }
            : new List<string> { CanonicalToolNames.Read, CanonicalToolNames.Bash };

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 401, Title = "Skills Check", Description = "Test skills catalog injection" },
            StageId = StandardStageIds.Implementation,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            WorkspacePath = _tempDir,
            Profile = new AgentProfile
            {
                Id = "dev",
                Name = "Dev",
                Mode = AgentMode.Full,
                PermittedTools = permittedTools
            }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedOptions?.Instructions);

        if (includeLoadSkill)
        {
            mockSkillsService.Verify(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains("my-skill: My skill desc", capturedOptions.Instructions);
        }
        else
        {
            mockSkillsService.Verify(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.DoesNotContain("my-skill: My skill desc", capturedOptions.Instructions);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdeationService_InjectsSkillsCatalog_IfAndOnlyIfLoadSkillPermitted(bool includeLoadSkill)
    {
        var mockSkillsService = new Mock<IWorkspaceSkillsService>();
        mockSkillsService
            .Setup(s => s.GetActiveSkillsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SkillDescriptor("ideation-skill", "Ideation skill desc", "/skills/ideation-skill", "/skills/ideation-skill/SKILL.md") });
        mockSkillsService
            .Setup(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("<skills>\n- ideation-skill: Ideation skill desc\n</skills>");

        var mockSkillsFactory = new Mock<IWorkspaceSkillsServiceFactory>();
        mockSkillsFactory
            .Setup(f => f.Create(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(mockSkillsService.Object);

        ChatOptions? capturedOptions = null;
        var client = new CapturingStreamingChatClient((_, opts) =>
        {
            capturedOptions = opts;
            return [new ChatResponseUpdate(ChatRole.Assistant, "Ideation response.")];
        });

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            SkillsServiceFactory = mockSkillsFactory.Object,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "ideation-transcripts"))
        }.Build();

        var permittedTools = includeLoadSkill
            ? new List<string> { CanonicalToolNames.Read, CanonicalToolNames.ProposeTasks, CanonicalToolNames.LoadSkill }
            : new List<string> { CanonicalToolNames.Read, CanonicalToolNames.ProposeTasks };

        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            Mode = AgentMode.ReadOnly,
            PermittedTools = permittedTools
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "WS",
            RepositoryPath = _tempDir,
            AgentProfiles = new List<AgentProfile> { profile }
        };

        var result = await service.ConverseAsync(workspace, "Explore features", agentProfile: profile);

        Assert.NotNull(result);
        Assert.NotNull(capturedOptions?.Instructions);

        if (includeLoadSkill)
        {
            mockSkillsService.Verify(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains("ideation-skill: Ideation skill desc", capturedOptions.Instructions);
        }
        else
        {
            mockSkillsService.Verify(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.DoesNotContain("ideation-skill: Ideation skill desc", capturedOptions.Instructions);
        }
    }

    // -------------------------------------------------------------------------
    // 2. Dynamic Context Window Discovery & Compaction Wiring
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CrucibleAgentHarness_WhenContextWindowNull_QueriesGatewayAndWiresCompactionStrategy()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway
            .Setup(g => g.ResolveContextWindowAsync("qwen3-coder:30b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(196608);

        var client = new CapturingStreamingChatClient((_, _) =>
            [new ChatResponseUpdate(ChatRole.Assistant, "Completed.")]);

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            GatewayService = mockGateway.Object,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "docs", "transcripts"))
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 402, Title = "Context Window Test", Description = "Verify gateway resolution" },
            StageId = StandardStageIds.Implementation,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            WorkspacePath = _tempDir,
            Profile = new AgentProfile
            {
                Id = "dev",
                Name = "Dev",
                ModelName = "qwen3-coder:30b",
                ContextWindowTokens = null
            }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        mockGateway.Verify(g => g.ResolveContextWindowAsync("qwen3-coder:30b", It.IsAny<CancellationToken>()), Times.Once);

        var transcriptFiles = Directory.GetFiles(Path.Combine(_tempDir, "docs", "transcripts"), "*-task-402-*.md");
        var transcriptText = await File.ReadAllTextAsync(Assert.Single(transcriptFiles));
        Assert.Contains("context_window_tokens: 196608", transcriptText);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenGatewayThrowsNonCancellationException_LogsWarningAndFallsBackGracefully()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway
            .Setup(g => g.ResolveContextWindowAsync("qwen3-coder:30b", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway unreachable"));

        var logger = new CapturingLogger<Crucible.CrucibleAgentHarness>();
        var client = new CapturingStreamingChatClient((_, _) =>
            [new ChatResponseUpdate(ChatRole.Assistant, "Fallback completed.")]);

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            GatewayService = mockGateway.Object,
            LlmOptions = Options.Create(new LlmProviderOptions { ContextWindowTokens = 65536 }),
            Logger = logger,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "docs", "transcripts"))
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 403, Title = "Gateway Fallback Test", Description = "Verify fallback on error" },
            StageId = StandardStageIds.Implementation,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            WorkspacePath = _tempDir,
            Profile = new AgentProfile
            {
                Id = "dev",
                Name = "Dev",
                ModelName = "qwen3-coder:30b",
                ContextWindowTokens = null
            }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("qwen3-coder:30b"));

        var transcriptFiles = Directory.GetFiles(Path.Combine(_tempDir, "docs", "transcripts"), "*-task-403-*.md");
        var transcriptText = await File.ReadAllTextAsync(Assert.Single(transcriptFiles));
        Assert.Contains("context_window_tokens: 65536", transcriptText);
    }

    [Fact]
    public async Task IdeationService_WhenGatewayThrowsNonCancellationException_LogsWarningAndFallsBackGracefully()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway
            .Setup(g => g.ResolveContextWindowAsync("deepseek-r1:32b", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway timeout"));

        var logger = new CapturingLogger<IdeationService>();
        var client = new CapturingStreamingChatClient((_, _) =>
            [new ChatResponseUpdate(ChatRole.Assistant, "Ideation fallback succeeded.")]);

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            GatewayService = mockGateway.Object,
            LlmOptions = Options.Create(new LlmProviderOptions { ContextWindowTokens = 65536 }),
            Logger = logger,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "ideation-transcripts"))
        }.Build();

        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            ModelName = "deepseek-r1:32b",
            ContextWindowTokens = null
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "WS",
            RepositoryPath = _tempDir,
            AgentProfiles = new List<AgentProfile> { profile }
        };

        var result = await service.ConverseAsync(workspace, "Plan next iteration", agentProfile: profile);

        Assert.NotNull(result);
        Assert.Contains("Ideation fallback succeeded.", result.AssistantMessage);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("deepseek-r1:32b"));
    }

    [Fact]
    public async Task IdeationService_WhenContextWindowNull_PassesDiscoveredWindowToCompactionStrategy()
    {
        // Discover a small 4096-token window from the gateway so history exceeding ~3,072 tokens (~12,300 chars)
        // triggers PiStyleCrucibleCompactionStrategy before the model call.
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway
            .Setup(g => g.ResolveContextWindowAsync("compact-model:7b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(4096);

        var callMessages = new List<List<ChatMessage>>();
        var client = new CapturingStreamingChatClient((msgs, _) =>
        {
            callMessages.Add(msgs.ToList());
            return [new ChatResponseUpdate(ChatRole.Assistant, "Compacted summary response.")];
        });

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            GatewayService = mockGateway.Object,
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "ideation-compact-transcripts"))
        }.Build();

        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            ModelName = "compact-model:7b",
            ContextWindowTokens = null
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "WS",
            RepositoryPath = _tempDir,
            AgentProfiles = new List<AgentProfile> { profile }
        };

        var longHistory = new List<(string Role, string Content)>
        {
            ("user", new string('A', 6000)),
            ("assistant", new string('B', 6000)),
            ("user", new string('C', 6000)),
            ("assistant", new string('D', 6000))
        };

        var result = await service.ConverseAsync(workspace, "Final question", conversationHistory: longHistory, agentProfile: profile);

        Assert.NotNull(result);
        mockGateway.Verify(g => g.ResolveContextWindowAsync("compact-model:7b", It.IsAny<CancellationToken>()), Times.Once);
        // Compaction strategy invoked the summarizer client first, then the main agent turn.
        Assert.True(callMessages.Count >= 2);
    }

    // -------------------------------------------------------------------------
    // 3. Read-Only Tool Scoping
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentMode.ReadOnly, AgentMode.Full)]
    [InlineData(AgentMode.Full, AgentMode.ReadOnly)]
    public async Task CrucibleAgentHarness_WhenReadOnlyMode_ExcludesWriteAndEditToolsFromDefaultToolset(
        AgentMode profileMode,
        AgentMode requiredMode)
    {
        ChatOptions? capturedOptions = null;
        var client = new CapturingStreamingChatClient((_, opts) =>
        {
            capturedOptions = opts;
            return [new ChatResponseUpdate(ChatRole.Assistant, "Read-only inspection complete.")];
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "docs", "transcripts"))
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 404, Title = "ReadOnly Toolset Test", Description = "Inspect only" },
            StageId = StandardStageIds.Triage,
            RequiredMode = requiredMode,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            WorkspacePath = _tempDir,
            Profile = new AgentProfile
            {
                Id = "triage",
                Name = "Triage",
                Mode = profileMode,
                PermittedTools = new List<string>()
            }
        };

        await harness.RunStageAsync(context);

        Assert.NotNull(capturedOptions?.Tools);
        var toolNames = capturedOptions.Tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains(CanonicalToolNames.Read, toolNames);
        Assert.DoesNotContain(CanonicalToolNames.Write, toolNames);
        Assert.DoesNotContain(CanonicalToolNames.Edit, toolNames);
    }

    [Theory]
    [InlineData(AgentMode.ReadOnly, SandboxSecurityTier.Standard)]
    [InlineData(AgentMode.Full, SandboxSecurityTier.RestrictedReadOnly)]
    public async Task CrucibleAgentHarness_WhenReadOnlyOrRestrictedSecurityTier_RejectsMutatingToolCalls(
        AgentMode mode,
        SandboxSecurityTier securityTier)
    {
        int turn = 0;
        string? writeToolResult = null;
        var client = new CapturingStreamingChatClient((messages, _) =>
        {
            turn++;
            if (turn == 1)
            {
                return
                [
                    new ChatResponseUpdate
                    {
                        Contents =
                        [
                            new FunctionCallContent("call-write", CanonicalToolNames.Write, new Dictionary<string, object?>
                            {
                                ["path"] = "blocked.txt",
                                ["content"] = "should not be written"
                            })
                        ]
                    }
                ];
            }

            writeToolResult = messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .FirstOrDefault(r => r.CallId == "call-write")
                ?.Result?.ToString();

            return [new ChatResponseUpdate(ChatRole.Assistant, "Write was rejected.")];
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "docs", "transcripts"))
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 405, Title = "Reject Write Test", Description = "Verify write rejection" },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            WorkspacePath = _tempDir,
            Profile = new AgentProfile
            {
                Id = "restricted-profile",
                Name = "Restricted",
                Mode = mode,
                SecurityTier = securityTier,
                PermittedTools = new List<string> { CanonicalToolNames.Read, CanonicalToolNames.Write, CanonicalToolNames.Edit }
            }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(writeToolResult);
        Assert.Contains("not permitted in read-only mode", writeToolResult, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_tempDir, "blocked.txt")));
    }

    [Fact]
    public async Task IdeationService_WhenReadOnlyMode_ExcludesWriteAndEditToolsFromDefaultToolset()
    {
        ChatOptions? capturedOptions = null;
        var client = new CapturingStreamingChatClient((_, opts) =>
        {
            capturedOptions = opts;
            return [new ChatResponseUpdate(ChatRole.Assistant, "Read-only ideation complete.")];
        });

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "ideation-transcripts"))
        }.Build();

        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            Mode = AgentMode.ReadOnly,
            PermittedTools = new List<string>()
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "WS",
            RepositoryPath = _tempDir,
            AgentProfiles = new List<AgentProfile> { profile }
        };

        await service.ConverseAsync(workspace, "Explore codebase", agentProfile: profile);

        Assert.NotNull(capturedOptions?.Tools);
        var toolNames = capturedOptions.Tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains(CanonicalToolNames.Read, toolNames);
        Assert.DoesNotContain(CanonicalToolNames.Write, toolNames);
        Assert.DoesNotContain(CanonicalToolNames.Edit, toolNames);
    }

    [Theory]
    [InlineData(AgentMode.ReadOnly, SandboxSecurityTier.Standard)]
    [InlineData(AgentMode.Full, SandboxSecurityTier.RestrictedReadOnly)]
    public async Task IdeationService_WhenReadOnlyOrRestrictedSecurityTier_RejectsMutatingToolCalls(
        AgentMode mode,
        SandboxSecurityTier securityTier)
    {
        int turn = 0;
        string? writeToolResult = null;
        var client = new CapturingStreamingChatClient((messages, _) =>
        {
            turn++;
            if (turn == 1)
            {
                return
                [
                    new ChatResponseUpdate
                    {
                        Contents =
                        [
                            new FunctionCallContent("ideation-write-1", CanonicalToolNames.Write, new Dictionary<string, object?>
                            {
                                ["path"] = "ideation-blocked.txt",
                                ["content"] = "nope"
                            })
                        ]
                    }
                ];
            }

            writeToolResult = messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .FirstOrDefault(r => r.CallId == "ideation-write-1")
                ?.Result?.ToString();

            return [new ChatResponseUpdate(ChatRole.Assistant, "Write rejected as expected.")];
        });

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TranscriptService = new SessionTranscriptService(Path.Combine(_tempDir, "ideation-transcripts"))
        }.Build();

        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            Mode = mode,
            SecurityTier = securityTier,
            PermittedTools = new List<string> { CanonicalToolNames.Read, CanonicalToolNames.Write, CanonicalToolNames.Edit }
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "WS",
            RepositoryPath = _tempDir,
            AgentProfiles = new List<AgentProfile> { profile }
        };

        var result = await service.ConverseAsync(workspace, "Attempt write", agentProfile: profile);

        Assert.NotNull(result);
        Assert.NotNull(writeToolResult);
        Assert.Contains("not permitted in read-only mode", writeToolResult, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_tempDir, "ideation-blocked.txt")));
    }

    // -------------------------------------------------------------------------
    // Test Doubles
    // -------------------------------------------------------------------------

    private sealed class CapturingStreamingChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatResponseUpdate>> _handler;

        public CapturingStreamingChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatResponseUpdate>> handler)
        {
            _handler = handler;
        }

        public ChatClientMetadata Metadata => new("CapturingStreamingChatClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var updates = _handler(chatMessages, options);
            var msg = new ChatMessage(ChatRole.Assistant, string.Concat(updates.Select(u => u.Text)));
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _handler(chatMessages, options))
            {
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
