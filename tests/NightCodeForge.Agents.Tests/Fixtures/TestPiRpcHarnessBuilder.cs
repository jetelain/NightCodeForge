namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

public class TestPiRpcHarnessBuilder
{
    public IOptions<PiRpcHarnessOptions> Options { get; set; } = Microsoft.Extensions.Options.Options.Create(new PiRpcHarnessOptions());
    public IOptions<LlmProviderOptions> LlmOptions { get; set; } = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions());
    public AgentsMdService AgentsMdService { get; set; } = new();
    public IAgentActivityStreamer ActivityStreamer { get; set; } = new InMemoryAgentActivityStreamer();
    public SessionTranscriptService TranscriptService { get; set; } = new();
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IProcessRunner ProcessRunner { get; set; } = new Mock<IProcessRunner>().Object;
    public ILogger<PiRpcAgentHarness> Logger { get; set; } = NullLogger<PiRpcAgentHarness>.Instance;

    public PiRpcAgentHarness Build()
    {
        return new PiRpcAgentHarness(
            Options,
            LlmOptions,
            AgentsMdService,
            ActivityStreamer,
            TranscriptService,
            PathResolver,
            ProcessRunner,
            Logger);
    }
}
