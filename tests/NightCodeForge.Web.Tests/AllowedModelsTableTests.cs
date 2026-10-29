using Bunit;
using Microsoft.AspNetCore.Components;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Settings;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class AllowedModelsTableTests : BunitContext
{
    [Fact]
    public void AllowedModelsTable_WhenEmptyAndOptional_RendersOptionalEmptyNotice()
    {
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.IsMandatory, false)
            .Add(x => x.Models, new List<OpenAiAllowedModel>()));

        Assert.Contains("Allowed Models &amp; Context Sizing (Optional Overrides)", cut.Markup);
        Assert.Empty(cut.FindAll(".badge-amber"));
        Assert.Contains("No manual model overrides configured", cut.Markup);
        Assert.DoesNotContain("ModelNotAllowedException", cut.Markup);
    }

    [Fact]
    public void AllowedModelsTable_WhenEmptyAndMandatory_RendersMandatoryWarningAndBadge()
    {
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.IsMandatory, true)
            .Add(x => x.Models, new List<OpenAiAllowedModel>()));

        Assert.Contains("Allowed Models &amp; Context Sizing (Mandatory)", cut.Markup);
        Assert.NotNull(cut.Find(".badge-amber"));
        Assert.Contains("ModelNotAllowedException", cut.Markup);
        Assert.Contains("No allowed models configured", cut.Markup);
    }

    [Fact]
    public void AllowedModelsTable_DisplaysModels_WithFormattedTokensAndBadges()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel
            {
                ModelId = "meta-llama/llama-3.1-70b-instruct",
                DisplayName = "Llama 3.1 70B",
                ContextWindowTokens = 131072,
                MaxOutputTokens = 8192,
                SupportsReasoning = true,
                DefaultThinkingEffort = ThinkingEffort.High
            }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        Assert.Contains("meta-llama/llama-3.1-70b-instruct", cut.Markup);
        Assert.Contains("Llama 3.1 70B", cut.Markup);
        Assert.Contains("131,072 tokens", cut.Markup);
        Assert.Contains("8,192", cut.Markup);
        Assert.Contains("High", cut.Markup);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_RequiresModelId()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find("#btn-add-allowed-model").Click();

        // ModelId is empty by default; click save
        cut.Find("#btn-save-model").Click();

        Assert.Contains("Model identifier is required.", cut.Markup);
        Assert.Empty(models);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_RequiresPositiveContextTokens()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("gpt-4o");
        cut.Find("#input-context-tokens").Change(0);

        cut.Find("#btn-save-model").Click();

        Assert.Contains("Context window must be greater than zero.", cut.Markup);
        Assert.Empty(models);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_RejectsDuplicateModelId()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel { ModelId = "deepseek-chat", ContextWindowTokens = 65536 }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("deepseek-chat");
        cut.Find("#btn-save-model").Click();

        Assert.Contains("is already in the allowed list", cut.Markup);
        Assert.Single(models);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_WithPresetChips_AppliesSelectedTokens()
    {
        List<OpenAiAllowedModel> models = [];
        bool saveRequested = false;

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m))
            .Add(x => x.OnSaveRequested, EventCallback.Factory.Create(this, () => saveRequested = true)));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("qwen/qwen-2.5-coder-32b");

        // Verify chips: 32k, 64k, 128k, 200k
        var chips = cut.FindAll(".preset-chip");
        Assert.Equal(4, chips.Count);

        // Click 200k chip
        var chip200k = chips.First(c => c.TextContent.Trim() == "200k");
        chip200k.Click();

        var ctxInput = cut.Find("#input-context-tokens");
        Assert.Equal("200000", ctxInput.GetAttribute("value"));

        cut.Find("#btn-save-model").Click();

        Assert.Single(models);
        Assert.Equal(200000, models[0].ContextWindowTokens);
        Assert.True(saveRequested);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_WithReasoningAndPricing_AddsSuccessfully()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m)));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("deepseek-reasoner");
        cut.Find("#chk-supports-reasoning").Change(true);

        var effortSelect = cut.Find("#select-thinking-effort");
        Assert.NotNull(effortSelect);
        effortSelect.Change(ThinkingEffort.High.ToString());

        cut.Find("#btn-save-model").Click();

        Assert.Single(models);
        Assert.Equal("deepseek-reasoner", models[0].ModelId);
        Assert.True(models[0].SupportsReasoning);
        Assert.Equal(ThinkingEffort.High, models[0].DefaultThinkingEffort);
    }

    [Fact]
    public void AllowedModelsTable_EditModel_PrepopulatesAndSavesUpdates()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel
            {
                ModelId = "claude-3-5-sonnet",
                DisplayName = "Claude Sonnet",
                ContextWindowTokens = 200000,
                MaxOutputTokens = 8192
            }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m)));

        // Click Edit
        cut.Find(".btn-edit-model").Click();

        var idInput = cut.Find("#input-model-id");
        Assert.True(idInput.HasAttribute("disabled")); // Model ID cannot be changed on edit

        var nameInput = cut.Find("#input-display-name");
        Assert.Equal("Claude Sonnet", nameInput.GetAttribute("value"));
        nameInput.Change("Claude 3.5 Sonnet (200k)");

        cut.Find("#btn-save-model").Click();

        Assert.Equal("Claude 3.5 Sonnet (200k)", models[0].DisplayName);
        Assert.Empty(cut.FindAll(".allowed-model-editor"));
    }

    [Fact]
    public void AllowedModelsTable_CancelEdit_ClosesEditorWithoutModifyingList()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel { ModelId = "mistral-large", DisplayName = "Mistral" }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find(".btn-edit-model").Click();
        Assert.NotNull(cut.Find(".allowed-model-editor"));

        cut.Find("#btn-cancel-model").Click();
        Assert.Empty(cut.FindAll(".allowed-model-editor"));
        Assert.Equal("Mistral", models[0].DisplayName);
    }

    [Fact]
    public void AllowedModelsTable_DeleteModel_RemovesFromCollection()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel { ModelId = "model-a" },
            new OpenAiAllowedModel { ModelId = "model-b" }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m)));

        var deleteButtons = cut.FindAll(".btn-delete-model");
        deleteButtons[0].Click();

        Assert.Single(models);
        Assert.Equal("model-b", models[0].ModelId);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_TrimsModelIdAndRejectsWhitespaceDuplicates()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m)));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("  deepseek-chat  ");
        cut.Find("#input-display-name").Change("  DeepSeek Chat  ");
        cut.Find("#btn-save-model").Click();

        Assert.Single(models);
        Assert.Equal("deepseek-chat", models[0].ModelId);
        Assert.Equal("DeepSeek Chat", models[0].DisplayName);

        // Try adding with surrounding whitespace
        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change(" deepseek-chat ");
        cut.Find("#btn-save-model").Click();

        Assert.Contains("is already in the allowed list", cut.Markup);
        Assert.Single(models);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_ValidatesMaxOutputTokensLessThanContextWindow()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("small-ctx-model");
        cut.Find("#input-context-tokens").Change(4096);
        cut.Find("#input-max-output").Change(8192);

        cut.Find("#btn-save-model").Click();

        Assert.Contains("Max output tokens must be less than context window tokens.", cut.Markup);
        Assert.Empty(models);
    }

    [Fact]
    public void AllowedModelsTable_AddAllowedModel_RejectsNegativeMaxOutputOrPricing()
    {
        List<OpenAiAllowedModel> models = [];
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models));

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("test-neg");
        cut.Find("#input-max-output").Change(-1);

        cut.Find("#btn-save-model").Click();

        Assert.Contains("Max output tokens cannot be negative.", cut.Markup);
        Assert.Empty(models);
    }

    [Fact]
    public void AllowedModelsTable_DeleteModel_WhenEditingSameModel_CancelsEditMode()
    {
        List<OpenAiAllowedModel> models =
        [
            new OpenAiAllowedModel { ModelId = "model-to-delete", DisplayName = "To Delete" }
        ];

        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, models)
            .Add(x => x.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, (List<OpenAiAllowedModel> m) => models = m)));

        // Start editing the model
        cut.Find(".btn-edit-model").Click();
        Assert.NotEmpty(cut.FindAll(".allowed-model-editor"));

        // Delete the model while editor is open
        cut.Find(".btn-delete-model").Click();

        Assert.Empty(models);
        Assert.Empty(cut.FindAll(".allowed-model-editor"));
    }

    [Fact]
    public void AllowedModelsTable_WhenModelsParameterIsNull_DoesNotThrowNullReferenceException()
    {
        var cut = Render<AllowedModelsTable>(p => p
            .Add(x => x.Models, null!));

        Assert.Contains("Allowed Models &amp; Context Sizing", cut.Markup);
        Assert.Contains("No manual model overrides configured", cut.Markup);
    }
}
