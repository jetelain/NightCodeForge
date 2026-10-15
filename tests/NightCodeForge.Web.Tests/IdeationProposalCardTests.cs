using Bunit;
using Microsoft.AspNetCore.Components;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Ideation;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationProposalCardTests : BunitContext
{
    [Fact]
    public void RendersTitleDescriptionAndPriority()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Refactor Telemetry Hub",
            Description = "Improve SignalR broadcast throughput",
            Priority = 3
        };

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal));

        var titleInput = cut.Find("input.proposal-title-input");
        Assert.Equal("Refactor Telemetry Hub", titleInput.GetAttribute("value"));

        var prioritySelect = cut.Find("select.proposal-priority-select");
        Assert.Equal("3", prioritySelect.GetAttribute("value"));

        // Defaults to preview mode
        var previewContainer = cut.Find(".proposal-desc-preview");
        Assert.Contains("Improve SignalR broadcast throughput", previewContainer.InnerHtml);

        // Click Edit Scope tab to view textarea
        var editBtn = cut.FindAll("button.proposal-tab-btn").First(b => b.TextContent.Contains("Edit Scope"));
        editBtn.Click();

        var descTextarea = cut.Find("textarea.proposal-desc-textarea");
        Assert.Equal("Improve SignalR broadcast throughput", descTextarea.GetAttribute("value"));
    }

    [Fact]
    public void DefaultPreviewMode_DisplaysMarkdownRenderer_AndCanToggleToEdit()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Doc update",
            Description = "### Acceptance Criteria\n- Step 1\n- Step 2"
        };

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal));

        // In preview mode by default
        var previewContainer = cut.Find(".proposal-desc-preview");
        Assert.Contains("Acceptance Criteria", previewContainer.InnerHtml);

        // Switch to Edit Scope
        var editBtn = cut.FindAll("button.proposal-tab-btn").First(b => b.TextContent.Contains("Edit Scope"));
        editBtn.Click();

        var descTextarea = cut.Find("textarea.proposal-desc-textarea");
        Assert.Contains("Acceptance Criteria", descTextarea.GetAttribute("value"));

        // Switch back to Preview
        var previewBtn = cut.FindAll("button.proposal-tab-btn").First(b => b.TextContent.Contains("Preview"));
        previewBtn.Click();

        Assert.NotNull(cut.Find(".proposal-desc-preview"));
    }

    [Fact]
    public void ToggleDependencies_AllowsSelectingWorkspaceTasks()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "New Task"
        };

        var tasks = new List<ForgeTask>
        {
            new() { Id = 101, Title = "Prerequisite Task 1", Status = TaskOverallStatus.Open },
            new() { Id = 102, Title = "Prerequisite Task 2", Status = TaskOverallStatus.Suggested }
        };

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal)
            .Add(p => p.WorkspaceTasks, tasks));

        // Open accordion
        cut.Find("button.dependency-toggle-btn").Click();

        var checkboxes = cut.FindAll(".dependency-item-checkbox input[type='checkbox']");
        Assert.Equal(2, checkboxes.Count);

        // Check task 101
        checkboxes[0].Change(true);
        Assert.Contains(101, proposal.SelectedDependencyIds);

        // Uncheck task 101 - re-query after re-render
        cut.FindAll(".dependency-item-checkbox input[type='checkbox']")[0].Change(false);
        Assert.DoesNotContain(101, proposal.SelectedDependencyIds);
    }

    [Fact]
    public void ToggleDependencies_DisplaysSequentialDependenciesHint_WhenPresent()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Dependent Task",
            DependsOnTitles = new List<string> { "Predecessor Task A", "Predecessor Task B" }
        };

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal));

        // Open accordion
        cut.Find("button.dependency-toggle-btn").Click();

        var hintPanel = cut.Find(".dependency-implicit-hint");
        Assert.NotNull(hintPanel);
        Assert.Contains("Predecessor Task A", hintPanel.InnerHtml);
        Assert.Contains("Predecessor Task B", hintPanel.InnerHtml);
    }

    [Fact]
    public void ClickAddToBacklog_TriggersCallback()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Implement Auth Service"
        };

        IdeationProposalViewModel? addedProposal = null;

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal)
            .Add(p => p.OnAddToBacklog, EventCallback.Factory.Create<IdeationProposalViewModel>(this, p => addedProposal = p)));

        cut.Find("button.add-backlog-btn").Click();

        Assert.NotNull(addedProposal);
        Assert.Same(proposal, addedProposal);
    }

    [Fact]
    public void WhenAdded_DisplaysSuccessBannerAndDisablesInputs()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Completed Formulation",
            IsAdded = true,
            CreatedTaskId = 42
        };

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal));

        var banner = cut.Find(".proposal-success-box");
        Assert.Contains("Added as ForgeTask #42 (Suggested)", banner.TextContent);

        var titleInput = cut.Find("input.proposal-title-input");
        Assert.True(titleInput.HasAttribute("disabled"));

        Assert.Empty(cut.FindAll("button.add-backlog-btn"));
    }

    [Fact]
    public void ClickDismiss_TriggersCallback()
    {
        var proposal = new IdeationProposalViewModel
        {
            Title = "Task to dismiss"
        };

        IdeationProposalViewModel? dismissedProposal = null;

        var cut = Render<IdeationProposalCard>(parameters => parameters
            .Add(p => p.Proposal, proposal)
            .Add(p => p.OnDismiss, EventCallback.Factory.Create<IdeationProposalViewModel>(this, p => dismissedProposal = p)));

        cut.Find("button.dismiss-btn").Click();

        Assert.NotNull(dismissedProposal);
        Assert.Same(proposal, dismissedProposal);
    }
}
