using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Transcripts;

public record TranscriptDiagnosis(
    string DiagnosisSummary,
    string RootCauseCategory,
    IReadOnlyList<string> RecommendedActions);

/// <summary>
/// Hybrid diagnostic service that evaluates failed session transcripts using fast zero-VRAM Cloud LLMs
/// or 100% offline Local LLMs to generate actionable post-mortem advice.
/// </summary>
public partial class TranscriptDiagnosticService
{
    [GeneratedRegex(@"Root cause(?:\s*category)?:\s*([A-Za-z0-9_\- ]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RootCauseRegex();

    private readonly IChatClient _diagnosticChatClient;

    public TranscriptDiagnosticService(IChatClient diagnosticChatClient)
    {
        _diagnosticChatClient = diagnosticChatClient ?? throw new ArgumentNullException(nameof(diagnosticChatClient));
    }

    public async Task<TranscriptDiagnosis> DiagnoseTranscriptAsync(
        string transcriptMarkdown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcriptMarkdown))
        {
            return new TranscriptDiagnosis("Transcript is empty.", "Unknown", Array.Empty<string>());
        }

        var systemPrompt = @"You are a senior AI agent systems diagnostician.
Analyze the following failed agent execution transcript from NightCodeForge.
Identify:
1. Root cause category (e.g. InfiniteLoop, MalformedToolPayload, CompilationFailure, TestRegression, ModelConfusion).
2. Concise diagnosis summary explaining why the stage failed.
3. 2-3 concrete recommended actions for the developer or next automated retry.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, transcriptMarkdown)
        };

        var response = await _diagnosticChatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var text = response.Text ?? string.Empty;

        // Parse root cause category if available
        var categoryMatch = RootCauseRegex().Match(text);
        var category = categoryMatch.Success ? categoryMatch.Groups[1].Value.Trim() : "GeneralFailure";

        return new TranscriptDiagnosis(
            DiagnosisSummary: text,
            RootCauseCategory: category,
            RecommendedActions: new List<string> { "Review diagnostic summary", "Retry with optimized profile or manual steering" }
        );
    }
}
