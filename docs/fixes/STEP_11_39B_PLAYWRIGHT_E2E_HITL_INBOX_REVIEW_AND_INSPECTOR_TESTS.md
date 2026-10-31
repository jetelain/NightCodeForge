# 🎭 Step 11.39B: Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings

> **Fix & Resiliency Specification (Part 2 of 2 — Human-in-the-Loop & Observability Browser E2E Suite)**  
> *Parent Plan:* [Fixes & Resiliency Roadmap](README.md)  
> *Prerequisite Step:* [Step 11.39A: Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests](STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)  
> *Target Components:* `tests/NightCodeForge.Web.Tests/E2E` | *Status:* **Ready for Implementation** | *Target Framework:* .NET 10 (C# 13)  
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
  - Seeds a workspace and a `ForgeTask` with `IsWaitingForHuman = true`, `Status = TaskOverallStatus.Open`, `CurrentStageId = StandardStageIds.TaskReview`, and a batch of 2 `Question` records (`QuestionStatus.Pending`, one single-choice with options JSON and one free-text question).
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/inbox`.
  - Verifies the question card renders both questions in the batch along with option chips.
  - Clicks an option chip on Question 1, types a custom answer into Question 2, and clicks the Submit button (Note: in paused/test mode, verify the answer submission persists `Question.Answer` and `QuestionStatus.Answered` in SQLite).

### 2.2 Review Room Diff & Human Actions E2E (`ReviewRoom_RendersGitBranchDiff_And_HandlesRejectTaskAction`)
- **Seeded State:**
  - Uses `SyntheticRepoFixture` to create a real feature branch `feature/task-50` with a committed modification to `src/SyntheticLib/Calculator.cs`.
  - Seeds `ForgeTask` (`Id = 50`, `Status = TaskOverallStatus.Ready`, `MergeMode = MergeMode.ManualReviewRequired`, `BaseBranch = "main"`, `FeatureBranch = "feature/task-50"`, `HasExecutionRun = true`).
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/review?taskId=50`.
  - Verifies `Review.razor` selects Task #50, renders `Calculator.cs` in the changed files list, and displays the added lines inside `DiffWorkspace.razor`.
  - Triggers the **Reject Task** action in the UI and verifies the task transitions to `TaskOverallStatus.Rejected` in SQLite and disappears from the pending review queue.

### 2.3 Task Execution Inspector & Transcripts E2E (`TaskInspector_And_Transcripts_RenderSeededStagesAndConversationTurns`)
- **Seeded State:**
  - Seeds a `ForgeTask` with 2 `StageExecutionRecord` rows (`task-review` Completed, `implementation` Running), a `Conversation` with `ConversationTurn` records (User prompt, Assistant thinking + tool call, Tool result, Assistant final summary), and `ExecutionLog` entries.
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/inspector/{taskId}`.
  - Verifies `PipelineStepper` displays the stages, clicking a stage loads its `ConversationTurn` cards in `StageConversationViewer`, and switching to the Terminal Console tab displays the seeded `ExecutionLog` lines.
  - Navigates to `/w/{workspaceId}/transcripts` and verifies the seeded conversation appears in the transcript browser and renders its turns when selected.

### 2.4 Settings & Agent Profile Configuration E2E (`SettingsPage_TabNavigation_And_WorkspaceUpdate_PersistsToDatabase`)
- **Browser Interaction & Assertions:**
  - Navigates to `/w/{workspaceId}/settings`.
  - Switches across the Workspace Settings, Agent Profiles, and Workflows tabs in Chromium, modifies a workspace setting (e.g. build/test timeout or global skills mode), saves, and verifies via `QueryDatabaseAsync` that the updated value is persisted in SQLite.

---

## 3. Verification & Acceptance Criteria

- [ ] All 4 E2E tests in `HitlAndObservabilityE2ETests` pass in headless Chromium via `dotnet test --filter "Category=E2E&Category!=Acceptance"` with **zero LLM calls**.
- [ ] Full test suite (including `Category=E2E`) passes cleanly.

