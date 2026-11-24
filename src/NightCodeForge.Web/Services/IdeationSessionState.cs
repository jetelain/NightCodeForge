using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Mutable view model for a tool call executed during ideation.
/// </summary>
public class IdeationToolCallViewModel
{
    public string ToolCallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? Result { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// Base class for chronological content parts within a chat turn.
/// </summary>
public abstract record ChatContentPart;

public record TextContentPart(string Text) : ChatContentPart;

public record ThinkingContentPart(string Thinking) : ChatContentPart;

public record ToolCallContentPart(IdeationToolCallViewModel ToolCall) : ChatContentPart;

public record OptimizationContentPart(
    string Summary,
    int? PreCompactionTokens = null,
    int? PostCompactionTokens = null,
    double? DurationMs = null) : ChatContentPart;

public record VisualInspectionContentPart(
    IReadOnlyList<ConversationHistorySerializer.SerializedImageRef> Images,
    string? Summary = null) : ChatContentPart;

public record QuestionContentPart : ChatContentPart
{
    private string? _prompt;
    private string? _rationale;
    private IReadOnlyList<string>? _options;

    public Guid GroupId { get; init; } = Guid.NewGuid();
    public string? ToolCallId { get; init; }
    public IReadOnlyList<QuestionItem> Questions { get; set; } = Array.Empty<QuestionItem>();
    public IReadOnlyList<IdeationQuestion>? PendingQuestions { get; set; }
    public string? Answer { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsAnswered => !string.IsNullOrWhiteSpace(Answer);

    public Guid QuestionId => PendingQuestions?.FirstOrDefault()?.Id ?? GroupId;

    public string Prompt
    {
        get => Questions.FirstOrDefault()?.Prompt ?? _prompt ?? string.Empty;
        set => _prompt = value;
    }

    public string? Rationale
    {
        get => Questions.FirstOrDefault()?.Rationale ?? _rationale;
        set => _rationale = value;
    }

    public IReadOnlyList<string> Options
    {
        get => Questions.FirstOrDefault()?.Options ?? _options ?? Array.Empty<string>();
        set => _options = value;
    }

    public IReadOnlyList<QuestionItem> EffectiveQuestions
    {
        get
        {
            if (Questions.Count > 0) return Questions;
            if (!string.IsNullOrWhiteSpace(Prompt))
            {
                return new[] { new QuestionItem(Prompt, Rationale, Options) };
            }
            return Array.Empty<QuestionItem>();
        }
    }
}

/// <summary>
/// Immutable chat turn record within an ideation conversational session.
/// </summary>
public record ChatMessage(
    string Role,
    string Content,
    string? ThinkingTrace = null,
    DateTime? Timestamp = null,
    IReadOnlyList<IdeationToolCallViewModel>? ToolCalls = null,
    IReadOnlyList<ChatContentPart>? Parts = null,
    IReadOnlyList<ConversationAttachmentChipViewModel>? AttachmentChips = null)
{
    public string GetEffectiveHistoryContent()
    {
        if (Parts != null && Parts.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in Parts)
            {
                if (part is TextContentPart tp && !string.IsNullOrWhiteSpace(tp.Text))
                {
                    sb.AppendLine(tp.Text);
                }
                else if (part is QuestionContentPart qp)
                {
                    var questions = qp.EffectiveQuestions;
                    if (questions.Count > 1)
                    {
                        for (int i = 0; i < questions.Count; i++)
                        {
                            sb.AppendLine($"[Agent Question {i + 1}: {questions[i].Prompt}]");
                        }
                    }
                    else if (questions.Count == 1)
                    {
                        sb.AppendLine($"[Agent Question: {questions[0].Prompt}]");
                    }
                    if (qp.IsAnswered)
                    {
                        sb.AppendLine($"[User Answer: {qp.Answer}]");
                    }
                }
                else if (part is ToolCallContentPart tcp)
                {
                    var tc = tcp.ToolCall;
                    if (string.Equals(tc.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Parts.OfType<QuestionContentPart>().Any())
                        {
                            sb.AppendLine($"[Tool: {tc.ToolName}({tc.Arguments})]");
                            if (!string.IsNullOrWhiteSpace(tc.Result))
                            {
                                sb.AppendLine($"[{tc.Result}]");
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine($"[Tool: {tc.ToolName}({tc.Arguments})]");
                        if (!string.IsNullOrWhiteSpace(tc.Result))
                        {
                            var resSummary = tc.Result.Length > 400 ? tc.Result[..400] + "... [truncated]" : tc.Result;
                            sb.AppendLine($"[Result: {resSummary}]");
                        }
                    }
                }
            }
            var text = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return Content;
    }
}

/// <summary>
/// Mutable view model for a backlog task candidate discovered during ideation.
/// </summary>
public class IdeationProposalViewModel
{
    public Guid ClientId { get; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Priority { get; set; } = TaskPriority.Normal;
    public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Standard;
    public List<int> SelectedDependencyIds { get; set; } = new();
    public List<string> DependsOnTitles { get; set; } = new();
    public List<int> SelectedAttachmentIds { get; set; } = new();
    public bool IsAdded { get; set; }
    public int? CreatedTaskId { get; set; }
    public bool IsSyncedFromPersistedTask { get; private set; }
    public bool IsEditingDescription { get; set; }

    public IReadOnlyList<int> GetEffectiveAttachmentIds() =>
        AttachmentCitationParser.UnionAttachmentIds(SelectedAttachmentIds, Description)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    public void RemoveAttachment(int attachmentId, string? title = null)
    {
        SelectedAttachmentIds.RemoveAll(id => id == attachmentId);
        Description = AttachmentPresentationHelper.RemoveAttachmentCitation(Description, attachmentId, title);
    }

    public static IdeationProposalViewModel FromProposal(IdeationProposal proposal, IReadOnlyList<ForgeTask>? existingTasks = null)
    {
        var vm = new IdeationProposalViewModel
        {
            Title = proposal.Title ?? string.Empty,
            Description = proposal.Description ?? string.Empty,
            Priority = proposal.Priority > 0 ? proposal.Priority : TaskPriority.Normal,
            Difficulty = TaskMarkdownSyncService.ParseDifficultyOrDefault(proposal.Difficulty),
            CreatedTaskId = proposal.TaskId
        };

        if (proposal.DependsOnIds != null)
        {
            foreach (var depId in proposal.DependsOnIds)
            {
                if (depId != proposal.TaskId && !vm.SelectedDependencyIds.Contains(depId))
                {
                    vm.SelectedDependencyIds.Add(depId);
                }
            }
        }

        if (proposal.DependsOnTitles != null)
        {
            foreach (var depTitle in proposal.DependsOnTitles)
            {
                if (!string.IsNullOrWhiteSpace(depTitle) && !vm.DependsOnTitles.Contains(depTitle, StringComparer.OrdinalIgnoreCase))
                {
                    vm.DependsOnTitles.Add(depTitle.Trim());
                }
            }
        }

        foreach (var attId in AttachmentCitationParser.UnionAttachmentIds(proposal.AttachmentIds, vm.Description))
        {
            if (attId > 0 && !vm.SelectedAttachmentIds.Contains(attId))
            {
                vm.SelectedAttachmentIds.Add(attId);
            }
        }

        if (proposal.TaskId.HasValue && existingTasks != null)
        {
            var matchedTask = existingTasks.FirstOrDefault(t => t.Id == proposal.TaskId.Value);
            if (matchedTask != null)
            {
                vm.SyncFromTask(matchedTask);
            }
        }

        vm.ResolveDependencies(existingTasks);

        return vm;
    }

    public void SyncFromTask(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        IsSyncedFromPersistedTask = true;
        CreatedTaskId = task.Id;
        Title = task.Title ?? string.Empty;
        Description = task.Description ?? string.Empty;
        Priority = task.Priority > 0 ? task.Priority : TaskPriority.Normal;
        Difficulty = task.Difficulty;
        IsAdded = task.Status != TaskOverallStatus.Draft &&
                  task.Status is not (TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected);

        SelectedDependencyIds = (task.Dependencies ?? Enumerable.Empty<TaskDependency>())
            .Select(d => d.DependsOnTaskId)
            .Where(id => id != task.Id)
            .Distinct()
            .ToList();
        DependsOnTitles.Clear();

        var taskAttachmentIds = task.AttachmentLinks?
            .Select(l => l.TaskAttachmentId)
            .Where(id => id > 0);
        SelectedAttachmentIds = AttachmentCitationParser
            .UnionAttachmentIds(taskAttachmentIds, task.Description)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    public void ResolveDependencies(IReadOnlyList<ForgeTask>? existingTasks)
    {
        if (CreatedTaskId.HasValue)
        {
            SelectedDependencyIds.RemoveAll(id => id == CreatedTaskId.Value);
        }

        if (existingTasks == null || existingTasks.Count == 0)
        {
            return;
        }

        var cancelledOrRejectedIds = existingTasks
            .Where(t => t.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected)
            .Select(t => t.Id)
            .ToHashSet();
        if (cancelledOrRejectedIds.Count > 0)
        {
            SelectedDependencyIds.RemoveAll(cancelledOrRejectedIds.Contains);
        }

        if (IsSyncedFromPersistedTask || DependsOnTitles.Count == 0)
        {
            return;
        }

        var activeTasks = existingTasks
            .Where(t => t.Id != CreatedTaskId && t.Status is not (TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected))
            .ToList();

        foreach (var depTitle in DependsOnTitles)
        {
            if (TryParseTaskId(depTitle, out var referencedId))
            {
                var byId = activeTasks.FirstOrDefault(t => t.Id == referencedId);
                if (byId != null)
                {
                    if (!SelectedDependencyIds.Contains(byId.Id))
                    {
                        SelectedDependencyIds.Add(byId.Id);
                    }
                    continue;
                }
            }

            var matched = activeTasks.FirstOrDefault(t =>
                string.Equals(t.Title, depTitle, StringComparison.OrdinalIgnoreCase) ||
                t.Title.Contains(depTitle, StringComparison.OrdinalIgnoreCase) ||
                depTitle.Contains(t.Title, StringComparison.OrdinalIgnoreCase));

            if (matched != null && !SelectedDependencyIds.Contains(matched.Id))
            {
                SelectedDependencyIds.Add(matched.Id);
            }
        }
    }

    private static bool TryParseTaskId(string reference, out int taskId)
    {
        var trimmed = reference.Trim().TrimStart('#');
        return int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out taskId);
    }
}

public sealed record IdeationContextLoadResult(string? SeedPrompt, int? HandledTaskId);

/// <summary>
/// Scoped Blazor circuit adapter managing Ideation presentation state, coordinator stream subscriptions,
/// turn rehydration, proposal promotion, and attachment intake outside Razor components.
/// </summary>
public class IdeationSessionState : IDisposable
{
    private readonly IIdeationConversationCoordinator _coordinator;
    private readonly IIdeationProposalService _proposalService;
    private readonly IdeationRefinementService _refinementService;
    private readonly ICompanionTriageDraftStore _companionDraftStore;
    private readonly IAttachmentUiService _attachmentUiService;
    private readonly ILogger<IdeationSessionState> _logger;

    private IdeationStreamSubscription? _streamSubscription;
    private Channel<IdeationStreamUpdate>? _streamChannel;
    private Task? _streamPumpTask;
    private long _subscriptionGeneration;
    private Func<Func<Task>, Task>? _uiDispatcher;
    private string? _lastAppliedDraftId;
    private string? _lastAppliedMemoryActionKey;
    private ChatMessage? _inFlightOptimisticUserMessage;
    private List<ConversationAttachmentChipViewModel>? _inFlightOptimisticChips;
    private bool _costAlreadyIncludesInFlightTurn;
    private bool _disposed;

    public IdeationSessionState(
        IIdeationConversationCoordinator coordinator,
        IIdeationProposalService proposalService,
        IdeationRefinementService refinementService,
        ICompanionTriageDraftStore companionDraftStore,
        IAttachmentUiService attachmentUiService,
        ILogger<IdeationSessionState> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _proposalService = proposalService ?? throw new ArgumentNullException(nameof(proposalService));
        _refinementService = refinementService ?? throw new ArgumentNullException(nameof(refinementService));
        _companionDraftStore = companionDraftStore ?? throw new ArgumentNullException(nameof(companionDraftStore));
        _attachmentUiService = attachmentUiService ?? throw new ArgumentNullException(nameof(attachmentUiService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<ChatMessage> Messages { get; } = new();
    public List<IdeationProposalViewModel> Proposals { get; } = new();
    public List<IdeationToolCallViewModel> CurrentToolCalls { get; } = new();
    public List<ChatContentPart> CurrentParts { get; } = new();
    public List<IdeationQuestion> PendingQuestions { get; } = new();
    public List<ConversationAttachmentChipViewModel> PendingComposerChips { get; } = new();
    public IReadOnlyList<ForgeTask> WorkspaceTasks { get; private set; } = Array.Empty<ForgeTask>();
    public IReadOnlyList<Conversation> WorkspaceSessions { get; private set; } = Array.Empty<Conversation>();
    public IReadOnlyList<TaskAttachment> WorkspaceAttachments { get; private set; } = Array.Empty<TaskAttachment>();

    public bool IsGenerating { get; set; }
    public string CurrentThinkingBuffer { get; set; } = string.Empty;
    public string CurrentMessageBuffer { get; set; } = string.Empty;
    public string? CustomSystemPrompt { get; set; }
    public int? BoundWorkspaceId { get; set; }
    public int? ActiveConversationId { get; set; }
    public string? ActiveConversationTitle { get; set; }
    public string? LatestTranscriptPath { get; set; }
    public string? LatestFullTranscriptMarkdown { get; set; }
    public TaskCostSummary ActiveSessionCost { get; set; } = TaskCostSummary.Empty;
    public OllamaRoutingTarget SelectedRoutingTarget { get; set; } = OllamaRoutingTarget.Auto;
    public string? SelectedModelOverride { get; set; }
    public VisionCapabilityStatus? ActiveVisionStatus { get; private set; }
    public bool HasPendingImageChips => PendingComposerChips.Any(c => c.IsImage);
    public string? VisionWarningMessage => AttachmentPresentationHelper.BuildTextOnlyImageWarning(ActiveVisionStatus, HasPendingImageChips);

    public IdeationSessionOptions CurrentOptions => new(
        SelectedRoutingTarget,
        SelectedModelOverride,
        CustomSystemPrompt);

    private IdeationSessionOptions? NonDefaultOptionsOrNull =>
        SelectedRoutingTarget == OllamaRoutingTarget.Auto &&
        string.IsNullOrWhiteSpace(SelectedModelOverride) &&
        string.IsNullOrWhiteSpace(CustomSystemPrompt)
            ? null
            : CurrentOptions;

    public IdeationQuestion? PendingQuestion
    {
        get => PendingQuestions.FirstOrDefault();
        set
        {
            CancelPendingQuestions();
            if (value != null)
            {
                PendingQuestions.Add(value);
            }
        }
    }

    public event Action? OnChange;
    public event Action<string, string, ToastLevel>? OnNotification;
    public event Action<bool>? OnScrollRequested;

    public void AttachUiDispatcher(Func<Func<Task>, Task> invokeAsync)
    {
        _uiDispatcher = invokeAsync ?? throw new ArgumentNullException(nameof(invokeAsync));
    }

    public void DetachUiDispatcher()
    {
        _uiDispatcher = null;
    }

    public void NotifyStateChanged() => OnChange?.Invoke();

    public void CancelPendingQuestions()
    {
        foreach (var pq in PendingQuestions)
        {
            pq.Cancel();
        }
        PendingQuestions.Clear();
    }

    public void AddMessage(ChatMessage message)
    {
        Messages.Add(message);
        NotifyStateChanged();
    }

    public void AddProposal(IdeationProposalViewModel proposal)
    {
        Proposals.Add(proposal);
        NotifyStateChanged();
    }

    public void RemoveProposal(IdeationProposalViewModel proposal)
    {
        RemoveProposalCore(proposal);
        NotifyStateChanged();
    }

    public static bool IsCancelledOrRejectedInWorkspace(IdeationProposal proposal, IReadOnlyList<ForgeTask>? workspaceTasks)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.TaskId.HasValue || workspaceTasks == null)
        {
            return false;
        }

        var matched = workspaceTasks.FirstOrDefault(t => t.Id == proposal.TaskId.Value);
        return matched != null && matched.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected;
    }

    public static bool IsMissingPersistedInWorkspace(IdeationProposal proposal, IReadOnlyList<ForgeTask>? workspaceTasks)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.TaskId.HasValue || workspaceTasks == null)
        {
            return false;
        }

        return !workspaceTasks.Any(t => t.Id == proposal.TaskId.Value);
    }

    public IdeationProposalViewModel? TryAddProposal(
        IdeationProposal proposal,
        IReadOnlyList<ForgeTask>? workspaceTasks,
        bool skipMissingPersisted = false)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (IsCancelledOrRejectedInWorkspace(proposal, workspaceTasks) ||
            (skipMissingPersisted && IsMissingPersistedInWorkspace(proposal, workspaceTasks)) ||
            HasMatchingProposal(proposal))
        {
            return null;
        }

        var vm = IdeationProposalViewModel.FromProposal(proposal, workspaceTasks);
        Proposals.Add(vm);
        return vm;
    }

    public void ReconcileProposedTasks(
        IEnumerable<IdeationProposal>? proposedTasks,
        IReadOnlyList<ForgeTask>? workspaceTasks,
        bool removeMissing = false)
    {
        if (proposedTasks != null)
        {
            foreach (var proposal in proposedTasks)
            {
                TryAddProposal(proposal, workspaceTasks, skipMissingPersisted: removeMissing);
            }
        }

        SyncProposalsWithWorkspaceTasks(workspaceTasks, removeMissing);
    }

    public void SyncProposalsWithWorkspaceTasks(IReadOnlyList<ForgeTask>? workspaceTasks, bool removeMissing = false)
    {
        if (workspaceTasks == null)
        {
            return;
        }

        foreach (var proposal in Proposals.ToList())
        {
            if (proposal.CreatedTaskId.HasValue)
            {
                var matched = workspaceTasks.FirstOrDefault(t => t.Id == proposal.CreatedTaskId.Value);
                if (matched == null)
                {
                    if (removeMissing)
                    {
                        RemoveProposalCore(proposal);
                    }
                    continue;
                }

                if (matched.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected)
                {
                    RemoveProposalCore(proposal);
                    continue;
                }

                proposal.SyncFromTask(matched);
            }

            if (!proposal.IsAdded)
            {
                proposal.ResolveDependencies(workspaceTasks);
            }
        }

        NotifyStateChanged();
    }

    public void Reset()
    {
        Messages.Clear();
        Proposals.Clear();
        CurrentToolCalls.Clear();
        PendingQuestions.Clear();
        CurrentParts.Clear();
        PendingComposerChips.Clear();
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        LatestTranscriptPath = null;
        LatestFullTranscriptMarkdown = null;
        ActiveConversationId = null;
        ActiveConversationTitle = null;
        ActiveSessionCost = TaskCostSummary.Empty;
        SelectedRoutingTarget = OllamaRoutingTarget.Auto;
        SelectedModelOverride = null;
        ActiveVisionStatus = null;
        IsGenerating = false;
        _inFlightOptimisticUserMessage = null;
        _inFlightOptimisticChips = null;
        _costAlreadyIncludesInFlightTurn = false;
        NotifyStateChanged();
    }

    public void DetachStreamSubscription()
    {
        Interlocked.Increment(ref _subscriptionGeneration);
        _streamSubscription?.Dispose();
        _streamSubscription = null;
        _streamChannel?.Writer.TryComplete();
        _streamChannel = null;
        _streamPumpTask = null;
    }

    public void ClearDraftDeduplication()
    {
        _lastAppliedDraftId = null;
    }

    public void ClearMemoryActionDeduplication()
    {
        _lastAppliedMemoryActionKey = null;
    }

    public async Task<IdeationContextLoadResult> ReloadWorkspaceContextAsync(
        Workspace? activeWorkspace,
        int? taskId,
        string? draftId,
        IdeationMemoryActionRequest? memoryAction = null,
        CancellationToken cancellationToken = default)
    {
        if (activeWorkspace == null)
        {
            await ReleaseBoundSessionOnWorkspaceSwitchAsync(null, null, cancellationToken);
            WorkspaceTasks = Array.Empty<ForgeTask>();
            WorkspaceSessions = Array.Empty<Conversation>();
            WorkspaceAttachments = Array.Empty<TaskAttachment>();
            PendingComposerChips.Clear();
            ActiveVisionStatus = null;
            NotifyStateChanged();
            return new IdeationContextLoadResult(null, null);
        }

        int? handledTaskId = null;
        try
        {
            if (BoundWorkspaceId.HasValue && BoundWorkspaceId.Value != activeWorkspace.Id)
            {
                PendingComposerChips.Clear();
                await ReleaseBoundSessionOnWorkspaceSwitchAsync(BoundWorkspaceId, ActiveConversationId, cancellationToken);
                BoundWorkspaceId = null;
                ActiveConversationId = null;
                IsGenerating = false;
            }
            else if (BoundWorkspaceId == activeWorkspace.Id &&
                     ActiveConversationId.HasValue &&
                     IsGenerating &&
                     _streamSubscription == null &&
                     !_coordinator.IsConversationGenerating(ActiveConversationId.Value))
            {
                IsGenerating = false;
            }

            WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(activeWorkspace.Id, cancellationToken) ?? Array.Empty<ForgeTask>();
            WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(activeWorkspace.Id, cancellationToken) ?? Array.Empty<Conversation>();
            WorkspaceAttachments = await _attachmentUiService.LoadWorkspaceAttachmentsAsync(activeWorkspace.Id, cancellationToken) ?? Array.Empty<TaskAttachment>();

            if (taskId.HasValue)
            {
                handledTaskId = taskId.Value;
                var refinement = await TryInitializeTaskRefinementAsync(activeWorkspace.Id, taskId.Value, cancellationToken);
                if (refinement.Handled)
                {
                    NotifyStateChanged();
                    return new IdeationContextLoadResult(refinement.SeedPrompt, taskId.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(draftId))
            {
                var draftResult = await TryInitializeCompanionDraftAsync(activeWorkspace.Id, draftId, cancellationToken);
                if (draftResult.Handled)
                {
                    NotifyStateChanged();
                    return new IdeationContextLoadResult(draftResult.SeedPrompt, handledTaskId);
                }
            }

            if (!string.IsNullOrWhiteSpace(memoryAction?.Action))
            {
                var memoryResult = await TryInitializeMemoryActionAsync(
                    activeWorkspace.Id,
                    memoryAction.Action,
                    memoryAction.Scope,
                    memoryAction.Path,
                    cancellationToken);
                if (memoryResult.Handled)
                {
                    NotifyStateChanged();
                    return new IdeationContextLoadResult(memoryResult.SeedPrompt, handledTaskId);
                }
            }

            if (BoundWorkspaceId != activeWorkspace.Id)
            {
                await ReleaseBoundSessionOnWorkspaceSwitchAsync(BoundWorkspaceId, ActiveConversationId, cancellationToken);
                var handle = await _coordinator.GetOrCreateActiveConversationAsync(activeWorkspace.Id, NonDefaultOptionsOrNull, cancellationToken);
                WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(activeWorkspace.Id, cancellationToken);
                await AttachAndHydrateConversationAsync(activeWorkspace.Id, handle.Conversation.Id, cancellationToken);
            }
            else if (!ActiveConversationId.HasValue)
            {
                if (!IsGenerating)
                {
                    var handle = await _coordinator.GetOrCreateActiveConversationAsync(activeWorkspace.Id, NonDefaultOptionsOrNull, cancellationToken);
                    WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(activeWorkspace.Id, cancellationToken);
                    await AttachAndHydrateConversationAsync(activeWorkspace.Id, handle.Conversation.Id, cancellationToken);
                }
            }
            else
            {
                await AttachAndHydrateConversationAsync(activeWorkspace.Id, ActiveConversationId.Value, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            WorkspaceTasks = Array.Empty<ForgeTask>();
            WorkspaceSessions = Array.Empty<Conversation>();
            WorkspaceAttachments = Array.Empty<TaskAttachment>();
            OnNotification?.Invoke("Workspace Context Error", ex.Message, ToastLevel.Error);
        }

        NotifyStateChanged();
        return new IdeationContextLoadResult(null, handledTaskId);
    }

    public async Task<(bool Handled, string? SeedPrompt)> TryInitializeTaskRefinementAsync(
        int workspaceId,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        if (IsGenerating)
        {
            return (false, null);
        }

        try
        {
            var refinement = await _refinementService.InitializeTaskRefinementAsync(workspaceId, taskId, cancellationToken);
            WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(workspaceId, cancellationToken);

            await ReleaseConversationAffinityIfChangedAsync(
                BoundWorkspaceId ?? workspaceId,
                ActiveConversationId,
                refinement.Conversation.Id,
                cancellationToken);

            await AttachAndHydrateConversationAsync(workspaceId, refinement.Conversation.Id, cancellationToken);

            var seed = Messages.Count == 0 ? refinement.SeedPrompt : string.Empty;
            NotifyStateChanged();
            return (true, seed);
        }
        catch (Exception ex) when (ex is TaskNotFoundException or TaskCannotBeModifiedException)
        {
            OnNotification?.Invoke("Cannot Refine Task", ex.Message, ToastLevel.Error);
            return (false, string.Empty);
        }
    }

    public async Task<(bool Handled, string? SeedPrompt)> TryInitializeCompanionDraftAsync(
        int workspaceId,
        string? draftId,
        CancellationToken cancellationToken = default)
    {
        if (IsGenerating)
        {
            return (false, null);
        }

        if (!IdeationRefinementHelper.TryResolveCompanionDraft(
                _companionDraftStore,
                workspaceId,
                draftId,
                ref _lastAppliedDraftId,
                out var draft) || draft is null)
        {
            return (false, null);
        }

        var oldConvId = ActiveConversationId;
        var oldWsId = BoundWorkspaceId ?? workspaceId;

        var handle = await _coordinator.StartConversationAsync(
            workspaceId,
            title: draft.Title,
            options: CurrentOptions,
            cancellationToken: cancellationToken);

        await ReleaseConversationAffinityIfChangedAsync(oldWsId, oldConvId, handle.Conversation.Id, cancellationToken);
        WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(workspaceId, cancellationToken);
        await AttachAndHydrateConversationAsync(workspaceId, handle.Conversation.Id, cancellationToken);

        var seed = IdeationRefinementHelper.BuildCompanionSeedPrompt(draft);
        NotifyStateChanged();
        return (true, seed);
    }

    public async Task<(bool Handled, string? SeedPrompt)> TryInitializeMemoryActionAsync(
        int workspaceId,
        string? actionParam,
        string? scopeParam,
        string? pathParam,
        CancellationToken cancellationToken = default)
    {
        if (!IdeationMemoryPromptHelper.TryParseActionQueryValue(actionParam, out var action))
        {
            return (false, null);
        }

        if (action == MemoryIdeationAction.DiscussSelectedFile && string.IsNullOrWhiteSpace(pathParam))
        {
            return (false, null);
        }

        var key = IdeationMemoryPromptHelper.BuildDeduplicationKey(workspaceId, actionParam, scopeParam, pathParam);
        if (string.Equals(_lastAppliedMemoryActionKey, key, StringComparison.Ordinal))
        {
            return (false, null);
        }

        if (IsGenerating)
        {
            _lastAppliedMemoryActionKey = key;
            OnNotification?.Invoke(
                "Ideation Busy",
                "Wait for the current Ideation turn to finish before starting a memory prompt.",
                ToastLevel.Warning);
            return (false, null);
        }

        var previousKey = _lastAppliedMemoryActionKey;
        _lastAppliedMemoryActionKey = key;

        NightCodeForge.Core.Memory.AgentMemoryScope? parsedScope =
            MemoryPresentationHelper.TryParseScopeQueryValue(scopeParam, out var scope)
                ? scope
                : null;

        var oldConvId = ActiveConversationId;
        var oldWsId = BoundWorkspaceId ?? workspaceId;

        try
        {
            var title = IdeationMemoryPromptHelper.BuildConversationTitle(action, pathParam);
            var handle = await _coordinator.StartConversationAsync(
                workspaceId,
                title: title,
                options: CurrentOptions,
                cancellationToken: cancellationToken);

            await ReleaseConversationAffinityIfChangedAsync(oldWsId, oldConvId, handle.Conversation.Id, cancellationToken);
            WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(workspaceId, cancellationToken);
            await AttachAndHydrateConversationAsync(workspaceId, handle.Conversation.Id, cancellationToken);
        }
        catch
        {
            _lastAppliedMemoryActionKey = previousKey;
            throw;
        }

        var seed = IdeationMemoryPromptHelper.BuildPrompt(action, parsedScope, pathParam);
        NotifyStateChanged();
        return (true, seed);
    }

    public async Task SelectSessionAsync(
        int workspaceId,
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        if (IsGenerating)
        {
            return;
        }

        try
        {
            await ReleaseConversationAffinityIfChangedAsync(
                BoundWorkspaceId ?? workspaceId,
                ActiveConversationId,
                conversationId,
                cancellationToken);

            var conv = await AttachAndHydrateConversationAsync(workspaceId, conversationId, cancellationToken);
            if (conv != null)
            {
                OnNotification?.Invoke("Session Loaded", conv.Title ?? $"Session #{conv.Id}", ToastLevel.Info);
                OnScrollRequested?.Invoke(true);
                NotifyStateChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed selecting ideation session #{ConversationId}.", conversationId);
            OnNotification?.Invoke("Session Load Failed", ex.Message, ToastLevel.Error);
        }
    }

    public async Task<bool> SendMessageAsync(
        Workspace? workspace,
        string? rawPrompt,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null || !AttachmentPresentationHelper.CanSubmitIdeationComposer(
                rawPrompt,
                PendingComposerChips,
                hasActiveWorkspace: true,
                isGenerating: IsGenerating))
        {
            return false;
        }

        var prompt = AttachmentPresentationHelper.BuildEffectivePromptWithCitations(rawPrompt, PendingComposerChips);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        bool hasImagePending = PendingComposerChips.Any(c => c.IsImage && c.AttachmentId is > 0);
        var citedIds = AttachmentCitationParser.ExtractAttachmentIds(prompt);
        bool hasCitedImage = citedIds
            .Any(id => WorkspaceAttachments.Any(a => a.Id == id && a.ContentKind == TaskAttachmentContentKind.Image));
        if (hasImagePending || hasCitedImage)
        {
            var visionStatus = ActiveVisionStatus
                ?? await _attachmentUiService.EvaluateIdeationVisionAsync(workspace.Id, SelectedModelOverride, cancellationToken);
            if (visionStatus != null)
            {
                ActiveVisionStatus = visionStatus;
            }

            if (visionStatus is { SupportsVision: false })
            {
                var warning = AttachmentPresentationHelper.BuildTextOnlyImageWarning(visionStatus, hasImageAttachments: true)
                    ?? "Active model does not support vision. Select a vision-capable model or remove image attachments.";
                OnNotification?.Invoke("Vision Not Supported", warning, ToastLevel.Error);
                NotifyStateChanged();
                return false;
            }
        }

        var messageChips = new List<ConversationAttachmentChipViewModel>(PendingComposerChips);
        PendingComposerChips.Clear();

        var userMessage = new ChatMessage(
            "user",
            prompt,
            Timestamp: DateTime.UtcNow,
            Parts: [new TextContentPart(prompt)],
            AttachmentChips: messageChips);
        Messages.Add(userMessage);
        IsGenerating = true;
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        CurrentToolCalls.Clear();
        CurrentParts.Clear();
        NotifyStateChanged();
        OnScrollRequested?.Invoke(true);

        try
        {
            if (citedIds.Count > 0)
            {
                var citedChips = await _attachmentUiService.ResolveCitedAttachmentChipsAsync(
                    workspace.Id,
                    prompt,
                    cancellationToken);
                foreach (var chip in citedChips)
                {
                    AttachmentPresentationHelper.UpsertChip(messageChips, chip);
                }

                NotifyStateChanged();
            }

            foreach (var chip in messageChips)
            {
                await PopulateChipThumbnailBestEffortAsync(chip, cancellationToken);
            }

            if (!ActiveConversationId.HasValue)
            {
                var handle = await _coordinator.StartConversationAsync(
                    workspace.Id,
                    title: ChatPresentationHelper.Truncate(prompt, 50),
                    options: CurrentOptions,
                    cancellationToken: cancellationToken);
                BoundWorkspaceId = workspace.Id;
                ActiveConversationId = handle.Conversation.Id;
                ActiveConversationTitle = handle.Conversation.Title;
                WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(workspace.Id, cancellationToken);
            }

            EnsureSubscribedToConversation(ActiveConversationId.Value);
            _inFlightOptimisticUserMessage = userMessage;
            _inFlightOptimisticChips = messageChips;

            var userMetadataJson = IdeationTurnRehydrationMapper.SerializeUserAttachmentMetadata(messageChips);
            var started = await _coordinator.StartTurnAsync(
                new IdeationSendRequest(
                    workspace.Id,
                    ActiveConversationId.Value,
                    prompt,
                    CurrentOptions,
                    userMetadataJson),
                cancellationToken);

            if (!started)
            {
                _inFlightOptimisticUserMessage = null;
                _inFlightOptimisticChips = null;
                Messages.Remove(userMessage);
                RestorePendingComposerChips(messageChips);
                await AttachAndHydrateConversationAsync(workspace.Id, ActiveConversationId.Value, cancellationToken);
                OnNotification?.Invoke("Turn Already Active", "Another turn is already running in this conversation.", ToastLevel.Warning);
                NotifyStateChanged();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _inFlightOptimisticUserMessage = null;
            _inFlightOptimisticChips = null;
            Messages.Remove(userMessage);
            RestorePendingComposerChips(messageChips);
            IsGenerating = false;
            CurrentThinkingBuffer = string.Empty;
            CurrentMessageBuffer = string.Empty;
            CurrentToolCalls.Clear();
            CurrentParts.Clear();
            OnNotification?.Invoke("Generation Error", ex.Message, ToastLevel.Error);
            NotifyStateChanged();
            return false;
        }
    }

    public async Task SubmitQuestionAnswersAsync(
        QuestionContentPart qp,
        IReadOnlyDictionary<int, string> answersByIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qp);
        if (answersByIndex == null || answersByIndex.Count == 0)
        {
            return;
        }

        var (formattedAnswer, metadataJson) = IdeationPresentationHelper.FormatSubmission(answersByIndex, qp.EffectiveQuestions);
        qp.Answer = formattedAnswer;
        qp.MetadataJson = metadataJson;

        if (ActiveConversationId.HasValue)
        {
            var handledByCoordinator = await _coordinator.SubmitQuestionAnswersAsync(
                ActiveConversationId.Value,
                qp.GroupId,
                answersByIndex,
                formattedAnswer,
                metadataJson,
                cancellationToken);

            if (!handledByCoordinator)
            {
                var pending = qp.PendingQuestions ?? PendingQuestions;
                IdeationPresentationHelper.SubmitAnswersToPendingQuestions(pending, answersByIndex);
            }
        }
        else
        {
            var pending = qp.PendingQuestions ?? PendingQuestions;
            IdeationPresentationHelper.SubmitAnswersToPendingQuestions(pending, answersByIndex);
        }

        PendingQuestions.Clear();
        NotifyStateChanged();
        OnScrollRequested?.Invoke(true);
    }

    public async Task CancelGenerationAsync(CancellationToken cancellationToken = default)
    {
        if (!ActiveConversationId.HasValue)
        {
            return;
        }

        try
        {
            CancelPendingQuestions();
            await _coordinator.CancelActiveTurnAsync(ActiveConversationId.Value, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed cancelling active ideation turn for conversation #{ConversationId}.", ActiveConversationId.Value);
        }
    }

    public async Task ResetSessionAsync(Workspace? activeWorkspace, CancellationToken cancellationToken = default)
    {
        var previousConversationId = ActiveConversationId;
        var previousOptions = CurrentOptions;

        try
        {
            if (activeWorkspace != null)
            {
                DetachStreamSubscription();
                var handle = await _coordinator.StartConversationAsync(
                    activeWorkspace.Id,
                    title: null,
                    archivePreviousConversationId: previousConversationId,
                    archiveSummary: previousConversationId.HasValue ? "Session archived by user via Clear Chat." : null,
                    options: new IdeationSessionOptions(
                        OllamaRoutingTarget.Auto,
                        null,
                        previousOptions.CustomSystemPrompt),
                    cancellationToken: cancellationToken);

                Reset();
                CustomSystemPrompt = previousOptions.CustomSystemPrompt;
                BoundWorkspaceId = activeWorkspace.Id;
                ActiveConversationId = handle.Conversation.Id;
                ActiveConversationTitle = handle.Conversation.Title;
                WorkspaceSessions = await _coordinator.GetWorkspaceConversationsAsync(activeWorkspace.Id, cancellationToken);
                EnsureSubscribedToConversation(handle.Conversation.Id);
                await RefreshVisionStatusAsync(activeWorkspace.Id, cancellationToken);
                OnNotification?.Invoke("New Session Started", "Previous session archived and fresh chat initialized.", ToastLevel.Info);
            }
            else
            {
                DetachStreamSubscription();
                Reset();
            }

            OnScrollRequested?.Invoke(true);
            NotifyStateChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed resetting ideation session.");
            OnNotification?.Invoke("Session Reset Failed", ex.Message, ToastLevel.Error);
        }
    }

    public async Task ApplyConfigChangeAsync(CancellationToken cancellationToken = default)
    {
        NotifyStateChanged();
        if (BoundWorkspaceId.HasValue)
        {
            await RefreshVisionStatusAsync(BoundWorkspaceId.Value, cancellationToken);
        }

        if (BoundWorkspaceId.HasValue && ActiveConversationId.HasValue)
        {
            try
            {
                await _coordinator.UpdateSessionOptionsAsync(
                    BoundWorkspaceId.Value,
                    ActiveConversationId.Value,
                    CurrentOptions,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed applying ideation session configuration for conversation #{ConversationId}.", ActiveConversationId.Value);
                OnNotification?.Invoke("Configuration Error", ex.Message, ToastLevel.Error);
            }
        }
    }

    public async Task RefreshVisionStatusAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            ActiveVisionStatus = await _attachmentUiService
                .EvaluateIdeationVisionAsync(workspaceId, SelectedModelOverride, cancellationToken);
            NotifyStateChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed evaluating Ideation vision capability for workspace #{WorkspaceId}.", workspaceId);
        }
    }

    public async Task AddProposalToBacklogAsync(
        Workspace? workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null)
        {
            return;
        }

        try
        {
            var result = await _proposalService.PromoteOrCreateProposalAsync(workspace, proposal, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.MarkdownSyncWarning))
            {
                OnNotification?.Invoke("Markdown Sync Warning", result.MarkdownSyncWarning, ToastLevel.Warning);
            }

            WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(workspace.Id, cancellationToken);
            SyncProposalsWithWorkspaceTasks(WorkspaceTasks);

            OnNotification?.Invoke("Added to Backlog", $"ForgeTask #{result.CreatedTask.Id} created with status Suggested.", ToastLevel.Success);
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            OnNotification?.Invoke("Creation Error", $"Failed to add proposal to backlog: {ex.Message}", ToastLevel.Error);
        }
    }

    public async Task AddAllProposalsToBacklogAsync(
        Workspace? workspace,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null)
        {
            return;
        }

        var pendingProposals = Proposals.Where(p => !p.IsAdded && !string.IsNullOrWhiteSpace(p.Title)).ToList();
        if (pendingProposals.Count == 0)
        {
            return;
        }

        int addedCount = 0;
        foreach (var proposal in pendingProposals)
        {
            await AddProposalToBacklogAsync(workspace, proposal, cancellationToken);
            if (proposal.IsAdded)
            {
                addedCount++;
            }
        }

        OnNotification?.Invoke("All Proposals Ingested", $"Successfully added {addedCount} proposal(s) sequentially into the backlog.", ToastLevel.Success);
    }

    public async Task DismissProposalAsync(
        Workspace? workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _proposalService.PurgeDraftProposalAsync(workspace, proposal, cancellationToken);
            RemoveProposal(proposal);

            if (workspace != null && proposal.CreatedTaskId.HasValue)
            {
                WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(workspace.Id, cancellationToken);
            }

            OnNotification?.Invoke("Proposal Dismissed", $"Proposal '{proposal.Title}' removed.", ToastLevel.Info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed dismissing ideation proposal '{Title}'.", proposal?.Title);
            OnNotification?.Invoke("Dismissal Error", ex.Message, ToastLevel.Error);
        }
    }

    public async Task UploadAttachmentFileAsync(
        Workspace? workspace,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null)
        {
            return;
        }

        var chip = ConversationAttachmentChipViewModel.CreatePendingUpload(fileName);
        AttachmentPresentationHelper.UpsertChip(PendingComposerChips, chip);
        NotifyStateChanged();

        try
        {
            var ingested = await _attachmentUiService.UploadFileAsync(workspace.Id, fileName, content, cancellationToken);
            chip.ApplyIngested(ingested);
            await PopulateChipThumbnailBestEffortAsync(chip, cancellationToken);
            AttachmentPresentationHelper.UpsertChip(PendingComposerChips, chip);
            WorkspaceAttachments = await _attachmentUiService.LoadWorkspaceAttachmentsAsync(workspace.Id, cancellationToken);
            OnNotification?.Invoke("Attachment Stored", $"#{ingested.Id} {ingested.Title} added to workspace attachments.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed uploading attachment file '{FileName}' in Ideation.", fileName);
            PendingComposerChips.Remove(chip);
            OnNotification?.Invoke("Attachment Upload Failed", ex.Message, ToastLevel.Error);
        }

        NotifyStateChanged();
    }

    public void ReportUnsupportedAttachmentUpload(string fileName)
    {
        var message = AttachmentConstants.BuildUnsupportedFormatMessage(fileName, Path.GetExtension(fileName));
        _logger.LogWarning("Rejected unsupported attachment upload '{FileName}' in Ideation: {Reason}", fileName, message);
        OnNotification?.Invoke("Unsupported Attachment", message, ToastLevel.Warning);
    }

    public void ReportAttachmentUploadError(string? fileName, string? errorMessage)
    {
        var effectiveMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "Failed uploading attachment file."
            : errorMessage;
        _logger.LogWarning(
            "Failed reading or uploading attachment '{FileName}' in Ideation: {Reason}",
            string.IsNullOrWhiteSpace(fileName) ? "(batch)" : fileName,
            effectiveMessage);
        OnNotification?.Invoke("Attachment Upload Failed", effectiveMessage, ToastLevel.Error);
    }

    public async Task PasteImageFromClipboardAsync(
        Workspace? workspace,
        string? fileName,
        string? contentType,
        string? base64Data,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null)
        {
            return;
        }

        if (!AttachmentPresentationHelper.TryDecodeClipboardImage(
                base64Data,
                fileName,
                contentType,
                out var bytes,
                out var normalizedFileName,
                out var errorMessage))
        {
            ReportClipboardImageError(fileName, errorMessage);
            return;
        }

        await using var ms = new MemoryStream(bytes, writable: false);
        await UploadAttachmentFileAsync(workspace, normalizedFileName, ms, cancellationToken);
    }

    public void ReportClipboardImageError(string? fileName, string? errorMessage)
    {
        var effectiveError = string.IsNullOrWhiteSpace(errorMessage) ? "Invalid pasted image." : errorMessage;
        _logger.LogWarning("Rejected clipboard image paste '{FileName}' in Ideation: {Reason}", fileName, effectiveError);
        OnNotification?.Invoke("Clipboard Image Error", effectiveError, ToastLevel.Error);
        NotifyStateChanged();
    }

    public async Task<bool> AttachHostFileAsync(
        Workspace? workspace,
        string? rawHostPath,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null || string.IsNullOrWhiteSpace(rawHostPath))
        {
            return false;
        }

        var hostPath = rawHostPath.Trim();
        var chip = ConversationAttachmentChipViewModel.CreatePendingHostPath(hostPath);
        AttachmentPresentationHelper.UpsertChip(PendingComposerChips, chip);
        NotifyStateChanged();

        try
        {
            var ingested = await _attachmentUiService.AttachHostFileAsync(workspace.Id, hostPath, cancellationToken);
            chip.ApplyIngested(ingested);
            await PopulateChipThumbnailBestEffortAsync(chip, cancellationToken);
            AttachmentPresentationHelper.UpsertChip(PendingComposerChips, chip);
            WorkspaceAttachments = await _attachmentUiService.LoadWorkspaceAttachmentsAsync(workspace.Id, cancellationToken);
            OnNotification?.Invoke("Host File Attached", $"#{ingested.Id} {ingested.Title} mapped as symlink.", ToastLevel.Success);
            NotifyStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed attaching host file '{HostPath}' in Ideation.", hostPath);
            PendingComposerChips.Remove(chip);
            OnNotification?.Invoke("Host File Attach Failed", ex.Message, ToastLevel.Error);
            NotifyStateChanged();
            return false;
        }
    }

    public void RemovePendingChip(ConversationAttachmentChipViewModel chip)
    {
        PendingComposerChips.Remove(chip);
        NotifyStateChanged();
    }

    public async Task<string> HandleAttachmentDeletedAsync(
        Workspace? workspace,
        int deletedAttachmentId,
        string currentInput,
        CancellationToken cancellationToken = default)
    {
        var deletedTitle = WorkspaceAttachments.FirstOrDefault(a => a.Id == deletedAttachmentId)?.Title
            ?? PendingComposerChips.FirstOrDefault(c => c.AttachmentId == deletedAttachmentId)?.Title;

        PendingComposerChips.RemoveAll(c => c.AttachmentId == deletedAttachmentId);
        var updatedInput = AttachmentPresentationHelper.RemoveAttachmentCitation(currentInput, deletedAttachmentId, deletedTitle);
        foreach (var proposal in Proposals)
        {
            proposal.RemoveAttachment(deletedAttachmentId, deletedTitle);
        }

        if (workspace != null)
        {
            WorkspaceAttachments = await _attachmentUiService.LoadWorkspaceAttachmentsAsync(workspace.Id, cancellationToken);
            WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(workspace.Id, cancellationToken);
        }
        else
        {
            WorkspaceAttachments = Array.Empty<TaskAttachment>();
        }

        NotifyStateChanged();
        return updatedInput;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachStreamSubscription();
        DetachUiDispatcher();
    }

    private async Task<Conversation?> AttachAndHydrateConversationAsync(
        int workspaceId,
        int conversationId,
        CancellationToken cancellationToken)
    {
        var previousConversationId = ActiveConversationId;
        var switchingConversation = previousConversationId.HasValue && previousConversationId.Value != conversationId;
        var previousCost = ActiveSessionCost;
        var preservedTarget = SelectedRoutingTarget;
        var preservedModel = SelectedModelOverride;
        var preservedPrompt = CustomSystemPrompt;

        DetachStreamSubscription();
        _inFlightOptimisticUserMessage = null;
        _inFlightOptimisticChips = null;
        _costAlreadyIncludesInFlightTurn = false;

        var channel = Channel.CreateUnbounded<IdeationStreamUpdate>(new UnboundedChannelOptions { SingleReader = true });
        var generation = Volatile.Read(ref _subscriptionGeneration);
        var sub = _coordinator.Subscribe(conversationId, u => channel.Writer.TryWrite(u));
        _streamSubscription = sub;
        _streamChannel = channel;

        var optionsToLoad = switchingConversation ? null : NonDefaultOptionsOrNull;
        var handle = await _coordinator.LoadConversationAsync(workspaceId, conversationId, optionsToLoad, cancellationToken);
        if (handle == null)
        {
            DetachStreamSubscription();
            return null;
        }

        var conv = handle.Conversation;
        bool hasBufferedUpdates = channel.Reader.TryPeek(out _);
        var snapshot = sub.InitialSnapshot ?? (!hasBufferedUpdates ? handle.InFlightTurn : null);
        var liveInFlightHint = snapshot ?? (hasBufferedUpdates ? handle.InFlightTurn : null);
        var preservePreSeededWorkspaceState =
            !previousConversationId.HasValue &&
            BoundWorkspaceId == (conv.WorkspaceId ?? workspaceId) &&
            conv.Turns.Count == 0 &&
            liveInFlightHint is not { IsGenerating: true } &&
            (Messages.Count > 0 || Proposals.Count > 0);

        if (!preservePreSeededWorkspaceState)
        {
            Messages.Clear();
            Proposals.Clear();
        }
        CurrentToolCalls.Clear();
        PendingQuestions.Clear();
        CurrentParts.Clear();
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        var resolvedOptions = liveInFlightHint?.Options ?? handle.SessionOptions;
        LatestTranscriptPath = liveInFlightHint?.LatestTranscriptPath ?? handle.LatestTranscriptPath;
        LatestFullTranscriptMarkdown = liveInFlightHint?.LatestFullTranscriptMarkdown ?? handle.LatestFullTranscriptMarkdown;
        BoundWorkspaceId = conv.WorkspaceId ?? workspaceId;
        ActiveConversationId = conv.Id;
        ActiveConversationTitle = conv.Title;
        SelectedRoutingTarget = resolvedOptions?.RoutingTarget ?? (switchingConversation ? OllamaRoutingTarget.Auto : preservedTarget);
        SelectedModelOverride = resolvedOptions != null ? resolvedOptions.ModelOverride : (switchingConversation ? null : preservedModel);
        CustomSystemPrompt = resolvedOptions != null ? resolvedOptions.CustomSystemPrompt : (switchingConversation ? null : preservedPrompt);

        var computedCost = TaskCostPresentationHelper.ComputeConversationCost(conv);
        ActiveSessionCost = (computedCost.HasCostData || conv.Turns.Count > 0 || (previousConversationId.HasValue && previousConversationId.Value != conv.Id))
            ? computedCost
            : previousCost;

        if (!preservePreSeededWorkspaceState)
        {
            var maxTurnIndex = liveInFlightHint is { IsGenerating: true } ? liveInFlightHint.ActiveUserTurnIndex : null;
            var projected = IdeationTurnRehydrationMapper.ProjectConversation(
                conv,
                WorkspaceAttachments,
                WorkspaceTasks,
                maxTurnIndex,
                _logger);

            Messages.AddRange(projected.Messages);
            foreach (var proposal in projected.Proposals)
            {
                TryAddProposal(proposal, WorkspaceTasks, skipMissingPersisted: true);
            }
        }

        if (snapshot is { IsGenerating: true })
        {
            _costAlreadyIncludesInFlightTurn = snapshot.ActiveUserTurnIndex.HasValue
                && conv.Turns.Any(t => t.TurnIndex > snapshot.ActiveUserTurnIndex.Value && t.Role == StageTurnRole.Assistant);

            var inFlight = IdeationTurnRehydrationMapper.ProjectInFlightSnapshot(snapshot);
            IsGenerating = true;
            CurrentThinkingBuffer = inFlight.ThinkingBuffer;
            CurrentMessageBuffer = inFlight.MessageBuffer;
            CurrentToolCalls.AddRange(inFlight.ToolCalls);
            CurrentParts.AddRange(inFlight.Parts);
            PendingQuestions.AddRange(inFlight.PendingQuestions);

            foreach (var proposal in snapshot.DiscoveredProposals)
            {
                TryAddProposal(proposal, WorkspaceTasks, skipMissingPersisted: false);
            }
        }
        else if (liveInFlightHint is { IsGenerating: true })
        {
            _costAlreadyIncludesInFlightTurn = liveInFlightHint.ActiveUserTurnIndex.HasValue
                && conv.Turns.Any(t => t.TurnIndex > liveInFlightHint.ActiveUserTurnIndex.Value && t.Role == StageTurnRole.Assistant);
            IsGenerating = true;
        }
        else
        {
            IsGenerating = false;
        }

        if (!preservePreSeededWorkspaceState)
        {
            SyncProposalsWithWorkspaceTasks(WorkspaceTasks, removeMissing: true);
            await PopulateMessageThumbnailsBestEffortAsync(Messages, cancellationToken);
        }
        await RefreshVisionStatusAsync(workspaceId, cancellationToken);
        StartStreamPump(channel, generation);
        return conv;
    }

    private async Task PopulateMessageThumbnailsBestEffortAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var msg in messages)
        {
            if (msg.AttachmentChips is not { Count: > 0 })
            {
                continue;
            }

            foreach (var chip in msg.AttachmentChips)
            {
                await PopulateChipThumbnailBestEffortAsync(chip, cancellationToken);
            }
        }
    }

    private async Task PopulateChipThumbnailBestEffortAsync(
        ConversationAttachmentChipViewModel chip,
        CancellationToken cancellationToken)
    {
        if (!chip.IsImage || chip.AttachmentId is not > 0 || !string.IsNullOrEmpty(chip.ThumbnailDataUri))
        {
            return;
        }

        try
        {
            chip.ThumbnailDataUri = await _attachmentUiService
                .LoadImageThumbnailDataUriAsync(chip.AttachmentId.Value, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Best-effort thumbnail generation skipped for attachment #{AttachmentId}.", chip.AttachmentId);
        }
    }

    private void EnsureSubscribedToConversation(int conversationId)
    {
        if (_streamSubscription != null && ActiveConversationId == conversationId && _streamChannel != null)
        {
            return;
        }

        DetachStreamSubscription();
        var channel = Channel.CreateUnbounded<IdeationStreamUpdate>(new UnboundedChannelOptions { SingleReader = true });
        var generation = Volatile.Read(ref _subscriptionGeneration);
        _streamSubscription = _coordinator.Subscribe(conversationId, u => channel.Writer.TryWrite(u));
        _streamChannel = channel;
        StartStreamPump(channel, generation);
    }

    private void StartStreamPump(Channel<IdeationStreamUpdate> channel, long generation)
    {
        _streamPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (_disposed || generation != Volatile.Read(ref _subscriptionGeneration))
                    {
                        break;
                    }

                    try
                    {
                        var dispatcher = _uiDispatcher;
                        bool dispatched = false;
                        if (dispatcher != null)
                        {
                            bool enteredApply = false;
                            try
                            {
                                await dispatcher(() =>
                                {
                                    enteredApply = true;
                                    return ApplyStreamUpdateAsync(update, generation);
                                }).ConfigureAwait(false);
                                dispatched = true;
                            }
                            catch (Exception ex) when (!enteredApply && ex is ObjectDisposedException or InvalidOperationException)
                            {
                                // Circuit disconnected before entering ApplyStreamUpdateAsync; apply directly.
                            }
                        }

                        if (!dispatched)
                        {
                            await ApplyStreamUpdateAsync(update, generation).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error applying ideation stream update {Kind}.", update.Kind);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Channel closed on detach/dispose
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ideation stream pump exited unexpectedly.");
            }
        });
    }

    private async Task ApplyStreamUpdateAsync(IdeationStreamUpdate update, long generation)
    {
        if (_disposed || generation != Volatile.Read(ref _subscriptionGeneration))
        {
            return;
        }

        if (update.ConversationId.HasValue && ActiveConversationId.HasValue && update.ConversationId.Value != ActiveConversationId.Value)
        {
            return;
        }

        var wsId = BoundWorkspaceId;

        switch (update.Kind)
        {
            case IdeationStreamUpdateKind.ThinkingDelta when !string.IsNullOrEmpty(update.TextDelta):
                IsGenerating = true;
                CurrentThinkingBuffer += update.TextDelta;
                if (CurrentParts.LastOrDefault() is ThinkingContentPart lastThink)
                {
                    CurrentParts[^1] = new ThinkingContentPart(lastThink.Thinking + update.TextDelta);
                }
                else
                {
                    CurrentParts.Add(new ThinkingContentPart(update.TextDelta));
                }
                NotifyStateChanged();
                break;

            case IdeationStreamUpdateKind.MessageDelta when !string.IsNullOrEmpty(update.TextDelta):
                IsGenerating = true;
                CurrentMessageBuffer += update.TextDelta;
                if (CurrentParts.LastOrDefault() is TextContentPart lastText)
                {
                    CurrentParts[^1] = new TextContentPart(lastText.Text + update.TextDelta);
                }
                else
                {
                    CurrentParts.Add(new TextContentPart(update.TextDelta));
                }
                NotifyStateChanged();
                break;

            case IdeationStreamUpdateKind.ToolCallStarted when update.ToolCall != null:
                IsGenerating = true;
                var call = update.ToolCall;
                if (!CurrentToolCalls.Any(t => string.Equals(t.ToolCallId, call.ToolCallId, StringComparison.Ordinal)))
                {
                    var vm = new IdeationToolCallViewModel
                    {
                        ToolCallId = call.ToolCallId,
                        ToolName = call.ToolName,
                        Arguments = call.Arguments,
                        IsCompleted = false
                    };
                    CurrentToolCalls.Add(vm);
                    if (!string.Equals(call.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentParts.Add(new ToolCallContentPart(vm));
                    }
                }
                NotifyStateChanged();
                break;

            case IdeationStreamUpdateKind.ToolCallCompleted when update.ToolCall != null:
                var comp = update.ToolCall;
                var match = CurrentToolCalls.FirstOrDefault(t => string.Equals(t.ToolCallId, comp.ToolCallId, StringComparison.Ordinal));
                if (match != null)
                {
                    match.Result = comp.Result;
                    match.IsCompleted = true;
                    match.IsError = comp.IsError;
                    match.DurationMs = comp.DurationMs;
                }

                var partMatch = CurrentParts.OfType<ToolCallContentPart>()
                    .FirstOrDefault(p => string.Equals(p.ToolCall.ToolCallId, comp.ToolCallId, StringComparison.Ordinal));
                if (partMatch != null)
                {
                    partMatch.ToolCall.Result = comp.Result;
                    partMatch.ToolCall.IsCompleted = true;
                    partMatch.ToolCall.IsError = comp.IsError;
                    partMatch.ToolCall.DurationMs = comp.DurationMs;
                }

                if (string.Equals(comp.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase) &&
                    (!string.IsNullOrWhiteSpace(comp.Result) || !string.IsNullOrWhiteSpace(update.ToolMetadataJson)))
                {
                    var qPart = CurrentParts.OfType<QuestionContentPart>().LastOrDefault(q => !q.IsAnswered)
                        ?? CurrentParts.OfType<QuestionContentPart>().LastOrDefault();
                    if (qPart != null)
                    {
                        if (!string.IsNullOrWhiteSpace(update.ToolMetadataJson) && string.IsNullOrWhiteSpace(qPart.MetadataJson))
                        {
                            qPart.MetadataJson = update.ToolMetadataJson;
                        }

                        if (!qPart.IsAnswered)
                        {
                            qPart.Answer = IdeationTurnRehydrationMapper.ResolveCleanQuestionAnswer(
                                comp.Result,
                                update.ToolMetadataJson ?? qPart.MetadataJson);
                        }

                        if (qPart.IsAnswered)
                        {
                            PendingQuestions.Clear();
                        }
                    }
                }

                if (!comp.IsError && wsId.HasValue && IdeationPresentationHelper.RequiresWorkspaceTaskSync(comp.ToolName))
                {
                    WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(wsId.Value);
                    SyncProposalsWithWorkspaceTasks(WorkspaceTasks);
                }
                NotifyStateChanged();
                break;

            case IdeationStreamUpdateKind.QuestionAsked:
                IsGenerating = true;
                var batch = update.Questions ?? (update.Question != null ? new[] { update.Question } : Array.Empty<IdeationQuestion>());
                if (batch.Count > 0)
                {
                    PendingQuestions.Clear();
                    PendingQuestions.AddRange(batch);

                    var questionItems = IdeationPresentationHelper.MapToQuestionItems(batch);
                    var existingPart = CurrentParts.OfType<QuestionContentPart>().LastOrDefault(q => !q.IsAnswered);
                    if (existingPart == null)
                    {
                        CurrentParts.Add(new QuestionContentPart
                        {
                            GroupId = update.QuestionGroupId ?? Guid.NewGuid(),
                            Questions = questionItems,
                            PendingQuestions = batch
                        });
                    }
                    else
                    {
                        existingPart.Questions = questionItems;
                        existingPart.PendingQuestions = batch;
                    }
                }
                NotifyStateChanged();
                OnScrollRequested?.Invoke(true);
                break;

            case IdeationStreamUpdateKind.ProposalDiscovered when update.Proposal != null:
                if (update.Proposal.TaskId.HasValue && wsId.HasValue)
                {
                    WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(wsId.Value);
                }

                var newVm = TryAddProposal(update.Proposal, WorkspaceTasks);
                if (newVm != null)
                {
                    SyncProposalsWithWorkspaceTasks(WorkspaceTasks);
                    OnNotification?.Invoke("New Proposal Discovered", newVm.Title, ToastLevel.Info);
                    NotifyStateChanged();
                }
                break;

            case IdeationStreamUpdateKind.OptimizationNotice:
                CurrentParts.Add(new OptimizationContentPart(
                    update.TextDelta ?? "Context compacted",
                    update.PreCompactionTokens,
                    update.PostCompactionTokens,
                    update.DurationMs));
                NotifyStateChanged();
                break;

            case IdeationStreamUpdateKind.Completed when update.FinalResult != null:
                await HandleTurnCompletedAsync(update.FinalResult, wsId);
                break;

            case IdeationStreamUpdateKind.Error:
                PendingQuestions.Clear();
                var hadCompletedTools = CurrentToolCalls.Any(t => t.IsCompleted);
                if (hadCompletedTools)
                {
                    var errorContent = string.IsNullOrEmpty(CurrentMessageBuffer)
                        ? IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice
                        : $"{CurrentMessageBuffer}\n\n{IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice}";
                    var partsForErrorMessage = CurrentParts.Count > 0 ? CurrentParts.ToList() : new List<ChatContentPart>();
                    partsForErrorMessage.Add(new TextContentPart(IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice));
                    var toolsForErrorMessage = CurrentToolCalls.Select(t => new IdeationToolCallViewModel
                    {
                        ToolCallId = t.ToolCallId,
                        ToolName = t.ToolName,
                        Arguments = t.Arguments,
                        Result = t.Result,
                        IsCompleted = t.IsCompleted,
                        IsError = t.IsError,
                        DurationMs = t.DurationMs
                    }).ToList();

                    Messages.Add(new ChatMessage(
                        "assistant",
                        errorContent,
                        string.IsNullOrEmpty(CurrentThinkingBuffer) ? null : CurrentThinkingBuffer,
                        DateTime.UtcNow,
                        toolsForErrorMessage,
                        partsForErrorMessage));
                }
                else if (_inFlightOptimisticUserMessage != null)
                {
                    Messages.Remove(_inFlightOptimisticUserMessage);
                    if (_inFlightOptimisticChips is { Count: > 0 })
                    {
                        RestorePendingComposerChips(_inFlightOptimisticChips);
                    }
                }

                _inFlightOptimisticUserMessage = null;
                _inFlightOptimisticChips = null;
                _costAlreadyIncludesInFlightTurn = false;
                CurrentThinkingBuffer = string.Empty;
                CurrentMessageBuffer = string.Empty;
                CurrentToolCalls.Clear();
                CurrentParts.Clear();
                OnNotification?.Invoke("Generation Error", update.ErrorMessage ?? "Ideation turn failed.", ToastLevel.Error);
                IsGenerating = false;
                NotifyStateChanged();
                OnScrollRequested?.Invoke(true);
                break;
        }
    }

    private async Task HandleTurnCompletedAsync(IdeationResult result, int? wsId)
    {
        if (!string.IsNullOrWhiteSpace(result.TranscriptPath))
        {
            LatestTranscriptPath = result.TranscriptPath;
        }
        if (!string.IsNullOrWhiteSpace(result.FullTranscriptMarkdown))
        {
            LatestFullTranscriptMarkdown = result.FullTranscriptMarkdown;
        }

        var skipCostAccumulation = _costAlreadyIncludesInFlightTurn;
        _costAlreadyIncludesInFlightTurn = false;
        if (result.UsageDelta != null && !skipCostAccumulation)
        {
            ActiveSessionCost = TaskCostPresentationHelper.AccumulateCost(ActiveSessionCost, result.UsageDelta);
        }

        if (result.IsCancelled)
        {
            OnNotification?.Invoke("Generation Cancelled", "Exploration agent stopped.", ToastLevel.Warning);
        }

        foreach (var qp in CurrentParts.OfType<QuestionContentPart>().Where(q => !q.IsAnswered))
        {
            qp.Answer = result.IsCancelled ? "(Cancelled)" : "(No answer provided)";
        }

        var toolsForMessage = CurrentToolCalls.Count > 0
            ? CurrentToolCalls.Select(t => new IdeationToolCallViewModel
            {
                ToolCallId = t.ToolCallId,
                ToolName = t.ToolName,
                Arguments = t.Arguments,
                Result = t.Result,
                IsCompleted = t.IsCompleted,
                IsError = t.IsError,
                DurationMs = t.DurationMs
            }).ToList()
            : null;

        var partsForMessage = CurrentParts.Count > 0
            ? CurrentParts.ToList()
            : null;

        var cancelledNotice = IIdeationConversationCoordinator.CancelledAssistantNotice;
        var trimmedCancelledNotice = cancelledNotice.TrimStart();
        var effectiveContent = !string.IsNullOrEmpty(CurrentMessageBuffer)
            ? (result.IsCancelled && !CurrentMessageBuffer.EndsWith(trimmedCancelledNotice, StringComparison.Ordinal)
                ? CurrentMessageBuffer + cancelledNotice
                : CurrentMessageBuffer)
            : (result.IsCancelled && (toolsForMessage == null || toolsForMessage.Count == 0)
                ? (!string.IsNullOrWhiteSpace(result.AssistantMessage) ? result.AssistantMessage : string.Empty)
                : result.AssistantMessage);

        if (result.IsCancelled && partsForMessage != null && effectiveContent.EndsWith(trimmedCancelledNotice, StringComparison.Ordinal))
        {
            var lastTextIdx = partsForMessage.FindLastIndex(p => p is TextContentPart);
            if (lastTextIdx >= 0 && partsForMessage[lastTextIdx] is TextContentPart lastText)
            {
                if (!lastText.Text.EndsWith(trimmedCancelledNotice, StringComparison.Ordinal))
                {
                    partsForMessage[lastTextIdx] = new TextContentPart(lastText.Text + cancelledNotice);
                }
            }
            else
            {
                partsForMessage.Add(new TextContentPart(trimmedCancelledNotice));
            }
        }
        else if (!result.IsCancelled &&
                 partsForMessage != null &&
                 string.IsNullOrEmpty(CurrentMessageBuffer) &&
                 !string.IsNullOrEmpty(effectiveContent) &&
                 !partsForMessage.OfType<TextContentPart>().Any(p => string.Equals(p.Text, effectiveContent, StringComparison.Ordinal)))
        {
            partsForMessage.Add(new TextContentPart(effectiveContent));
        }

        if (!string.IsNullOrEmpty(effectiveContent) ||
            !string.IsNullOrEmpty(CurrentThinkingBuffer) ||
            (toolsForMessage != null && toolsForMessage.Count > 0) ||
            (partsForMessage != null && partsForMessage.Count > 0))
        {
            Messages.Add(new ChatMessage(
                "assistant",
                effectiveContent,
                string.IsNullOrEmpty(CurrentThinkingBuffer) ? null : CurrentThinkingBuffer,
                DateTime.UtcNow,
                toolsForMessage,
                partsForMessage));
        }

        if (wsId.HasValue && (result.ProposedTasks is { Count: > 0 } || Proposals.Count > 0))
        {
            WorkspaceTasks = await _proposalService.GetWorkspaceTasksAsync(wsId.Value);
        }

        if (!result.IsCancelled)
        {
            ReconcileProposedTasks(result.ProposedTasks, WorkspaceTasks, removeMissing: wsId.HasValue);
        }

        _inFlightOptimisticUserMessage = null;
        _inFlightOptimisticChips = null;
        PendingQuestions.Clear();
        IsGenerating = false;
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        CurrentToolCalls.Clear();
        CurrentParts.Clear();
        NotifyStateChanged();
        OnScrollRequested?.Invoke(true);
    }

    private async Task ReleaseBoundSessionOnWorkspaceSwitchAsync(
        int? oldWorkspaceId,
        int? oldConversationId,
        CancellationToken cancellationToken)
    {
        DetachStreamSubscription();
        if (oldWorkspaceId.HasValue && oldConversationId.HasValue)
        {
            try
            {
                await _coordinator.ReleaseConversationSessionAsync(
                    oldWorkspaceId.Value,
                    oldConversationId.Value,
                    cancelIfActive: false,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed releasing ideation session #{ConversationId} on workspace switch.", oldConversationId.Value);
            }
        }
    }

    private async Task ReleaseConversationAffinityIfChangedAsync(
        int workspaceId,
        int? oldConversationId,
        int newConversationId,
        CancellationToken cancellationToken)
    {
        if (oldConversationId.HasValue && oldConversationId.Value != newConversationId)
        {
            DetachStreamSubscription();
            try
            {
                await _coordinator.ReleaseConversationSessionAsync(
                    workspaceId,
                    oldConversationId.Value,
                    cancelIfActive: false,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed releasing session affinity when switching from ideation session #{ConversationId}.", oldConversationId.Value);
            }
        }
    }

    private void RestorePendingComposerChips(IReadOnlyList<ConversationAttachmentChipViewModel> chips)
    {
        foreach (var chip in chips)
        {
            AttachmentPresentationHelper.UpsertChip(PendingComposerChips, chip);
        }
    }

    private bool HasMatchingProposal(IdeationProposal proposal) =>
        Proposals.Any(existing =>
            (proposal.TaskId.HasValue && existing.CreatedTaskId.HasValue)
                ? existing.CreatedTaskId == proposal.TaskId
                : string.Equals(existing.Title, proposal.Title, StringComparison.OrdinalIgnoreCase));

    private void RemoveProposalCore(IdeationProposalViewModel proposal)
    {
        Proposals.Remove(proposal);
        if (proposal.CreatedTaskId.HasValue)
        {
            var removedId = proposal.CreatedTaskId.Value;
            foreach (var remaining in Proposals)
            {
                remaining.SelectedDependencyIds.RemoveAll(id => id == removedId);
            }
        }

        if (!string.IsNullOrWhiteSpace(proposal.Title))
        {
            foreach (var remaining in Proposals.Where(p => !p.IsAdded))
            {
                remaining.DependsOnTitles.RemoveAll(t => string.Equals(t, proposal.Title, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
