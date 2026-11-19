using NightCodeForge.Core.Domain;
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

public record QuestionContentPart : ChatContentPart
{
    private string? _prompt;
    private string? _rationale;
    private IReadOnlyList<string>? _options;

    public Guid GroupId { get; init; } = Guid.NewGuid();
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
                    if (string.Equals(tc.ToolName, "ask", StringComparison.OrdinalIgnoreCase))
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
    public MergeMode MergeMode { get; set; } = MergeMode.ManualReviewRequired;
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
            MergeMode = ParseProposalMergeMode(proposal.MergeMode),
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
        MergeMode = task.MergeMode;
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

    /// <summary>
    /// Parses a numeric task reference such as "101" or "#101".
    /// </summary>
    private static bool TryParseTaskId(string reference, out int taskId)
    {
        var trimmed = reference.Trim().TrimStart('#');
        return int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out taskId);
    }

    private static MergeMode ParseProposalMergeMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MergeMode.ManualReviewRequired;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => MergeMode.Automatic,
            "externalpullrequest" or "external_pull_request" or "pull_request" or "pr" => MergeMode.ExternalPullRequest,
            "manualreviewrequired" or "manual_review" or "manual" => MergeMode.ManualReviewRequired,
            _ => MergeMode.ManualReviewRequired
        };
    }
}

/// <summary>
/// Scoped session state preserving chat turns, streaming buffers, and discovered task proposals for the Ideation engine.
/// </summary>
public class IdeationSessionState
{
    public List<ChatMessage> Messages { get; } = new();
    public List<IdeationProposalViewModel> Proposals { get; } = new();
    public List<IdeationToolCallViewModel> CurrentToolCalls { get; } = new();
    public List<ChatContentPart> CurrentParts { get; } = new();
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
    public List<IdeationQuestion> PendingQuestions { get; } = new();

    public void CancelPendingQuestions()
    {
        foreach (var pq in PendingQuestions)
        {
            pq.Cancel();
        }
        PendingQuestions.Clear();
    }

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

    /// <summary>
    /// Active target policy for ideation inference dispatch via OllamaRouter.
    /// Auto = Dynamic routing across Host GPU and LAN Server.
    /// Local = Host GPU (powers ideation concurrently while background dev-queue uses LAN server).
    /// Remote = Dedicated LAN GPU Server.
    /// Cloud = Cloud Overflow (instant complex chats without waiting for LAN GPU).
    /// </summary>
    public OllamaRoutingTarget SelectedRoutingTarget { get; set; } = OllamaRoutingTarget.Auto;

    /// <summary>
    /// Optional model override for ideation (e.g. lightweight model for Host GPU, or large model for Cloud Overflow).
    /// </summary>
    public string? SelectedModelOverride { get; set; }

    public event Action? OnChange;

    public void NotifyStateChanged() => OnChange?.Invoke();

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

    public void Reset()
    {
        Messages.Clear();
        Proposals.Clear();
        CurrentToolCalls.Clear();
        CancelPendingQuestions();
        CurrentParts.Clear();
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        LatestTranscriptPath = null;
        LatestFullTranscriptMarkdown = null;
        ActiveConversationId = null;
        ActiveConversationTitle = null;
        ActiveSessionCost = TaskCostSummary.Empty;
        SelectedRoutingTarget = OllamaRoutingTarget.Auto;
        SelectedModelOverride = null;
        IsGenerating = false;
        NotifyStateChanged();
    }
}
