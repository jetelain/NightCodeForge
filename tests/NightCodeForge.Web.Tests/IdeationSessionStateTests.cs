using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationSessionStateTests
{
    [Fact]
    public void InitialState_ShouldBeEmpty()
    {
        var session = new IdeationSessionState();

        Assert.Empty(session.Messages);
        Assert.Empty(session.Proposals);
        Assert.False(session.IsGenerating);
        Assert.Empty(session.CurrentThinkingBuffer);
        Assert.Empty(session.CurrentMessageBuffer);
        Assert.Null(session.BoundWorkspaceId);
        Assert.Null(session.CustomSystemPrompt);
    }

    [Fact]
    public void AddMessage_ShouldAppendAndNotify()
    {
        var session = new IdeationSessionState();
        var notified = false;
        session.OnChange += () => notified = true;

        var msg = new ChatMessage("user", "Hello architect", null, DateTime.UtcNow);
        session.AddMessage(msg);

        Assert.Single(session.Messages);
        Assert.Same(msg, session.Messages[0]);
        Assert.True(notified);
    }

    [Fact]
    public void AddProposal_And_RemoveProposal_ShouldModifyListAndNotify()
    {
        var session = new IdeationSessionState();
        var notifyCount = 0;
        session.OnChange += () => notifyCount++;

        var proposal = new IdeationProposalViewModel
        {
            Title = "Refactor logging",
            Description = "Use ILogger instead of Console",
            Priority = 2
        };

        session.AddProposal(proposal);
        Assert.Single(session.Proposals);
        Assert.Equal(1, notifyCount);

        session.RemoveProposal(proposal);
        Assert.Empty(session.Proposals);
        Assert.Equal(2, notifyCount);
    }

    [Fact]
    public void Reset_ShouldClearAllBuffersAndNotify()
    {
        var session = new IdeationSessionState();
        session.Messages.Add(new ChatMessage("user", "test"));
        session.Proposals.Add(new IdeationProposalViewModel { Title = "Task 1" });
        session.CurrentThinkingBuffer = "thinking...";
        session.CurrentMessageBuffer = "output...";
        session.IsGenerating = true;

        var notified = false;
        session.OnChange += () => notified = true;

        session.Reset();

        Assert.Empty(session.Messages);
        Assert.Empty(session.Proposals);
        Assert.Empty(session.CurrentThinkingBuffer);
        Assert.Empty(session.CurrentMessageBuffer);
        Assert.False(session.IsGenerating);
        Assert.True(notified);
    }

    [Fact]
    public void FromProposal_ShouldMapPropertiesAndResolveDependencies()
    {
        var existingTasks = new List<ForgeTask>
        {
            new() { Id = 10, Title = "Setup DB migrations" },
            new() { Id = 11, Title = "Configure Redis cache" }
        };

        var domainProposal = new IdeationProposal(
            Title: "Add distributed lock",
            Description: "Prevent race condition in worker",
            Priority: 4,
            DependsOnTitles: new[] { "Setup DB migrations", "Unknown Task" });

        var vm = IdeationProposalViewModel.FromProposal(domainProposal, existingTasks);

        Assert.Equal("Add distributed lock", vm.Title);
        Assert.Equal("Prevent race condition in worker", vm.Description);
        Assert.Equal(4, vm.Priority);
        Assert.Single(vm.SelectedDependencyIds);
        Assert.Contains(10, vm.SelectedDependencyIds);
        Assert.False(vm.IsAdded);
        Assert.Null(vm.CreatedTaskId);
    }
}
