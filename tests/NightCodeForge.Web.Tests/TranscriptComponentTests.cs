using Bunit;
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
        _toastService = new ToastService();
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

        Assert.Contains("Agent Cognitive Reasoning Trace", cut.Markup);
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
            Optimizations: new LocalOptimizationFlags { LoopCircuitBreaker = true, ThinkingTraceCompaction = true });

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
            CritiquePoints: new[] { "The `LoopCircuitBreaker` flag was **disabled**." },
            SuggestedActions: new[]
            {
                new RemediationSuggestion(
                    Title: "Enable `LoopCircuitBreaker`",
                    Description: "Prevents runaway tool loops via `ToolLoopCircuitBreaker`.",
                    ActionType: "ToggleOptimizationFlag",
                    TargetKey: "LoopCircuitBreaker",
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
        Assert.Contains("<code>LoopCircuitBreaker</code>", cut.Markup);
        Assert.Contains("<strong>disabled</strong>", cut.Markup);
    }

    [Fact]
    public void TranscriptsPage_RendersList_AndAllowsTabSwitching()
    {
        var cut = Render<Transcripts>();

        // Sidebar renders with transcript
        Assert.Contains("Session Transcripts", cut.Markup);
        Assert.Contains("Task #101: stage-compile", cut.Markup);

        // Metadata card renders for selected transcript
        Assert.Contains("2026-10-15-task-101-stage-compile-failed.md", cut.Markup);

        // Tabs render
        Assert.Contains("Turn-by-Turn Dialogue", cut.Markup);
        Assert.Contains("Environment &amp; Tools", cut.Markup);
        Assert.Contains("Model Call Snapshots", cut.Markup);

        // Switch to Environment tab
        var envBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Environment & Tools"));
        envBtn.Click();

        Assert.Contains("Available Tools Catalog", cut.Markup);
        Assert.Contains("ReadFile", cut.Markup);

        // Switch to Model Call Snapshots tab
        var modelCallBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Model Call Snapshots"));
        modelCallBtn.Click();

        Assert.Contains("Model Calls (Requests As Sent To The LLM)", cut.Markup);
    }

    private class FakeTranscriptBrowserService : ITranscriptBrowserService
    {
        public bool RemediationApplied { get; private set; }
        public DiagnosticReport? CustomReport { get; set; }

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
            Optimizations: new LocalOptimizationFlags { LoopCircuitBreaker = false, GuidedGrammarDecoding = true });

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
                ModelCallsMarkdown: "#### Call 1\nMessages: 2 total");

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
                CritiquePoints: new[] { "LoopCircuitBreaker was disabled.", "Task lacked net10.0 target specification." },
                SuggestedActions: new[]
                {
                    new RemediationSuggestion(
                        Title: "Enable Loop Circuit Breaker",
                        Description: "Interrupts redundant loops.",
                        ActionType: "ToggleOptimizationFlag",
                        TargetKey: "LoopCircuitBreaker",
                        TargetValue: true)
                });

            return Task.FromResult(report);
        }

        public Task ApplyRemediationAsync(RemediationSuggestion suggestion, int? workspaceId = null, string? profileName = null, CancellationToken cancellationToken = default)
        {
            RemediationApplied = true;
            return Task.CompletedTask;
        }
    }
}
