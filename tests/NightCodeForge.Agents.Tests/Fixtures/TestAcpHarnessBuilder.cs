namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

public class TestAcpHarnessBuilder
{
    public IOptions<AcpHarnessOptions> Options { get; set; } = Microsoft.Extensions.Options.Options.Create(new AcpHarnessOptions());
    public IExecutionEnvironmentProvider EnvProvider { get; set; } = new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object);
    public IMcpClientManager McpManager { get; set; } = new Mock<IMcpClientManager>().Object;
    public IEditorConfigService EditorConfigService { get; set; } = new EditorConfigService();
    public IEditorConfigFileApplier EditorConfigFileApplier { get; set; } = new EditorConfigFileApplier();
    public AgentsMdService AgentsMdService { get; set; } = new();
    public IAgentActivityStreamer ActivityStreamer { get; set; } = new InMemoryAgentActivityStreamer();
    public SessionTranscriptService TranscriptService { get; set; } = new();
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IProcessRunner ProcessRunner { get; set; } = new Mock<IProcessRunner>().Object;
    public ILogger<AcpAgentHarness> Logger { get; set; } = NullLogger<AcpAgentHarness>.Instance;

    public AcpAgentHarness Build()
    {
        return new AcpAgentHarness(
            Options,
            EnvProvider,
            McpManager,
            EditorConfigService,
            EditorConfigFileApplier,
            AgentsMdService,
            ActivityStreamer,
            TranscriptService,
            PathResolver,
            ProcessRunner,
            Logger);
    }
}
