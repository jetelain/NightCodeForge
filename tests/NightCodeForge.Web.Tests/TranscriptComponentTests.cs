using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Transcripts;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TranscriptComponentTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly FakeTranscriptBrowserService _browserService;

    public TranscriptComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _browserService = new FakeTranscriptBrowserService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<ITranscriptBrowserService>(_browserService);
    }

    [Fact]
    public void ThinkingTraceViewer_RendersBadgeAndTogglesContent()
    {
        var trace = "Step 1: Check imports.\nStep 2: Apply edits.";
        var cut = Render<ThinkingTraceViewer>(parameters => parameters
            .Add(p => p.Trace, trace)
            .Add(p => p.InitiallyExpanded, false));

        Assert.Contains("Reasoning Trace", cut.Markup);
        Assert.Contains("tokens", cut.Markup);
        Assert.Empty(cut.FindAll(".think-content"));

        // Click header to expand
        cut.Find(".think-header").Click();

        Assert.NotEmpty(cut.FindAll(".think-content"));
        Assert.Contains("Step 1: Check imports.", cut.Find(".think-content").TextContent);

        // Click again to collapse
        cut.Find(".think-header").Click();
        Assert.Empty(cut.FindAll(".think-content"));
    }

    [Fact]
    public void ToolCallCard_RendersNameArgsAndResult()
    {
        var cut = Render<ToolCallCard>(parameters => parameters
            .Add(p => p.ToolName, "EditFile")
            .Add(p => p.Arguments, "{\"path\": \"test.cs\"}")
            .Add(p => p.Result, "Success")
            .Add(p => p.Offset, "00:05")
            .Add(p => p.IsError, false));

        Assert.Contains("EditFile", cut.Markup);
        Assert.Contains("TOOL_CALL", cut.Markup);
        Assert.Contains("test.cs", cut.Markup);
        Assert.Contains("Success", cut.Markup);
        Assert.Contains("00:05", cut.Markup);
    }

    [Fact]
    public void TranscriptMetadataCard_RendersSummaryAndTriggersDiagnose()
    {
        var summary = new TranscriptSummary(
            FileName: "session-101.md",
            FilePath: "session-101.md",
            TaskId: 101,
            StageId: "stage-compile",
            ModelName: "qwen2.5-coder:32b",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 2500,
            CompletionTokens: 600,
            DurationMs: 4000,
            Status: "Failed",
            AgentProfile: "RefactorEngineer",
            KvCacheHitRate: 0.85,
            Optimizations: new LocalOptimizationFlags { GuidedGrammarDecoding = true, JitModelPreload = true });

        bool diagnoseClicked = false;
        var cut = Render<TranscriptMetadataCard>(parameters => parameters
            .Add(p => p.Summary, summary)
            .Add(p => p.OnDiagnoseRequested, () => diagnoseClicked = true));

        Assert.Contains("session-101.md", cut.Markup);
        Assert.Contains("Task:</strong> #101", cut.Markup);
        Assert.Contains("Stage:</strong> stage-compile", cut.Markup);
        Assert.Contains("Model:</strong> qwen2.5-coder:32b", cut.Markup);
        Assert.Contains("3,100", cut.Markup); // Total tokens
        Assert.Contains("85%", cut.Markup); // KV cache
        Assert.Contains("Failed", cut.Markup);

        // Click Diagnose with AI
        var button = cut.Find("button.forge-btn-primary");
        button.Click();

        Assert.True(diagnoseClicked);
    }

    [Fact]
    public async Task DiagnosticsModal_CloudOption_RunsAndDisplaysReportAndAppliesAction()
    {
        var summary = new TranscriptSummary(
            FileName: "session-101.md",
            FilePath: "session-101.md",
            TaskId: 101,
            StageId: "stage-compile",
            ModelName: "qwen2.5-coder:32b",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 2500,
            CompletionTokens: 600,
            DurationMs: 4000,
            Status: "Failed");

        var detail = new ParsedTranscriptDetail(summary, "markdown content", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        RemediationSuggestion? appliedAction = null;
        var cut = Render<DiagnosticsModal>(parameters => parameters
            .Add(p => p.Transcript, detail)
            .Add(p => p.OnRemediationApplied, (action) => appliedAction = action));

        // Initial choice screen
        Assert.Contains("Select Diagnostic Execution Engine", cut.Markup);
        Assert.Contains("Cloud Diagnostics", cut.Markup);
        Assert.Contains("Local Diagnostics", cut.Markup);

        // Click Cloud Diagnostics card
        var cloudCard = cut.FindAll(".diagnostic-option-card").First();
        await cut.InvokeAsync(() => cloudCard.Click());

        // Report should be displayed
        Assert.Contains("Root Cause Identified:", cut.Markup);
        Assert.Contains("Prompt &amp; Skill Critique:", cut.Markup);
        Assert.Contains("One-Click Remediation Actions:", cut.Markup);

        // Click Apply Action
        var applyBtn = cut.Find(".remediation-card button");
        Assert.Contains("Apply Action", applyBtn.TextContent);
        await cut.InvokeAsync(() => applyBtn.Click());

        Assert.NotNull(appliedAction);
        Assert.Contains("Applied", cut.Find(".remediation-card button").TextContent);
        Assert.True(_browserService.RemediationApplied);
    }

    [Fact]
    public async Task DiagnosticsModal_WhenDiagnosticsHasMarkdown_RendersFormattedHtmlElements()
    {
        var summary = new TranscriptSummary(
            FileName: "session-102.md",
            FilePath: "session-102.md",
            TaskId: 102,
            StageId: "stage-compile",
            ModelName: "qwen2.5-coder:32b",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 2500,
            CompletionTokens: 600,
            DurationMs: 4000,
            Status: "Failed");

        var detail = new ParsedTranscriptDetail(summary, "markdown content", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        _browserService.CustomReport = new DiagnosticReport(
            RootCause: "**Agent failed** due to `CS0117` in *UserProfile.cs*.\n\n```csharp\npublic record User();\n```",
            FailureCategory: "Compilation Failure",
            CritiquePoints: new[] { "The `DeterministicSymbolMap` flag was **disabled**." },
            SuggestedActions: new[]
            {
                new RemediationSuggestion(
                    Title: "Enable `DeterministicSymbolMap`",
                    Description: "Stabilizes symbol resolution across multi-turn runs.",
                    ActionType: "ToggleOptimizationFlag",
                    TargetKey: "DeterministicSymbolMap",
                    TargetValue: true)
            });

        var cut = Render<DiagnosticsModal>(parameters => parameters
            .Add(p => p.Transcript, detail));

        // Click Cloud Diagnostics
        var cloudCard = cut.FindAll(".diagnostic-option-card").First();
        await cut.InvokeAsync(() => cloudCard.Click());

        // Verify HTML tags are rendered, not raw markdown asterisks and backticks
        Assert.Contains("<strong>Agent failed</strong>", cut.Markup);
        Assert.Contains("<code>CS0117</code>", cut.Markup);
        Assert.Contains("<em>UserProfile.cs</em>", cut.Markup);
        Assert.Contains("<pre><code", cut.Markup);
        Assert.Contains("public record User();", cut.Markup);
        Assert.Contains("<code>DeterministicSymbolMap</code>", cut.Markup);
        Assert.Contains("<strong>disabled</strong>", cut.Markup);
    }

    [Fact]
    public void TranscriptsPage_RendersList_AndAllowsTabSwitching()
    {
        var cut = Render<Transcripts>();

        // Sidebar renders with transcript
        Assert.Contains("Session Transcripts", cut.Markup);
        Assert.Contains("Task #101: stage-compile", cut.Markup);

        // Mode switcher and Friction filter chip render
        Assert.Contains("Fleet Analysis", cut.Markup);
        Assert.Contains("⚠️ Friction", cut.Markup);

        // Metadata card renders for selected transcript
        Assert.Contains("2026-10-15-task-101-stage-compile-failed.md", cut.Markup);

        // Tabs render
        Assert.Contains("Turn-by-Turn Dialogue", cut.Markup);
        Assert.Contains("Analysis &amp; Friction", cut.Markup);
        Assert.Contains("Environment &amp; Tools", cut.Markup);
        Assert.Contains("Model Call Snapshots", cut.Markup);

        // Switch to Analysis & Friction tab
        var analysisBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Analysis & Friction"));
        analysisBtn.Click();

        Assert.Contains("Execution Friction Detected", cut.Markup);
        Assert.Contains("Edit Mismatches", cut.Markup);

        // Switch to Environment tab
        var envBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Environment & Tools"));
        envBtn.Click();

        Assert.Contains("Available Tools Catalog", cut.Markup);
        Assert.Contains("ReadFile", cut.Markup);

        // Switch to Model Call Snapshots tab
        var modelCallBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Model Call Snapshots"));
        modelCallBtn.Click();

        Assert.Contains("Model Calls (Requests As Sent To The LLM)", cut.Markup);

        // Switch to Fleet mode
        var fleetModeBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Fleet Analysis (1)"));
        fleetModeBtn.Click();

        Assert.Contains("Fleet Execution &amp; Friction Analysis", cut.Markup);
    }

    [Fact]
    public void TranscriptAnalysisViewer_RendersFrictionScorecardAndRecommendations()
    {
        var analysis = new NightCodeForge.Core.Services.TranscriptAnalysis(
            FilePath: "path/to/test.md",
            FileName: "test.md",
            Frontmatter: new NightCodeForge.Core.Services.TranscriptFrontmatter { TaskId = 1, StageId = "test", DurationMs = 2000 },
            TotalTurns: 10,
            UserTurns: 2,
            AssistantTurns: 4,
            ToolTurns: 1,
            ThinkingTurns: 2,
            ToolUsage: new Dictionary<string, int> { ["ReadFile"] = 2, ["EditFile"] = 2 },
            BashErrors: 1,
            EditFailures: 2,
            ToolErrors: 3,
            CompilerErrors: 1,
            TestFailures: 1,
            CompactionEvents: 1,
            HistoryRewrites: 2,
            TopReadFiles: new List<KeyValuePair<string, int>> { new("App.cs", 2) },
            TopEditedFiles: new List<KeyValuePair<string, int>> { new("App.cs", 2) },
            HighChurnFiles: new List<KeyValuePair<string, int>> { new("App.cs", 2) },
            FrictionPoints: new[] { "Edit mismatch detected on App.cs" },
            Recommendations: new[] { "Use search-and-replace instead of full file rewrite" });

        var cut = Render<TranscriptAnalysisViewer>(p => p.Add(x => x.Analysis, analysis));

        Assert.Contains("Execution Friction Detected", cut.Markup);
        Assert.Contains("Edit mismatch detected on App.cs", cut.Markup);
        Assert.Contains("Use search-and-replace", cut.Markup);
        Assert.Contains("Edit Mismatches", cut.Markup);
        Assert.Contains("High-Churn Files Detected", cut.Markup);
        Assert.Contains("Tool Usage Frequency (4 calls)", cut.Markup);
        Assert.Contains("50%", cut.Markup);
        Assert.Contains("width: 50%", cut.Markup);
        Assert.DoesNotContain("200%", cut.Markup);
    }

    [Fact]
    public void TranscriptAnalysisViewer_RendersCleanBannerWhenNoFriction()
    {
        var cleanAnalysis = new NightCodeForge.Core.Services.TranscriptAnalysis(
            FilePath: "clean.md",
            FileName: "clean.md",
            Frontmatter: new NightCodeForge.Core.Services.TranscriptFrontmatter { TaskId = 2, StageId = "clean", DurationMs = 1500 },
            TotalTurns: 4,
            UserTurns: 1,
            AssistantTurns: 2,
            ToolTurns: 1,
            ThinkingTurns: 1,
            ToolUsage: new Dictionary<string, int> { ["ReadFile"] = 1 },
            BashErrors: 0,
            EditFailures: 0,
            ToolErrors: 0,
            CompilerErrors: 0,
            TestFailures: 0,
            CompactionEvents: 0,
            HistoryRewrites: 0,
            TopReadFiles: new List<KeyValuePair<string, int>>(),
            TopEditedFiles: new List<KeyValuePair<string, int>>(),
            HighChurnFiles: new List<KeyValuePair<string, int>>(),
            FrictionPoints: Array.Empty<string>(),
            Recommendations: Array.Empty<string>());

        var cut = Render<TranscriptAnalysisViewer>(p => p.Add(x => x.Analysis, cleanAnalysis));

        Assert.Contains("Clean Execution Trace", cut.Markup);
        Assert.DoesNotContain("Execution Friction Detected", cut.Markup);
    }

    [Fact]
    public void FleetAnalysisDashboard_RendersKpisAndRespondsToFilterAndSelection()
    {
        var fleet = new List<NightCodeForge.Core.Services.TranscriptAnalysis>
        {
            new(
                FilePath: "t1.md",
                FileName: "2026-10-15-task-1-stage-1-failed.md",
                Frontmatter: new NightCodeForge.Core.Services.TranscriptFrontmatter
                {
                    TaskId = 1,
                    StageId = "stage-1",
                    Model = "model-alpha",
                    DurationMs = 5000,
                    Tokens = new NightCodeForge.Core.Services.TranscriptTokensFrontmatter { Total = 2000 },
                    Status = "Failed"
                },
                TotalTurns: 6,
                UserTurns: 1,
                AssistantTurns: 3,
                ToolTurns: 2,
                ThinkingTurns: 1,
                ToolUsage: new Dictionary<string, int> { ["EditFile"] = 2 },
                BashErrors: 0,
                EditFailures: 1,
                ToolErrors: 1,
                CompilerErrors: 1,
                TestFailures: 0,
                CompactionEvents: 0,
                HistoryRewrites: 0,
                TopReadFiles: new List<KeyValuePair<string, int>>(),
                TopEditedFiles: new List<KeyValuePair<string, int>>(),
                HighChurnFiles: new List<KeyValuePair<string, int>>(),
                FrictionPoints: new[] { "Edit mismatch" },
                Recommendations: new[] { "Check indentation" })
        };

        string selectedFileName = string.Empty;
        var cut = Render<FleetAnalysisDashboard>(p => p
            .Add(x => x.Fleet, fleet)
            .Add(x => x.OnSelectTranscript, EventCallback.Factory.Create<string>(this, s => selectedFileName = s)));

        Assert.Contains("Fleet Execution &amp; Friction Analysis", cut.Markup);
        Assert.Contains("Token Volume", cut.Markup);
        Assert.Contains("2,000", cut.Markup);
        Assert.Contains("Key Friction Patterns Across Fleet", cut.Markup);
        Assert.Contains("Fleet Sessions Comparison", cut.Markup);
        Assert.Contains("⚠️ 3", cut.Markup); // TotalErrors includes ToolErrors (1 edit + 1 tool + 1 compiler = 3)

        // Click Inspect button
        var inspectBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Inspect"));
        inspectBtn.Click();

        Assert.Equal("2026-10-15-task-1-stage-1-failed.md", selectedFileName);
    }

    [Fact]
    public void FleetAnalysisDashboard_WhenTotalTokensZero_FallsBackToPromptAndCompletion()
    {
        var fleet = new List<NightCodeForge.Core.Services.TranscriptAnalysis>
        {
            new(
                FilePath: "t2.md",
                FileName: "session-2.md",
                Frontmatter: new NightCodeForge.Core.Services.TranscriptFrontmatter
                {
                    TaskId = 2,
                    StageId = "stage-2",
                    Model = "model-beta",
                    DurationMs = 3000,
                    Tokens = new NightCodeForge.Core.Services.TranscriptTokensFrontmatter { Prompt = 300, Completion = 150, Total = 0 },
                    Status = "Completed"
                },
                TotalTurns: 3,
                UserTurns: 1,
                AssistantTurns: 1,
                ToolTurns: 1,
                ThinkingTurns: 0,
                ToolUsage: new Dictionary<string, int> { ["ReadFile"] = 1 },
                BashErrors: 0,
                EditFailures: 0,
                ToolErrors: 0,
                CompilerErrors: 0,
                TestFailures: 0,
                CompactionEvents: 0,
                HistoryRewrites: 0,
                TopReadFiles: new List<KeyValuePair<string, int>>(),
                TopEditedFiles: new List<KeyValuePair<string, int>>(),
                HighChurnFiles: new List<KeyValuePair<string, int>>(),
                FrictionPoints: Array.Empty<string>(),
                Recommendations: Array.Empty<string>())
        };

        var cut = Render<FleetAnalysisDashboard>(p => p.Add(x => x.Fleet, fleet));

        Assert.Contains("450", cut.Markup); // 300 + 150
    }

    [Fact]
    public void TranscriptsPage_WhenActiveTabIsAnalysisAndAnalysisNull_RendersFallbackNotice()
    {
        _browserService.ReturnNullAnalysis = true;
        var cut = Render<Transcripts>();

        // Switch to Fleet mode and click Inspect to navigate into session analysis tab
        var fleetModeBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Fleet Analysis (1)"));
        fleetModeBtn.Click();

        var inspectBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Inspect"));
        inspectBtn.Click();

        Assert.Contains("Analysis data is not available for this transcript.", cut.Markup);
    }

    private class FakeTranscriptBrowserService : ITranscriptBrowserService
    {
        public bool RemediationApplied { get; private set; }
        public DiagnosticReport? CustomReport { get; set; }
        public bool ReturnNullAnalysis { get; set; }

        private readonly TranscriptSummary _sampleSummary = new(
            FileName: "2026-10-15-task-101-stage-compile-failed.md",
            FilePath: "docs/transcripts/2026-10-15-task-101-stage-compile-failed.md",
            TaskId: 101,
            StageId: "stage-compile",
            ModelName: "qwen2.5-coder:32b",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 3200,
            CompletionTokens: 840,
            DurationMs: 4500,
            Status: "Failed",
            AgentProfile: "RefactorEngineer",
            KvCacheHitRate: 0.88,
            Optimizations: new LocalOptimizationFlags { JitModelPreload = false, GuidedGrammarDecoding = true },
            HasFriction: true,
            TotalErrors: 2);

        private readonly NightCodeForge.Core.Services.TranscriptAnalysis _sampleAnalysis = new(
            FilePath: "docs/transcripts/2026-10-15-task-101-stage-compile-failed.md",
            FileName: "2026-10-15-task-101-stage-compile-failed.md",
            Frontmatter: new NightCodeForge.Core.Services.TranscriptFrontmatter
            {
                TaskId = 101,
                StageId = "stage-compile",
                AgentProfile = "RefactorEngineer",
                Model = "qwen2.5-coder:32b",
                DurationMs = 4500,
                Tokens = new NightCodeForge.Core.Services.TranscriptTokensFrontmatter { Prompt = 3200, Completion = 840, Total = 4040 },
                Status = "Failed"
            },
            TotalTurns: 4,
            UserTurns: 1,
            AssistantTurns: 2,
            ToolTurns: 1,
            ThinkingTurns: 1,
            ToolUsage: new Dictionary<string, int> { ["ReadFile"] = 1, ["EditFile"] = 2 },
            BashErrors: 0,
            EditFailures: 1,
            ToolErrors: 0,
            CompilerErrors: 1,
            TestFailures: 0,
            CompactionEvents: 0,
            HistoryRewrites: 0,
            TopReadFiles: new List<KeyValuePair<string, int>> { new("UserProfile.cs", 2) },
            TopEditedFiles: new List<KeyValuePair<string, int>> { new("UserProfile.cs", 2) },
            HighChurnFiles: new List<KeyValuePair<string, int>> { new("UserProfile.cs", 2) },
            FrictionPoints: new[] { "Edit mismatch detected on UserProfile.cs", "Compiler error CS0117" },
            Recommendations: new[] { "Enable Deterministic Symbol Map" });

        public Task<IReadOnlyList<TranscriptSummary>> GetTranscriptsAsync(string? workspacePath = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TranscriptSummary>>(new[] { _sampleSummary });
        }

        public Task<ParsedTranscriptDetail?> GetTranscriptDetailAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var turns = new List<ParsedTranscriptTurn>
            {
                new("00:00", "USER", "Refactor UserProfile.cs"),
                new("00:02", "THINKING", "Analyzing UserProfile.cs syntax", ThinkingTrace: "Analyzing syntax."),
                new("00:05", "TOOL", "", ToolName: "ReadFile", ToolArguments: "{\"path\": \"UserProfile.cs\"}", ToolResult: "class UserProfile {}", IsTool: true),
                new("00:08", "ASSISTANT", "Now refactoring UserProfile to record type.")
            };

            var detail = new ParsedTranscriptDetail(
                Metadata: _sampleSummary,
                RawMarkdown: "# Demo Markdown Content",
                SystemPrompt: "You are an expert engineer.",
                AvailableTools: new[] { "ReadFile", "EditFile" },
                ActiveSkills: new[] { "csharp-refactoring" },
                Turns: turns,
                ModelCallsMarkdown: "#### Call 1\nMessages: 2 total",
                Analysis: ReturnNullAnalysis ? null : _sampleAnalysis);

            return Task.FromResult<ParsedTranscriptDetail?>(detail);
        }

        public Task<ParsedTranscriptDetail?> GetTranscriptDetailByNameAsync(string fileName, string? workspacePath = null, CancellationToken cancellationToken = default)
        {
            return GetTranscriptDetailAsync(fileName, cancellationToken);
        }

        public Task<DiagnosticReport> RunDiagnosticsAsync(ParsedTranscriptDetail detail, string mode, CancellationToken cancellationToken = default)
        {
            if (CustomReport != null)
            {
                return Task.FromResult(CustomReport);
            }

            var report = new DiagnosticReport(
                RootCause: "Agent encountered repeating compiler error.",
                FailureCategory: "Runaway Tool Loop / Incomplete Context",
                CritiquePoints: new[] { "DeterministicSymbolMap was disabled.", "Task lacked net10.0 target specification." },
                SuggestedActions: new[]
                {
                    new RemediationSuggestion(
                        Title: "Enable Deterministic Symbol Map",
                        Description: "Stabilizes symbol resolution.",
                        ActionType: "ToggleOptimizationFlag",
                        TargetKey: "DeterministicSymbolMap",
                        TargetValue: true)
                });

            return Task.FromResult(report);
        }

        public Task ApplyRemediationAsync(RemediationSuggestion suggestion, int? workspaceId = null, string? profileName = null, CancellationToken cancellationToken = default)
        {
            RemediationApplied = true;
            return Task.CompletedTask;
        }

        public Task<NightCodeForge.Core.Services.TranscriptAnalysis?> GetTranscriptAnalysisAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NightCodeForge.Core.Services.TranscriptAnalysis?>(_sampleAnalysis);
        }

        public Task<IReadOnlyList<NightCodeForge.Core.Services.TranscriptAnalysis>> GetFleetAnalysisAsync(string? workspacePath = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NightCodeForge.Core.Services.TranscriptAnalysis>>(new[] { _sampleAnalysis });
        }
    }
}
