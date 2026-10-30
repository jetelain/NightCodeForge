using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class GlobalSkillsManagementTabTests : BunitContext
{
    private class FakeGlobalSkillsManagerService : IGlobalSkillsManagerService
    {
        public List<GlobalSkillViewModel> Skills { get; set; } = new();
        public CreateSkillRequest? LastCreateRequest { get; private set; }
        public string? LastDeletedSkill { get; private set; }
        public string? LastUpdatedSkillName { get; private set; }
        public UpdateSkillRequest? LastUpdateRequest { get; private set; }

        public Task<IReadOnlyList<GlobalSkillViewModel>> GetGlobalSkillsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GlobalSkillViewModel>>(Skills);

        public Task CreateCustomSkillAsync(CreateSkillRequest request, CancellationToken ct = default)
        {
            LastCreateRequest = request;
            Skills.Add(new GlobalSkillViewModel(
                request.Name,
                request.Description,
                request.Stack,
                request.Instructions,
                $"/skills/{request.Stack}/{request.Name}/SKILL.md",
                IsBuiltIn: false));
            return Task.CompletedTask;
        }

        public Task UpdateCustomSkillAsync(string skillName, UpdateSkillRequest request, CancellationToken ct = default)
        {
            LastUpdatedSkillName = skillName;
            LastUpdateRequest = request;
            var existing = Skills.FirstOrDefault(s => s.Name == skillName);
            if (existing != null)
            {
                Skills.Remove(existing);
                Skills.Add(new GlobalSkillViewModel(
                    skillName,
                    request.Description,
                    request.Stack,
                    request.Instructions,
                    existing.FilePath,
                    IsBuiltIn: false));
            }
            return Task.CompletedTask;
        }

        public Task DeleteCustomSkillAsync(string skillName, CancellationToken ct = default)
        {
            LastDeletedSkill = skillName;
            Skills.RemoveAll(s => s.Name == skillName);
            return Task.CompletedTask;
        }
    }

    private readonly FakeGlobalSkillsManagerService _skillsManager;
    private readonly ToastService _toastService;

    public GlobalSkillsManagementTabTests()
    {
        _skillsManager = new FakeGlobalSkillsManagerService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<IGlobalSkillsManagerService>(_skillsManager);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void GlobalSkillsManagementTab_RendersGroupedSkills_AndDistinguishesBuiltInFromCustom()
    {
        _skillsManager.Skills = new List<GlobalSkillViewModel>
        {
            new("csharp-nav", "Roslyn navigation", "dotnet", "csnav instructions", "resource:csharp-nav", IsBuiltIn: true),
            new("mdfetch", "Markdown fetcher", "universal", "mdfetch instructions", "resource:mdfetch", IsBuiltIn: true),
            new("custom-dotnet-tool", "Custom dotnet tool", "dotnet", "custom instructions", "/path/to/custom", IsBuiltIn: false)
        };

        var cut = Render<GlobalSkillsManagementTab>();

        Assert.Contains(".NET / C# Skills", cut.Markup);
        Assert.Contains("Universal Skills (All Stacks)", cut.Markup);
        Assert.Contains("csharp-nav", cut.Markup);
        Assert.Contains("mdfetch", cut.Markup);
        Assert.Contains("custom-dotnet-tool", cut.Markup);

        // Built-in skills have the shield badge
        Assert.Contains("🛡️ Built-In Tooling", cut.Markup);
        Assert.Contains("✨ Custom Skill", cut.Markup);

        // Built-in skills do NOT have Edit or Delete buttons
        var buttons = cut.FindAll("button");
        Assert.Contains(buttons, b => b.TextContent.Contains("Edit"));
        Assert.Contains(buttons, b => b.TextContent.Contains("Delete"));
    }

    [Fact]
    public void GlobalSkillsManagementTab_ClickingAdd_OpensModal_AndSavingCreatesSkill()
    {
        _skillsManager.Skills = new List<GlobalSkillViewModel>
        {
            new("csharp-nav", "Roslyn navigation", "dotnet", "csnav instructions", "resource:csharp-nav", IsBuiltIn: true)
        };

        var cut = Render<GlobalSkillsManagementTab>();

        // Click Add button
        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Custom Global Skill"));
        addBtn.Click();

        // Modal should now be rendered
        Assert.Contains("Add Custom Global Skill", cut.Markup);

        // Fill out modal fields
        var nameInput = cut.Find("input.font-mono[placeholder*='efcore-assistant']");
        nameInput.Change("efcore-assistant");

        var descInput = cut.Find("input[placeholder*='Brief summary']");
        descInput.Change("EF Core migration advisor");

        var instructionsInput = cut.Find("textarea.font-mono");
        instructionsInput.Change("# EF Core Instructions\nRun migrations.");

        // Click Save Skill
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Skill"));
        saveBtn.Click();

        // Verify service received create request
        Assert.NotNull(_skillsManager.LastCreateRequest);
        Assert.Equal("efcore-assistant", _skillsManager.LastCreateRequest.Name);
        Assert.Equal("EF Core migration advisor", _skillsManager.LastCreateRequest.Description);
        Assert.Equal("# EF Core Instructions\nRun migrations.", _skillsManager.LastCreateRequest.Instructions);

        // Verify newly added skill appears in view
        Assert.Contains("efcore-assistant", cut.Markup);
    }

    [Fact]
    public void GlobalSkillsManagementTab_ClickingEdit_OpensPrepopulatedModal_AndUpdatesSkill()
    {
        _skillsManager.Skills = new List<GlobalSkillViewModel>
        {
            new("my-skill", "Initial description", "dotnet", "Initial body", "/path/my-skill", IsBuiltIn: false)
        };

        var cut = Render<GlobalSkillsManagementTab>();

        // Click Edit button
        var editBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Edit"));
        editBtn.Click();

        // Modal should open in edit mode
        Assert.Contains("Edit Global Skill", cut.Markup);

        // Update description and instructions
        var descInput = cut.Find("input[placeholder*='Brief summary']");
        descInput.Change("Updated description");

        var instructionsInput = cut.Find("textarea.font-mono");
        instructionsInput.Change("Updated instructions");

        // Save
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Skill"));
        saveBtn.Click();

        Assert.Equal("my-skill", _skillsManager.LastUpdatedSkillName);
        Assert.NotNull(_skillsManager.LastUpdateRequest);
        Assert.Equal("Updated description", _skillsManager.LastUpdateRequest.Description);
        Assert.Equal("Updated instructions", _skillsManager.LastUpdateRequest.Instructions);
    }

    [Fact]
    public void GlobalSkillsManagementTab_ClickingDelete_DeletesCustomSkill()
    {
        _skillsManager.Skills = new List<GlobalSkillViewModel>
        {
            new("to-delete", "Will be removed", "dotnet", "body", "/path/to-delete", IsBuiltIn: false)
        };

        var cut = Render<GlobalSkillsManagementTab>();

        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Delete"));
        deleteBtn.Click();

        Assert.Equal("to-delete", _skillsManager.LastDeletedSkill);
        Assert.DoesNotContain("to-delete", cut.Markup);
    }
}
