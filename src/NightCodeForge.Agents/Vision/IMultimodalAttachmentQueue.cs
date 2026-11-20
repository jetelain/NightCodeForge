using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Vision;

/// <summary>
/// Staged normalized visual payload captured during a canonical <c>read</c> tool call
/// and injected into the active conversation immediately after the enclosing <see cref="ChatRole.Tool"/> run.
/// </summary>
public sealed record StagedVisualContent(
    string? CallId,
    string SourcePath,
    string MediaType,
    int WidthPx,
    int HeightPx,
    string Sha256,
    int EstimatedTokens,
    ReadOnlyMemory<byte> Data)
{
    public DataContent ToDataContent()
    {
        var props = new AdditionalPropertiesDictionary
        {
            [VisionConstants.PropSourcePath] = SourcePath,
            [VisionConstants.PropSha256] = Sha256,
            [VisionConstants.PropWidthPx] = WidthPx,
            [VisionConstants.PropHeightPx] = HeightPx
        };

        if (!string.IsNullOrWhiteSpace(CallId))
        {
            props[VisionConstants.PropCallId] = CallId;
        }

        return new DataContent(Data, MediaType)
        {
            Name = Path.GetFileName(SourcePath),
            AdditionalProperties = props
        };
    }
}

/// <summary>
/// Session-scoped queue retaining normalized visual binaries produced by canonical <c>read</c> calls,
/// enabling deterministic replay across inner tool-loop iterations (preserving KV-cache prefix stability)
/// and clean pruning after context compaction.
/// </summary>
public interface IMultimodalAttachmentQueue
{
    void Stage(StagedVisualContent visual);

    IReadOnlyList<StagedVisualContent> ResolveForToolRun(
        IReadOnlyList<(string? CallId, string? ResultText)> toolResults);

    int EstimateUnmaterializedVisualTokens(
        IReadOnlyList<ChatMessage> includedMessages,
        int startMessageIndex = 0);

    void PruneToRetainedCallIds(IReadOnlySet<string> retainedCallIds);

    void Clear();
}
