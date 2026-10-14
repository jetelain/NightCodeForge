using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Immutable chat turn record within an ideation conversational session.
/// </summary>
public record ChatMessage(string Role, string Content, string? ThinkingTrace = null, DateTime? Timestamp = null);

/// <summary>
/// Mutable view model for a backlog task candidate discovered during ideation.
/// </summary>
public class IdeationProposalViewModel
{
    public Guid ClientId { get; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public List<int> SelectedDependencyIds { get; set; } = new();
    public bool IsAdded { get; set; }
    public int? CreatedTaskId { get; set; }
    public bool IsEditingDescription { get; set; }

    public static IdeationProposalViewModel FromProposal(IdeationProposal proposal, IReadOnlyList<ForgeTask>? existingTasks = null)
    {
        var vm = new IdeationProposalViewModel
        {
            Title = proposal.Title ?? string.Empty,
            Description = proposal.Description ?? string.Empty,
            Priority = proposal.Priority > 0 ? proposal.Priority : 1
        };

        if (proposal.DependsOnTitles != null && existingTasks != null)
        {
            foreach (var depTitle in proposal.DependsOnTitles)
            {
                var matched = existingTasks.FirstOrDefault(t =>
                    string.Equals(t.Title, depTitle, StringComparison.OrdinalIgnoreCase) ||
                    t.Title.Contains(depTitle, StringComparison.OrdinalIgnoreCase));
                if (matched != null && !vm.SelectedDependencyIds.Contains(matched.Id))
                {
                    vm.SelectedDependencyIds.Add(matched.Id);
                }
            }
        }

        return vm;
    }
}

/// <summary>
/// Scoped session state preserving chat turns, streaming buffers, and discovered task proposals for the Ideation engine.
/// </summary>
public class IdeationSessionState
{
    public List<ChatMessage> Messages { get; } = new();
    public List<IdeationProposalViewModel> Proposals { get; } = new();
    public bool IsGenerating { get; set; }
    public string CurrentThinkingBuffer { get; set; } = string.Empty;
    public string CurrentMessageBuffer { get; set; } = string.Empty;
    public string? CustomSystemPrompt { get; set; }
    public int? BoundWorkspaceId { get; set; }

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
        Proposals.Remove(proposal);
        NotifyStateChanged();
    }

    public void Reset()
    {
        Messages.Clear();
        Proposals.Clear();
        CurrentThinkingBuffer = string.Empty;
        CurrentMessageBuffer = string.Empty;
        IsGenerating = false;
        NotifyStateChanged();
    }
}
