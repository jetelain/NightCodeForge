using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Ideation;

internal sealed class WarmIdeationSession
{
    public sealed class PendingQuestionGroup
    {
        public PendingQuestionGroup(Guid groupId, IReadOnlyList<IdeationQuestion> questions)
        {
            GroupId = groupId;
            Questions = questions;
        }

        public Guid GroupId { get; }
        public IReadOnlyList<IdeationQuestion> Questions { get; }
        public bool IsSubmitted { get; set; }
        public string? SubmittedFormattedAnswer { get; set; }
        public string? SubmittedMetadataJson { get; set; }
    }

    public sealed class MutableInFlightPart
    {
        public required IdeationInFlightPartKind Kind { get; init; }
        public string? Text { get; set; }
        public IdeationToolCallInfo? ToolCall { get; set; }
        public bool IsToolCompleted { get; set; }
        public Guid? QuestionGroupId { get; init; }
        public IReadOnlyList<IdeationQuestion>? Questions { get; init; }
        public string? QuestionAnswer { get; set; }
        public string? QuestionMetadataJson { get; set; }
        public int? PreCompactionTokens { get; init; }
        public int? PostCompactionTokens { get; init; }
        public double? DurationMs { get; init; }

        public IdeationInFlightPart ToImmutable() => new(
            Kind,
            Text,
            ToolCall,
            IsToolCompleted,
            QuestionGroupId,
            Questions,
            QuestionAnswer,
            QuestionMetadataJson,
            PreCompactionTokens,
            PostCompactionTokens,
            DurationMs);
    }

    public sealed class SubscriberEntry
    {
        public SubscriberEntry(Action<IdeationStreamUpdate> callback)
        {
            Callback = callback;
        }

        public Action<IdeationStreamUpdate> Callback { get; }
    }

    public WarmIdeationSession(
        int workspaceId,
        int conversationId,
        ScopedConversationTurnQueue turnQueue,
        DateTimeOffset createdAt)
    {
        WorkspaceId = workspaceId;
        ConversationId = conversationId;
        RouterSessionId = LlmSessionIds.ForIdeation(workspaceId, conversationId);
        TurnQueue = turnQueue;
        LastAccessedAt = createdAt;
    }

    public int WorkspaceId { get; }
    public int ConversationId { get; }
    public string RouterSessionId { get; }
    public ScopedConversationTurnQueue TurnQueue { get; }

    public object StateGate { get; } = new();
    public object HistoryGate { get; } = new();
    public SemaphoreSlim SessionInitLock { get; } = new(1, 1);
    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    public IdeationSessionOptions Options { get; set; } = new();
    public int ConfigGeneration { get; set; }
    public bool OptionsDirty { get; set; }
    public bool ContextDirty { get; set; }
    public bool PendingEviction { get; set; }
    public bool PendingForceEviction { get; set; }
    public bool IsEvicted { get; set; }
    public bool CancelRequested { get; set; }
    public int WaitingOrActiveTurns { get; set; }
    public bool BackgroundBacklogSyncTriggered { get; set; }
    public DateTimeOffset LastAccessedAt { get; set; }

    public Task? InFlightWarmupTask { get; set; }
    public int InFlightWarmupGeneration { get; set; }
    public IdeationSessionOptions? InFlightWarmupOptions { get; set; }
    public string? LastPreloadedSignature { get; set; }

    public AgentProfile? EffectiveProfile { get; set; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }
    public AgentWorkspaceLocations? Locations { get; set; }
    public CanonicalToolsetSession? ToolsetSession { get; set; }
    public HarnessAgentSetup? HarnessSetup { get; set; }
    public string? SystemPrompt { get; set; }
    public IReadOnlyList<string> PermittedTools { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ActiveSkills { get; set; } = Array.Empty<string>();
    public string? LastBacklogDigest { get; set; }
    public string? LastAttachmentsDigest { get; set; }
    public string? LastFrictionHotspotsDigest { get; set; }
    public bool HistoryHydrated { get; set; }
    public int? HydratedMaxTurnIndex { get; set; }
    public bool SupportsVision { get; set; }
    public IMultimodalAttachmentQueue? MultimodalAttachmentQueue { get; set; }
    public Dictionary<int, IReadOnlyList<DataContent>> PendingUserTurnImages { get; } = new();
    public List<ChatMessage> ChatHistory { get; } = new();

    public bool IsGenerating { get; private set; }
    public bool IsExecutingInference { get; set; }
    public int? ActiveUserTurnIndex { get; private set; }
    public CancellationTokenSource? ActiveTurnCts { get; set; }
    public StringBuilder CurrentThinkingBuffer { get; } = new();
    public StringBuilder CurrentMessageBuffer { get; } = new();
    public List<IdeationInFlightToolCall> CurrentToolCalls { get; } = new();
    public List<MutableInFlightPart> CurrentParts { get; } = new();
    public List<IdeationProposal> DiscoveredProposals { get; } = new();
    public Dictionary<Guid, PendingQuestionGroup> PendingQuestionGroups { get; } = new();
    public string? LatestTranscriptPath { get; set; }
    public string? LatestFullTranscriptMarkdown { get; set; }
    public List<SubscriberEntry> Subscribers { get; } = new();

    public void BeginInFlightTurnLocked(int activeUserTurnIndex)
    {
        IsGenerating = true;
        ActiveUserTurnIndex = activeUserTurnIndex;
        CurrentThinkingBuffer.Clear();
        CurrentMessageBuffer.Clear();
        CurrentToolCalls.Clear();
        CurrentParts.Clear();
        DiscoveredProposals.Clear();
        PendingQuestionGroups.Clear();
    }

    public void EndInFlightTurnLocked()
    {
        IsGenerating = false;
        ActiveUserTurnIndex = null;
        ActiveTurnCts = null;
        CancelRequested = false;
        CurrentThinkingBuffer.Clear();
        CurrentMessageBuffer.Clear();
        CurrentToolCalls.Clear();
        CurrentParts.Clear();
        DiscoveredProposals.Clear();
        PendingQuestionGroups.Clear();
    }

    public IdeationInFlightTurnSnapshot? CaptureInFlightSnapshotLocked()
    {
        if (!IsGenerating)
        {
            return null;
        }

        return new IdeationInFlightTurnSnapshot(
            WorkspaceId,
            ConversationId,
            IsGenerating,
            ActiveUserTurnIndex,
            CurrentThinkingBuffer.ToString(),
            CurrentMessageBuffer.ToString(),
            CurrentToolCalls.ToList(),
            CurrentParts.Select(p => p.ToImmutable()).ToList(),
            DiscoveredProposals.ToList(),
            LatestTranscriptPath,
            LatestFullTranscriptMarkdown,
            Options);
    }

    public IdeationSessionHandle CaptureSessionHandleLocked(Conversation conversation) =>
        new(
            conversation,
            CaptureInFlightSnapshotLocked(),
            Options,
            LatestTranscriptPath,
            LatestFullTranscriptMarkdown);

    public Action<IdeationStreamUpdate>[] GetSubscribersSnapshotLocked() =>
        Subscribers.Select(s => s.Callback).ToArray();

    public static bool AreOptionsEquivalent(IdeationSessionOptions? a, IdeationSessionOptions? b)
    {
        var left = a ?? new IdeationSessionOptions();
        var right = b ?? new IdeationSessionOptions();

        return left.RoutingTarget == right.RoutingTarget
            && string.Equals(NormalizeNullable(left.ModelOverride), NormalizeNullable(right.ModelOverride), StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeNullable(left.CustomSystemPrompt), NormalizeNullable(right.CustomSystemPrompt), StringComparison.Ordinal)
            && ReferenceEquals(left.ExplicitProfile, right.ExplicitProfile);
    }

    public static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void DisposeLocks()
    {
        SessionInitLock.Dispose();
        TurnLock.Dispose();
    }
}
