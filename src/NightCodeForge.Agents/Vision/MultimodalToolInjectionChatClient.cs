using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Vision;

/// <summary>
/// Delegating chat client executing outermost on the <c>HarnessAgent</c> inner client chain
/// so staged images from canonical <c>read</c> calls are materialized deterministically in a synthetic
/// <see cref="ChatRole.User"/> message immediately following each contiguous <see cref="ChatRole.Tool"/> run,
/// with sliding-window eviction capped at <see cref="VisionConstants.MaxImagesInActiveWindow"/> images.
/// </summary>
public sealed class MultimodalToolInjectionChatClient : DelegatingChatClient
{
    /// <summary>
    /// Request-scoped property flagging a synthetic visual <see cref="ChatRole.User"/> message injected during the current call
    /// (rather than already restored into <c>CompactionMessageIndex</c>).
    /// </summary>
    public const string PropNewlyInjectedInMiddleware = "vision.newly_injected_in_middleware";

    private readonly IMultimodalAttachmentQueue _queue;
    private readonly int _maxImagesInActiveWindow;

    public MultimodalToolInjectionChatClient(IChatClient innerClient, IMultimodalAttachmentQueue queue)
        : this(innerClient, queue, VisionConstants.MaxImagesInActiveWindow)
    {
    }

    public MultimodalToolInjectionChatClient(IChatClient innerClient, IMultimodalAttachmentQueue queue, int maxImagesInActiveWindow)
        : base(innerClient ?? throw new ArgumentNullException(nameof(innerClient)))
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _maxImagesInActiveWindow = maxImagesInActiveWindow > 0
            ? maxImagesInActiveWindow
            : throw new ArgumentOutOfRangeException(nameof(maxImagesInActiveWindow), "MaxImagesInActiveWindow must be positive.");
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = TransformMessages(messages, _queue, _maxImagesInActiveWindow, markNewlyInjected: true);
        return base.GetResponseAsync(prepared, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = TransformMessages(messages, _queue, _maxImagesInActiveWindow, markNewlyInjected: true);
        await foreach (var update in base.GetStreamingResponseAsync(prepared, options, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Materializes staged visuals immediately after their enclosing contiguous <see cref="ChatRole.Tool"/> run
    /// (grouping parallel tool results into one combined <see cref="ChatRole.User"/> message),
    /// then enforces the active sliding-window image limit.
    /// </summary>
    public static List<ChatMessage> TransformMessages(
        IEnumerable<ChatMessage> sourceMessages,
        IMultimodalAttachmentQueue queue,
        int maxImagesInActiveWindow = VisionConstants.MaxImagesInActiveWindow,
        bool markNewlyInjected = false)
    {
        ArgumentNullException.ThrowIfNull(sourceMessages);
        ArgumentNullException.ThrowIfNull(queue);

        var messages = sourceMessages as IList<ChatMessage> ?? sourceMessages.ToList();
        var output = new List<ChatMessage>(messages.Count);

        int i = 0;
        while (i < messages.Count)
        {
            var current = StripNewlyInjectedPropertyIfPresent(messages[i]);
            if (current.Role != ChatRole.Tool)
            {
                output.Add(current);
                i++;
                continue;
            }

            int runEnd = i;
            var runToolResults = new List<(string? CallId, string? ResultText)>();
            while (runEnd < messages.Count && messages[runEnd].Role == ChatRole.Tool)
            {
                var toolMsg = StripNewlyInjectedPropertyIfPresent(messages[runEnd]);
                output.Add(toolMsg);

                if (toolMsg.Contents != null)
                {
                    foreach (var frc in toolMsg.Contents.OfType<FunctionResultContent>())
                    {
                        runToolResults.Add((frc.CallId, frc.Result?.ToString()));
                    }
                }

                runEnd++;
            }

            var stagedVisuals = queue.ResolveForToolRun(runToolResults);
            if (stagedVisuals.Count > 0)
            {
                bool alreadyFollowedByVisualUser = runEnd < messages.Count &&
                    IsMatchingPairedVisualUserMessage(messages[runEnd], stagedVisuals);

                if (!alreadyFollowedByVisualUser)
                {
                    output.Add(BuildInjectedVisualUserMessage(stagedVisuals, markNewlyInjected));
                }
            }

            i = runEnd;
        }

        EnforceActiveImageWindow(output, maxImagesInActiveWindow);
        return output;
    }

    /// <summary>
    /// Enforces a sliding window of at most <paramref name="maxImages"/> live visual <see cref="DataContent"/> items
    /// across <paramref name="messages"/> (chronologically oldest evicted first with a clean text marker).
    /// Returns the number of evicted images.
    /// </summary>
    public static int EnforceActiveImageWindow(IList<ChatMessage> messages, int maxImages = VisionConstants.MaxImagesInActiveWindow)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (maxImages <= 0 || messages.Count == 0)
        {
            return 0;
        }

        var imageLocations = CollectImageLocations(messages);
        int excess = imageLocations.Count - maxImages;
        if (excess <= 0)
        {
            return 0;
        }

        return ReplaceImageDataContents(
            messages,
            imageLocations.GetRange(0, excess),
            VisionConstants.FormatEvictedImageMarker);
    }

    /// <summary>
    /// Replaces all live visual <see cref="DataContent"/> payloads in <paramref name="messages"/>
    /// with deterministic omitted-image text markers so text-only models never receive binary image content
    /// (without mutating the original <see cref="ChatMessage"/> instances).
    /// Returns the number of omitted images.
    /// </summary>
    public static int StripImagesForTextOnlyModel(IList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return 0;
        }

        var imageLocations = CollectImageLocations(messages);
        if (imageLocations.Count == 0)
        {
            return 0;
        }

        return ReplaceImageDataContents(
            messages,
            imageLocations,
            VisionConstants.FormatVisionDisabledOmittedImageMarker);
    }

    private static List<(int MessageIndex, int ContentIndex, DataContent Content)> CollectImageLocations(IList<ChatMessage> messages)
    {
        var imageLocations = new List<(int MessageIndex, int ContentIndex, DataContent Content)>();
        for (int mIdx = 0; mIdx < messages.Count; mIdx++)
        {
            var contents = messages[mIdx].Contents;
            if (contents == null) continue;

            for (int cIdx = 0; cIdx < contents.Count; cIdx++)
            {
                if (contents[cIdx] is DataContent dc && dc.HasTopLevelMediaType(VisionConstants.TopLevelImageMediaType))
                {
                    imageLocations.Add((mIdx, cIdx, dc));
                }
            }
        }

        return imageLocations;
    }

    private static int ReplaceImageDataContents(
        IList<ChatMessage> messages,
        IReadOnlyList<(int MessageIndex, int ContentIndex, DataContent Content)> targets,
        Func<string, string> formatMarker)
    {
        var mutatedContentsByMessage = new Dictionary<int, List<AIContent>>();
        foreach (var (mIdx, cIdx, dc) in targets)
        {
            if (!mutatedContentsByMessage.TryGetValue(mIdx, out var clonedContents))
            {
                var orig = messages[mIdx];
                clonedContents = orig.Contents.ToList();
                mutatedContentsByMessage[mIdx] = clonedContents;

                var replacementMsg = new ChatMessage(orig.Role, clonedContents)
                {
                    AuthorName = orig.AuthorName
                };
                if (orig.AdditionalProperties != null)
                {
                    replacementMsg.AdditionalProperties = new AdditionalPropertiesDictionary(orig.AdditionalProperties);
                }

                messages[mIdx] = replacementMsg;
            }

            var sourcePath = ResolveDataContentSourcePath(dc);
            clonedContents[cIdx] = new TextContent(formatMarker(sourcePath));
        }

        return targets.Count;
    }

    public static string ResolveDataContentSourcePath(DataContent content)
    {
        if (content.AdditionalProperties != null &&
            content.AdditionalProperties.TryGetValue(VisionConstants.PropSourcePath, out var rawPath))
        {
            var str = ExtractString(rawPath);
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str;
            }
        }

        return !string.IsNullOrWhiteSpace(content.Name) ? content.Name : "image";
    }

    public static int? ResolveDataContentIntProperty(DataContent content, string key)
    {
        if (content.AdditionalProperties == null || !content.AdditionalProperties.TryGetValue(key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            int iv => iv,
            long lv when lv <= int.MaxValue => (int)lv,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var parsed) => parsed,
            _ => null
        };
    }

    public static string? ResolveDataContentStringProperty(DataContent content, string key)
    {
        if (content.AdditionalProperties == null || !content.AdditionalProperties.TryGetValue(key, out var raw))
        {
            return null;
        }

        return ExtractString(raw);
    }

    private static string? ExtractString(object? raw) => raw switch
    {
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        _ => raw?.ToString()
    };

    private static ChatMessage BuildInjectedVisualUserMessage(
        IReadOnlyList<StagedVisualContent> stagedVisuals,
        bool markNewlyInjected)
    {
        var contents = new List<AIContent>(stagedVisuals.Count * 2);
        var callIds = new List<string>(stagedVisuals.Count);

        foreach (var visual in stagedVisuals)
        {
            contents.Add(new TextContent(
                VisionConstants.FormatVisualHeader(visual.SourcePath, visual.CallId, visual.WidthPx, visual.HeightPx)));
            contents.Add(visual.ToDataContent());
            callIds.Add(!string.IsNullOrWhiteSpace(visual.CallId) ? visual.CallId : visual.SourcePath);
        }

        var props = new AdditionalPropertiesDictionary
        {
            [VisionConstants.PropIsInjectedVisual] = true,
            [VisionConstants.PropInjectedCallIds] = callIds.ToArray()
        };

        if (markNewlyInjected)
        {
            props[PropNewlyInjectedInMiddleware] = true;
        }

        return new ChatMessage(ChatRole.User, contents)
        {
            AdditionalProperties = props
        };
    }

    private static ChatMessage StripNewlyInjectedPropertyIfPresent(ChatMessage message)
    {
        if (message.AdditionalProperties == null || !message.AdditionalProperties.ContainsKey(PropNewlyInjectedInMiddleware))
        {
            return message;
        }

        var props = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        props.Remove(PropNewlyInjectedInMiddleware);
        return new ChatMessage(message.Role, message.Contents.ToList())
        {
            AuthorName = message.AuthorName,
            AdditionalProperties = props
        };
    }

    private static bool IsMatchingPairedVisualUserMessage(
        ChatMessage nextMessage,
        IReadOnlyList<StagedVisualContent> stagedVisuals)
    {
        if (nextMessage.Role != ChatRole.User || nextMessage.Contents == null)
        {
            return false;
        }

        bool hasInjectedMarkerProp = nextMessage.AdditionalProperties != null &&
            nextMessage.AdditionalProperties.TryGetValue(VisionConstants.PropIsInjectedVisual, out var rawFlag) &&
            (rawFlag is true || (rawFlag is JsonElement jeFlag && jeFlag.ValueKind == JsonValueKind.True));

        bool hasVisualContent = false;
        var presentCallIds = new HashSet<string>(StringComparer.Ordinal);
        var presentSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (nextMessage.AdditionalProperties != null &&
            nextMessage.AdditionalProperties.TryGetValue(VisionConstants.PropInjectedCallIds, out var rawIds))
        {
            if (rawIds is IEnumerable<string> cidList)
            {
                foreach (var cid in cidList)
                {
                    if (!string.IsNullOrWhiteSpace(cid)) presentCallIds.Add(cid);
                }
            }
            else if (rawIds is JsonElement jeIds && jeIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in jeIds.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                    {
                        presentCallIds.Add(el.GetString()!);
                    }
                }
            }
        }

        foreach (var content in nextMessage.Contents)
        {
            if (content is DataContent dc && dc.HasTopLevelMediaType(VisionConstants.TopLevelImageMediaType))
            {
                hasVisualContent = true;
                var sp = ResolveDataContentSourcePath(dc);
                if (!string.IsNullOrWhiteSpace(sp)) presentSourcePaths.Add(sp);
                var cid = ResolveDataContentStringProperty(dc, VisionConstants.PropCallId);
                if (!string.IsNullOrWhiteSpace(cid)) presentCallIds.Add(cid);
            }
            else if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text) &&
                     (tc.Text.Contains("[Visual Attachment:", StringComparison.Ordinal) ||
                      tc.Text.Contains("[Earlier visual attachment", StringComparison.Ordinal) ||
                      tc.Text.Contains("[Missing visual attachment:", StringComparison.Ordinal)))
            {
                hasVisualContent = true;
                foreach (var v in stagedVisuals)
                {
                    if (tc.Text.Contains(v.SourcePath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(v.CallId) && tc.Text.Contains(v.CallId, StringComparison.Ordinal)))
                    {
                        presentSourcePaths.Add(v.SourcePath);
                    }
                }
            }
        }

        if (!hasInjectedMarkerProp && !hasVisualContent)
        {
            return false;
        }

        return stagedVisuals.All(v =>
            (!string.IsNullOrWhiteSpace(v.CallId) && presentCallIds.Contains(v.CallId)) ||
            presentSourcePaths.Contains(v.SourcePath));
    }
}
