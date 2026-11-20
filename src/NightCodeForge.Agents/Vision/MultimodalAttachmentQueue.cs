using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Vision;

/// <inheritdoc />
public sealed class MultimodalAttachmentQueue : IMultimodalAttachmentQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StagedVisualContent> _byCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StagedVisualContent> _bySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StagedVisualContent> _unkeyedQueue = new();

    public void Stage(StagedVisualContent visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(visual.CallId))
            {
                _byCallId[visual.CallId] = visual;
            }
            else
            {
                _unkeyedQueue.Add(visual);
            }
        }
    }

    public IReadOnlyList<StagedVisualContent> ResolveForToolRun(
        IReadOnlyList<(string? CallId, string? ResultText)> toolResults)
    {
        if (toolResults == null || toolResults.Count == 0)
        {
            return Array.Empty<StagedVisualContent>();
        }

        lock (_lock)
        {
            var resolved = new List<StagedVisualContent>();
            foreach (var (callId, resultText) in toolResults)
            {
                if (!string.IsNullOrWhiteSpace(callId) && _byCallId.TryGetValue(callId, out var keyed))
                {
                    resolved.Add(keyed);
                    continue;
                }

                if (!IsLoadedImageToolResult(resultText, out var parsedSourcePath))
                {
                    continue;
                }

                StagedVisualContent? matched = null;
                if (_unkeyedQueue.Count > 0)
                {
                    int matchIndex = !string.IsNullOrWhiteSpace(parsedSourcePath)
                        ? _unkeyedQueue.FindIndex(v => string.Equals(v.SourcePath, parsedSourcePath, StringComparison.OrdinalIgnoreCase))
                        : 0;

                    if (matchIndex >= 0)
                    {
                        matched = _unkeyedQueue[matchIndex];
                        _unkeyedQueue.RemoveAt(matchIndex);

                        if (!string.IsNullOrWhiteSpace(callId))
                        {
                            matched = matched with { CallId = callId };
                            _byCallId[callId] = matched;
                        }
                        else if (!string.IsNullOrWhiteSpace(matched.SourcePath))
                        {
                            _bySourcePath[matched.SourcePath] = matched;
                        }
                    }
                }

                if (matched == null &&
                    !string.IsNullOrWhiteSpace(parsedSourcePath) &&
                    _bySourcePath.TryGetValue(parsedSourcePath, out var replayed))
                {
                    matched = replayed;
                    if (!string.IsNullOrWhiteSpace(callId))
                    {
                        matched = matched with { CallId = callId };
                        _byCallId[callId] = matched;
                    }
                }

                if (matched != null)
                {
                    resolved.Add(matched);
                }
            }

            return resolved;
        }
    }

    public int EstimateUnmaterializedVisualTokens(
        IReadOnlyList<ChatMessage> includedMessages,
        int startMessageIndex = 0)
    {
        if (includedMessages == null || includedMessages.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_byCallId.Count == 0 && _unkeyedQueue.Count == 0)
            {
                return 0;
            }

            var materializedCallIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var msg in includedMessages)
            {
                if (msg.Contents == null) continue;
                foreach (var content in msg.Contents)
                {
                    if (content is DataContent dc &&
                        dc.HasTopLevelMediaType(VisionConstants.TopLevelImageMediaType) &&
                        dc.AdditionalProperties != null &&
                        dc.AdditionalProperties.TryGetValue(VisionConstants.PropCallId, out var rawCid) &&
                        rawCid is string cid &&
                        !string.IsNullOrWhiteSpace(cid))
                    {
                        materializedCallIds.Add(cid);
                    }
                }
            }

            int total = 0;
            int clampedStart = Math.Clamp(startMessageIndex, 0, includedMessages.Count);
            var countedCallIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = clampedStart; i < includedMessages.Count; i++)
            {
                var msg = includedMessages[i];
                if (msg.Role != ChatRole.Tool || msg.Contents == null) continue;

                foreach (var frc in msg.Contents.OfType<FunctionResultContent>())
                {
                    if (!string.IsNullOrWhiteSpace(frc.CallId) &&
                        !materializedCallIds.Contains(frc.CallId) &&
                        countedCallIds.Add(frc.CallId) &&
                        _byCallId.TryGetValue(frc.CallId, out var visual))
                    {
                        total += visual.EstimatedTokens;
                    }
                }
            }

            if (clampedStart == 0 && _unkeyedQueue.Count > 0)
            {
                foreach (var unkeyed in _unkeyedQueue)
                {
                    total += unkeyed.EstimatedTokens;
                }
            }

            return total;
        }
    }

    public void PruneToRetainedCallIds(IReadOnlySet<string> retainedCallIds)
    {
        ArgumentNullException.ThrowIfNull(retainedCallIds);
        lock (_lock)
        {
            foreach (var key in _byCallId.Keys.Where(k => !retainedCallIds.Contains(k)).ToList())
            {
                _byCallId.Remove(key);
            }
            _bySourcePath.Clear();
            _unkeyedQueue.Clear();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byCallId.Clear();
            _bySourcePath.Clear();
            _unkeyedQueue.Clear();
        }
    }

    public static void PruneToIncludedMessages(
        IMultimodalAttachmentQueue queue,
        IEnumerable<ChatMessage> includedMessages)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(includedMessages);

        var retainedCallIds = includedMessages
            .SelectMany(m => m.Contents ?? Array.Empty<AIContent>())
            .OfType<FunctionResultContent>()
            .Select(frc => frc.CallId)
            .Where(cid => !string.IsNullOrWhiteSpace(cid))
            .Select(cid => cid!)
            .ToHashSet(StringComparer.Ordinal);
        queue.PruneToRetainedCallIds(retainedCallIds);
    }

    private static bool IsLoadedImageToolResult(string? resultText, out string? sourcePath)
    {
        sourcePath = null;
        if (string.IsNullOrWhiteSpace(resultText) ||
            !resultText.StartsWith(VisionConstants.LoadedImageToolResultPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int start = VisionConstants.LoadedImageToolResultPrefix.Length;
        int end = resultText.IndexOf('\'', start);
        if (end > start)
        {
            sourcePath = resultText[start..end];
        }

        return true;
    }
}
