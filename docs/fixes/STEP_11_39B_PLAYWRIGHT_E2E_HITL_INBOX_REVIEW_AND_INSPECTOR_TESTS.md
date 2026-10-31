# 🎭 Step 11.39B: Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings

> **Fix & Resiliency Specification (Part 2 of 2 — Human-in-the-Loop & Observability Browser E2E Suite)**  
> *Parent Plan:* [Fixes & Resiliency Roadmap](README.md)  
> *Prerequisite Step:* [Step 11.39A: Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests](STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)  
> *Target Components:* `tests/NightCodeForge.Web.Tests/E2E` | *Status:* **Completed** | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Motivation

Building on `PlaywrightWebHostFixture` from [Step 11.39A](STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md), NightCodeForge's most interactive Human-in-the-Loop (HITL) and observability pages—the **Human Question Inbox** (`/inbox`), **Review Room** (`/review`), **Task Execution Inspector** (`/inspector`), **Transcripts Browser** (`/transcripts`), and **Settings** (`/settings`)—need deterministic, LLM-free Playwright E2E coverage in a real Chromium browser.

Because these pages normally appear mid-pipeline or post-pipeline after several LLM stages, testing them via `AutonomousFeatureLifecycleTests` is slow and only covers auto-merge. By seeding the exact database and Git branch states via `PlaywrightWebHostFixture`, we can test every human review, question answering, diff viewing, and inspection interaction end-to-end in seconds without any LLM inference call.

---

## 2. Detailed Functional & Technical Requirements

All tests in `HitlAndObservabilityE2ETests.cs` share `[Collection("PlaywrightUiE2E")]`, are tagged with `[Trait("Category", "E2E")]`, and make **zero LLM calls**:

### 2.1 Human Question Inbox E2E (`QuestionInbox_SelectOptionsAndSubmitBatch_UpdatesDatabaseAndClearsTaskWait`)
- **Seeded State:**
  - Ensures background operations are paused (via `#nav-btn-pause-operations` or `IOperationsControlService`) so Hangfire does not immediately execute the resumed stage upon submission.
  - Seeds a workspace and a `ForgeTask` with `IsWaitingForHuman = true`, `Status = TaskOverallStatus.InProgress` (or `Open`), `CurrentStageId = StandardStageIds.TaskReview`, and a batch of 2 `Question` records sharing a non-empty `BatchId` (`QuestionStatus.Pending`, one single-choice with `OptionsJson` and one free-text question).
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/inbox`.
  - Verifies the grouped `QuestionCard` (`#question-card-{taskId}`) renders both questions in the batch along with option chips (`#btn-option-{question1Id}-0`, etc.).
  - Clicks an option chip on Question 1, types a custom answer into `#input-answer-{question2Id}` on Question 2, and clicks `#btn-submit-batch-{taskId}` ("Submit Batch (2)").
  - Verifies via `QueryDatabaseAsync` that both `Question` rows have `Status == QuestionStatus.Answered` and `Answer` populated, and that `ForgeTask.IsWaitingForHuman` is cleared (`false`).

### 2.2 Review Room Diff & Human Actions E2E (`ReviewRoom_RendersGitBranchDiff_And_HandlesRejectTaskAction`)
- **Seeded State:**
  - Uses `SyntheticRepoFixture` to create a real feature branch `feature/task-50` with a committed modification to `src/SyntheticLib/Calculator.cs`.
  - Seeds `ForgeTask` (`Id = 50`, `Status = TaskOverallStatus.Ready`, `MergeMode = MergeMode.ManualReviewRequired`, `BaseBranch = "main"`, `FeatureBranch = "feature/task-50"`, `HasExecutionRun = true`).
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/review/50`.
  - Verifies `Review.razor` loads Task #50, renders `Calculator.cs` in the changed files list, and displays the added lines inside `DiffWorkspace.razor`.
  - Navigates to the task details via `#btn-header-task-details` (`/w/{workspaceId}/tasks/50`), triggers `#btn-reject-task` to open `TaskRejectionModal`, fills `#reject-reason-input`, confirms via `#btn-confirm-reject`, and verifies the task transitions to `TaskOverallStatus.Rejected` in SQLite (or triggers `#btn-request-changes` directly in `Review.razor` to verify `ChangeRequestModal`).

### 2.3 Task Execution Inspector & Transcripts E2E (`TaskInspector_And_Transcripts_RenderSeededStagesAndConversationTurns`)
- **Seeded State:**
  - Seeds a `ForgeTask` (`HasExecutionRun = true`, `CurrentStageId = StandardStageIds.Implementation`) with 2 `Conversation` rows (`task-review` with `StageExecutionStatus.Completed`, and `implementation` with `StageExecutionStatus.Running`), `ConversationTurn` records (User prompt, Assistant thinking + tool call, Tool result, Assistant final summary), `ExecutionLog` entries, and a markdown transcript file in `{workspace.RepositoryPath}/docs/transcripts/`.
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/inspector/{taskId}`.
  - Verifies `PipelineStepper` displays the stages, clicking a stage loads its `ConversationTurn` cards in `StageConversationViewer`, and switching to the `🖥 Terminal & Logs` tab displays the seeded `ExecutionLog` lines.
  - Navigates to `/w/{workspaceId}/transcripts` and verifies `TranscriptBrowserService` lists the seeded markdown transcript file in the sidebar and renders its content when selected.

### 2.4 Settings & Agent Profile Configuration E2E (`SettingsPage_TabNavigation_And_WorkspaceUpdate_PersistsToDatabase`)
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/settings`.
  - Switches across the Workspace Settings, Agent Profiles, and Workflows tabs in Chromium, modifies a workspace setting (e.g. build/test timeout or global skills mode), saves, and verifies via `QueryDatabaseAsync` that the updated value is persisted in SQLite.

---

## 3. Verification & Acceptance Criteria

- [x] All 4 E2E tests in `HitlAndObservabilityE2ETests` pass in headless Chromium via `dotnet test --filter "Category=E2E&Category!=Acceptance"` with **zero LLM calls**.
- [x] Full test suite (including `Category=E2E`) passes cleanly.

