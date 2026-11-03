using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class ExternalProcessAgentHarnessBaseTests
{
    private const string DefaultExe = "default-exe";
    private const string DefaultArgs = "--default";

    private sealed class ProbeHarness : ExternalProcessAgentHarnessBase
    {
        public ProbeHarness()
            : base(
                new HarnessActivityRecorder(new InMemoryAgentActivityStreamer(), new SessionTranscriptService(), NullLogger<HarnessActivityRecorder>.Instance),
                new AgentsMdService(),
                new Mock<IWorkspacePathResolver>().Object,
                new Mock<IProcessRunner>().Object,
                NullLogger.Instance)
        {
        }

        public override string HarnessId => "probe";
        protected override string AgentDisplayName => "probe agent";
        protected override string DefaultExecutablePath => DefaultExe;
        protected override string DefaultArguments => DefaultArgs;
        protected override Func<Process>? CustomProcessFactory => null;

        public override Task<AgentRunResult> RunSessionStreamAsync(StreamWriter stdin, StreamReader stdout, AgentSessionContext context, string sessionId, Stopwatch sw, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public (string, string) Resolve(AgentSessionContext ctx) => ResolveLaunchCommand(ctx);
        public string Prompt(AgentSessionContext ctx, string root) => BuildEnrichedPrompt(ctx, root);
    }

    private static AgentSessionContext Context(string? json = null, string? humanAnswer = null, string? directive = null) => new()
    {
        ForgeTask = new ForgeTask { Id = 1, Title = "T", Description = "D" },
        Profile = new AgentProfile { Name = "p", HarnessConfigurationJson = json! },
        StageId = "stage",
        PendingHumanAnswer = humanAnswer!,
        StageDirective = directive!
    };

    [Theory]
    [InlineData(null, DefaultExe, DefaultArgs)]
    [InlineData("", DefaultExe, DefaultArgs)]
    [InlineData("{not json", DefaultExe, DefaultArgs)]
    [InlineData("{\"executablePath\":\"x\",\"arguments\":\"-a\"}", "x", "-a")]
    [InlineData("{\"command\":\"y\",\"args\":\"-b\"}", "y", "-b")]
    [InlineData("{\"executable\":\"z\",\"args\":42}", DefaultExe, DefaultArgs)]
    public void ResolveLaunchCommand_ParsesConfigurationAndFallsBack(string? json, string exe, string args)
    {
        var (actualExe, actualArgs) = new ProbeHarness().Resolve(Context(json));
        Assert.Equal(exe, actualExe);
        Assert.Equal(args, actualArgs);
    }

    [Fact]
    public void BuildEnrichedPrompt_IncludesDirectiveEnvironmentAndHumanAnswer()
    {
        var ctx = Context(humanAnswer: "yes", directive: "do it");
        var prompt = new ProbeHarness().Prompt(ctx, Path.GetTempPath());

        Assert.StartsWith("[Human Answer]: yes", prompt);
        Assert.Contains("[Stage Directive]: do it", prompt);
        Assert.Contains("<environment>", prompt);
    }
}
