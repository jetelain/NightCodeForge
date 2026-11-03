using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.ExternalProcess;

/// <summary>
/// Cohesive collaborator for external-process harnesses that broadcasts live agent activity
/// and persists markdown session transcripts.
/// </summary>
public class HarnessActivityRecorder : IHarnessActivityRecorder
{
    private const string TranscriptRoleUser = "User";
    private const string TranscriptRoleAssistant = "Assistant";
    private const string DocsFolder = "docs";
    private const string TranscriptsFolder = "transcripts";

    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly ISessionTranscriptService _transcriptService;
    private readonly ILogger<HarnessActivityRecorder> _logger;

    public HarnessActivityRecorder(
        IAgentActivityStreamer activityStreamer,
        ISessionTranscriptService transcriptService,
        ILogger<HarnessActivityRecorder> logger)
    {
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken cancellationToken)
    {
        var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow);
        return _activityStreamer.BroadcastAsync(evt, cancellationToken);
    }

    public async Task SaveTranscriptAsync(
        string harnessId,
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            var entries = new List<TranscriptEntry>();
            var now = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(promptText))
            {
                entries.Add(new TranscriptEntry(now, TranscriptRoleUser, promptText, null, null, null));
            }
            if (!string.IsNullOrEmpty(summaryText))
            {
                entries.Add(new TranscriptEntry(now, TranscriptRoleAssistant, summaryText, null, null, null));
            }

            var meta = new TranscriptMetadata(
                context.ForgeTask.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                elapsedMs,
                new AgentRunMetrics(elapsedMs, promptTokens, completionTokens, promptTokens + completionTokens),
                context.OptimizationFlags ?? new LocalOptimizationFlags(),
                status);

            var repoRoot = context.Workspace?.RepositoryPath ?? workspacePath;
            var transcriptDir = Path.Combine(repoRoot, DocsFolder, TranscriptsFolder);
            await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist markdown transcript in {HarnessId} harness", harnessId);
        }
    }
}

