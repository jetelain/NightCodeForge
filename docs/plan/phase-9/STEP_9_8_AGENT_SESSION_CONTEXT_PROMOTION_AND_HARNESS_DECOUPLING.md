# 🧩 Step 9.8: AgentSessionContext Property Promotion & Harness Decoupling

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Promote cross-cutting workflow state keys from untyped, leaky [`PipelineDataBagKeys`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs) into strongly-typed, plain properties on [`AgentSessionContext`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentSessionContext.cs). Fix the critical bug where code review agents were asked to review changes without ever receiving the [`ReviewDiff`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L15). Eliminate direct mutations of [`PipelineDataBag`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBag.cs) from inside [`IAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/IAgentHarness.cs) implementations ([`CrucibleAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), [`PiRpcAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs), [`AcpAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs)), decoupling agent harnesses from orchestrator pipeline internals.

---

## 2. Analysis of Current State & Hidden Architectural Flaws

### 2.1 Bug 1: Review Agent Never Receives the Git Diff (`ReviewDiff`)
* **Current Behavior:** In [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L674-L677), right after the implementation stage completes:
  ```csharp
  var diff = await _gitWorkspaceManager.GetDiffAsync(workspace, task.Id, cancellationToken);
  pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiff, diff);
  ```
* **The Bug:** Neither [`AgentSessionContext`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentSessionContext.cs) nor [`DefaultBootstrapPromptBuilder`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs) nor any harness reads `PipelineDataBagKeys.ReviewDiff`.
* **Impact:** The code review agent is asked to review changes without ever being provided the Git diff of what was changed!

### 2.2 Flaw 2: Leaky Abstraction & NullReferenceException Risks in Agent Harnesses
* In [`CrucibleAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L248):
  ```csharp
  var taskDocPath = context.PipelineContext?.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
  ```
  `Crucible` reaches deep into the optional `PipelineContext` and its untyped `DataBag` to resolve the task document path.
* In [`CrucibleAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L733-L745), [`PiRpcAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs#L401-L404), and [`AcpAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs#L563-L566):
  All three harnesses manually parse `<request_changes>` and directly mutate `context.PipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewerFeedback, feedback)` and `context.PipelineContext.DataBag.Set(PipelineDataBagKeys.PendingProposals, ...)`.
* If `context.PipelineContext` is null (e.g. running outside of `TaskOrchestrator`, in unit tests, or CLI evaluation runners), this causes instant `NullReferenceException` or silent omission.

---

## 3. Technical Scope & Specifications

### 3.1 Plain Property Promotion on `AgentSessionContext`
Expand [`AgentSessionContext`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentSessionContext.cs) with strongly-typed properties:
```csharp
public class AgentSessionContext
{
    public required ForgeTask ForgeTask { get; init; }
    public Workspace? Workspace { get; init; }
    public required AgentProfile Profile { get; init; }
    public PipelineContext? PipelineContext { get; init; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }
    public string StageId { get; init; } = string.Empty;
    public string StageDirective { get; init; } = string.Empty;
    public AgentMode RequiredMode { get; init; } = AgentMode.Full;
    public string BootstrapPrompt { get; init; } = string.Empty;
    public string? SerializedStateJson { get; init; }
    public string WorkspacePath { get; init; } = string.Empty;
    public string? PendingHumanAnswer { get; init; }
    public IReadOnlyList<string>? DiscoveredSymbols { get; init; }

    // --- Promoted Typed Properties ---
    public string? TaskMarkdownPath { get; init; }
    public string? ReviewDiff { get; init; }
    public string? ApprovedPlan { get; init; }
    public string? ReviewerFeedback { get; init; }
    public IReadOnlyList<string>? PriorDiagnostics { get; init; }
}
```

### 3.2 Context Population in `TaskOrchestrator`
In [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L364-L378), populate these properties when constructing `AgentSessionContext`:
```csharp
var agentContext = new AgentSessionContext
{
    ForgeTask = task,
    Workspace = workspace,
    Profile = profile,
    StageId = stage.StageId,
    StageDirective = stageDirective,
    RequiredMode = stage.RequiredMode,
    WorkspacePath = taskWorkingPath,
    SerializedStateJson = resumeMemory,
    PendingHumanAnswer = lastHumanAnswer,
    TaskMarkdownPath = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath),
    ReviewDiff = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiff),
    ApprovedPlan = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan) 
        ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview)),
    ReviewerFeedback = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback),
    PriorDiagnostics = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors),
    BootstrapPrompt = stage.BuildBootstrapPrompt(task, pipelineContext, stageDirective),
    OptimizationFlags = profile.OptimizationFlags ?? new LocalOptimizationFlags(),
    PipelineContext = pipelineContext
};
```

### 3.3 Wire `ReviewDiff` to Code Review Prompts
1. In [`DefaultBootstrapPromptBuilder.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs):
   * Add `review_diff` to the Scriban context model:
     ```csharp
     var reviewDiff = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiff);
     // ...
     context = new
     {
         task_markdown_path = ...,
         approved_plan = ...,
         reviewer_feedback = ...,
         review_diff = string.IsNullOrWhiteSpace(reviewDiff) ? null : reviewDiff,
         // ...
     }
     ```
   * In legacy format building: append `[Git Diff to Review]:\n{reviewDiff}\n` when `reviewDiff` is present.
2. In [`CrucibleAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs):
   * In `BuildTaskScope`: incorporate `context.ReviewDiff` when present so review agents immediately see the full patch.

### 3.4 Decouple Agent Harnesses via `AgentRunResult`
1. Expand [`AgentRunResult`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/AgentRunResult.cs):
   ```csharp
   public string? ReviewerFeedback { get; init; }
   public IReadOnlyList<TaskProposal>? Proposals { get; init; }
   ```
2. Update [`CrucibleAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), [`PiRpcAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs), and [`AcpAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs):
   * Stop directly mutating `context.PipelineContext.DataBag`.
   * Return `feedback` in `AgentRunResult.ReviewerFeedback`.
   * Return `canonicalTools.PendingProposals` in `AgentRunResult.Proposals`.
3. In [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs):
   * Inspect `runResult.ReviewerFeedback` and save to `PipelineDataBagKeys.ReviewerFeedback`.
   * Inspect `runResult.Proposals` and save to `PipelineDataBagKeys.PendingProposals`.

---

## 4. Verification & Testing Criteria

1. **Unit Tests:**
   * Verify `AgentSessionContext` exposes all promoted properties and defaults to null when not provided.
   * Verify `TaskOrchestrator` populates all promoted properties on `AgentSessionContext`.
   * Verify `DefaultBootstrapPromptBuilder` includes `review_diff` in rendered prompt when present in context.
   * Verify `CrucibleAgentHarness`, `PiRpcAgentHarness`, and `AcpAgentHarness` operate cleanly when `PipelineContext` is null.
   * Verify `AgentRunResult` correctly propagates `ReviewerFeedback` and `Proposals` into `PipelineDataBag` via `TaskOrchestrator`.
2. **Quality Gate Verification:**
   * Run all tests:
     `dotnet test --filter "Category!=Acceptance&Category!=E2E"`
   * Verify 100% pass rate.
