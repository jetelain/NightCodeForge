namespace NightCodeForge.Agents.Transcripts;

/// <summary>A message exactly as it was sent to the model (role plus a textual rendering of all of its contents).</summary>
public record ModelCallMessage(string Role, string Text);

/// <summary>A tool declaration exactly as it was sent to the model (name, description and JSON schema).</summary>
public record ToolSchemaSnapshot(string Name, string? Description, string? JsonSchema);

/// <summary>
/// One request sent to the model by the agent framework, captured at the innermost client seam so that
/// everything MAF adds (instructions, injected or rewritten messages, compaction results, tool declarations)
/// is visible. To keep transcripts bounded, each snapshot stores only what changed since the previous call:
/// <see cref="Messages"/> holds the messages after the unchanged prefix, and <see cref="Instructions"/> /
/// <see cref="Tools"/> are populated only when <see cref="InstructionsChanged"/> / <see cref="ToolsChanged"/> is true.
/// </summary>
/// <param name="Index">1-based sequence number of the model call within the run.</param>
/// <param name="Timestamp">UTC time at which the request reached the model client.</param>
/// <param name="TotalMessages">Number of messages sent in this request.</param>
/// <param name="UnchangedPrefixCount">Number of leading messages identical to the previous request.</param>
/// <param name="HistoryRewritten">True when messages of the previous request were altered or removed (e.g. compaction).</param>
/// <param name="Messages">Messages following the unchanged prefix (new or rewritten), in order.</param>
/// <param name="InstructionsChanged">True for the first request and whenever <c>ChatOptions.Instructions</c> changed.</param>
/// <param name="Instructions">Full <c>ChatOptions.Instructions</c> text when changed (null/empty means none).</param>
/// <param name="ToolsChanged">True for the first request and whenever the tool declarations changed.</param>
/// <param name="ToolCount">Number of tools declared in this request.</param>
/// <param name="Tools">Full tool declarations when changed.</param>
/// <param name="OptionsSummary">Rendering of the relevant <c>ChatOptions</c> fields (model, temperature, additional properties...).</param>
public record ModelCallSnapshot(
    int Index,
    DateTime Timestamp,
    int TotalMessages,
    int UnchangedPrefixCount,
    bool HistoryRewritten,
    IReadOnlyList<ModelCallMessage> Messages,
    bool InstructionsChanged,
    string? Instructions,
    bool ToolsChanged,
    int ToolCount,
    IReadOnlyList<ToolSchemaSnapshot>? Tools,
    string OptionsSummary);
