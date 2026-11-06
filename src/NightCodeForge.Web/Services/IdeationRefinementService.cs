using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Web.Services;

/// <summary>Conversation dedicated to refining one task, with the prompt to pre-seed when the session is new.</summary>
public sealed record TaskRefinementSession(Conversation Conversation, string SeedPrompt, bool IsNew);

/// <summary>
/// Creates or resumes the task-scoped ideation conversation behind the "Refine in Ideation" entry points.
/// </summary>
public sealed class IdeationRefinementService
{
    private readonly ITaskRepository _taskRepository;

    public IdeationRefinementService(ITaskRepository taskRepository)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
    }

    /// <exception cref="TaskNotFoundException">The task does not exist in the workspace.</exception>
    /// <exception cref="TaskCannotBeModifiedException">The task is no longer an unstarted backlog task.</exception>
    public async Task<TaskRefinementSession> InitializeTaskRefinementAsync(
        int workspaceId,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null || task.WorkspaceId != workspaceId)
        {
            throw new TaskNotFoundException(taskId);
        }

        if (!IdeationRefinementHelper.CanRefine(task))
        {
            throw new TaskCannotBeModifiedException(
                taskId,
                $"its status is {task.Status}{(task.HasExecutionRun ? " and it has already entered pipeline execution" : string.Empty)}; only unstarted Draft, Suggested or Open tasks can be refined.");
        }

        var prefix = IdeationRefinementHelper.BuildConversationTitlePrefix(taskId);
        var sessions = await _taskRepository.GetIdeationConversationsAsync(workspaceId, cancellationToken);
        var existing = sessions
            .Where(c => c.Title != null && c.Title.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(c => c.StartedAt)
            .FirstOrDefault();

        var seed = IdeationRefinementHelper.BuildSeedPrompt(task);
        if (existing != null)
        {
            var loaded = await _taskRepository.GetConversationWithTurnsAsync(existing.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Ideation conversation #{existing.Id} disappeared while resuming refinement of task #{taskId}.");
            return new TaskRefinementSession(loaded, seed, IsNew: false);
        }

        var created = await _taskRepository.StartIdeationConversationAsync(
            workspaceId,
            IdeationRefinementHelper.BuildConversationTitle(task),
            cancellationToken: cancellationToken);
        return new TaskRefinementSession(created, seed, IsNew: true);
    }
}

