# 🛡️ Step 12.7C: Unified Security Review UI (DevSkim + DiffGuard), Inspector Panel & Security Settings

> **Phase 12 Implementation Step (Part 3 of 3 — Single Unified Developer Experience in Review Room, Task Execution Inspector & `/settings` for DevSkim SAST + DiffGuard Guardrail)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.7A: Unified Security Foundation & DevSkim SAST Backend](STEP_12_7A_UNIFIED_SECURITY_DOMAIN_AND_DEVSKIM_SAST_BACKEND.md) | [Step 12.7B: DiffGuard CLI Security Scanner & Post-Review Guardrail Backend](STEP_12_7B_DIFFGUARD_POST_REVIEW_GUARDRAIL_BACKEND.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Web`](../../src/NightCodeForge.Web), [`tests/NightCodeForge.Web.Tests`](../../tests/NightCodeForge.Web.Tests) | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **One Unified Security Developer Experience:** Present feedback from both **DevSkim SAST** (Step 12.7A) and **DiffGuard Guardrail** (Step 12.7B) through **one single UI** in the Review Room, Task Execution Inspector, and Settings — eliminating competing quality gate cards, stacked banners, and split configuration tabs.
2. **Clear Actionable Distinction in One View:**
   - **DevSkim SAST findings** (`Disposition = AgentLoopback` / `Advisory`): Blocking points are intended to be solved autonomously by the coding LLM during Stage 4/6 loopbacks; any remaining advisory notes (or unresolved blockers) appear in the Review Room alongside an optional **Draft Review Comment** action if the maintainer wants the LLM to fix an advisory note via *Request Changes*.
   - **DiffGuard Guardrail findings** (`Disposition = HumanReviewRequired` / `Advisory`): Blocking points never loop back to the LLM and instead trigger human review (and automatically downgrade `MergeMode.Automatic` to `MergeMode.ManualReview`); they appear prominently with a clear **"Triggers Human Review"** / **"Auto-Merge Blocked"** indicator and 1-click allowlist actions.
3. **Strict UI & Presentation Separation ([AGENTS.md](../../AGENTS.md)):** Keep all DataBag extraction, severity/source/disposition badge formatting, filtering/sorting, and allowlist action mapping inside `NightCodeForge.Core` / `NightCodeForge.Web/Services` (`TaskReviewService`, `SecurityPresentationHelper`) rather than Razor `@code` blocks.

---

## 2. Technical Specification

### 2.1 Review Room Integration (`TaskReviewService`, `Review.razor`, `SecurityReviewPanel.razor`, `DiffWorkspace.razor`)

1. **`TaskReviewSnapshot` Enrichment ([`TaskReviewService.cs`](../../src/NightCodeForge.Web/Services/TaskReviewService.cs)):**
   - Extend [`TaskReviewSnapshot`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) with `TaskSecurityReport? SecurityReport = null`.
   - Extract `TaskSecurityReport` via `TaskSecurityReportExtractor.Extract(task)` inside `TaskReviewService.GetSnapshotAsync`.
   - Create `SecurityPresentationHelper` in `src/NightCodeForge.Web/Services/` to format gate card status, summary subtitles, disposition labels, severity/category/source badge CSS classes, filter counts, and contextual 1-click action descriptors.

2. **Single Unified Alert Banner (`Review.razor`):**
   - When `_snapshot.SecurityReport` has `HasHumanReviewBlockers || HasUnresolvedAgentBlockers`:
     - Render one prominent alert banner (`id="alert-security-review"`) at the top of `Review.razor`:
       - If `AutoMergeDowngraded == true`:
         `"🛡️ Auto-Merge Blocked by Security Guardrail: {BlockingCount} point(s) require human review. Merge mode was automatically changed from Automatic to Manual Review."`
       - Else if `HasHumanReviewBlockers`:
         `"🛡️ Human Security Review Required: DiffGuard flagged {HumanReviewCount} point(s) requiring maintainer inspection before merging."`
       - Else (`HasUnresolvedAgentBlockers`):
         `"🛡️ Security Gate Alert: {AgentBlockerCount} unresolved SAST point(s) remain from DevSkim."`

3. **Single 4th Quality Gate Card in `#gate-metrics-grid` (`Review.razor`):**
   - Add one unified **Security** card (`id="card-metric-security"`) next to Code Coverage, Compiler Warnings, and Test Suite:
     - **Pass (`gate-pass`):** Zero blocking points and zero advisory findings (`0 Findings`, subtitle summarizing active scanners, e.g., `DevSkim: Clean · DiffGuard: Clean (1.4% impact)`).
     - **Warning (`gate-warn`):** Zero blocking points, advisory findings present and/or an enabled scanner skipped (`{AdvisoryCount} Advisory`, subtitle e.g., `DevSkim: 2 notes · DiffGuard: Clean`).
     - **Fail (`gate-fail`):** Any `HumanReviewRequired` (DiffGuard) or unresolved `AgentLoopback` (DevSkim) point (`{BlockingCount} Risk(s)`, subtitle showing highest severity and `Auto-Merge Blocked` when `AutoMergeDowngraded == true`).

4. **Unified Interactive Security Review Panel (`SecurityReviewPanel.razor`):**
   - Rendered between the Review Room header card (`#review-header-section`) and [`DiffWorkspace.razor`](../../src/NightCodeForge.Web/Components/Review/DiffWorkspace.razor) whenever `SecurityReport is { HasAnyExecution: true }`.
   - **Collapsible Behavior:**
     - Auto-expanded whenever `AllFindings.Count > 0` or a scanner notice exists (e.g., same-model-family judge warning).
     - Collapsed into a compact summary strip when completely clean (expandable on click to inspect scanner telemetry: DevSkim scanned files/duration/status, DiffGuard judge model/blast-radius %/duration/status).
   - **Filter Chips:**
     - `All ({AllFindings.Count})`
     - `Human Review ({HumanReviewFindings.Count})` — DiffGuard blocking points
     - `SAST & Advisory ({AdvisoryAndSastCount})` — DevSkim points and below-threshold advisory points
   - **Finding Ordering:** `HumanReviewRequired` and `AgentLoopback` points first, then `Advisory`, each ordered by `Severity` descending → `FilePath` → `StartLine`.
   - **Unified Finding Rows (via shared `SecurityFindingsList.razor`):**
     - **Badges:** Source (`🛡️ DevSkim` / `🔍 DiffGuard` / `🧠 LLM Judge`), Severity (`Critical`, `High`, `Medium`, `Low`, `Info`), Disposition (`Triggers Human Review` / `Blocks Auto-Merge` / `LLM Loopback Gate` / `Advisory`), and Category (`SecurityFindingCategory`).
     - **Details:** `RuleId`, `Title`, `Message`, optional `Recommendation`, and monospace `Snippet` (when present).
     - **Click-to-Jump (`DiffWorkspace.razor`):** Clickable `FilePath:StartLine` link invokes `OnSelectFile(FilePath)` to select and highlight that file in [`DiffWorkspace.razor`](../../src/NightCodeForge.Web/Components/Review/DiffWorkspace.razor).
     - **Per-File Indicators in `DiffWorkspace.razor`:** Display a compact finding count badge next to files in the diff file tree that have security points.
     - **Contextual 1-Click Actions:**
       - DiffGuard `NET-URL-*` (with `AllowlistTarget`): **"Allow Domain"** → calls `ISecuritySettingsService.AddWellKnownDomainAsync(finding.AllowlistTarget, workspaceId)` and surfaces toast feedback.
       - DiffGuard `DEP-NEW-*` (with `AllowlistTarget`): **"Allow Package"** → calls `ISecuritySettingsService.AddWellKnownPackageAsync(finding.AllowlistTarget, workspaceId)` and surfaces toast feedback.
       - DevSkim findings (with `AllowlistTarget` / `RuleId`): **"Ignore Rule"** → calls `ISecuritySettingsService.AddSastIgnoredRuleAsync(finding.RuleId, workspaceId)` and surfaces toast feedback.
       - Any finding with `FilePath`: **"Draft Review Comment"** → adds a pre-filled inline review comment in [`ReviewCommentStateService`](../../src/NightCodeForge.Web/Services/ReviewCommentModels.cs) at `(FilePath, StartLine ?? 1)` so the maintainer can send an advisory point or DiffGuard issue back to the coding LLM via *Request Changes*.

---

### 2.2 Task Execution Inspector Integration (`SecurityFindingsList.razor`, `StageConversationViewer.razor`, `TaskExecutionInspector.razor`)

1. **Shared `SecurityFindingsList.razor` Component:**
   - Extract the finding row list into a shared Blazor component (`src/NightCodeForge.Web/Components/Review/SecurityFindingsList.razor` or shared folder) consumed by both `SecurityReviewPanel.razor` (Review Room, with interactive file-jump & action buttons enabled) and the Inspector (read-only mode).
2. **Stage 4 & Stage 6 Inspector Visibility ([`StageConversationViewer.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor) & [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor)):**
   - When inspecting either a standalone `deterministic-sast` / `deterministic-security-guardrail` stage run or a composite stage run (`PostImplementationGates` / `PostReviewVerification`) that executed `sast` or `security-guardrail`, render an expandable **"Security Findings"** section showing the stage's findings via `SecurityFindingsList.razor` (including historical Stage 4 failed loopback findings that the LLM subsequently remediated).

---

### 2.3 Unified Security Settings Tab (`SecuritySettingsTab.razor` in `/settings`)

Register one **`🛡️ Security & Guardrails`** tab (`SecuritySettingsTab.razor`) in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) under **both `SettingsScope.Global` and `SettingsScope.Workspace`**, backed by `ISecuritySettingsService` (Steps 12.7A/12.7B):

1. **Section 1 — DevSkim SAST Gate (Stage 4 & Stage 6 — Autonomous LLM Loopback):**
   - Toggle: `Enable DevSkim SAST Gate` (`Sast.Enabled`).
   - Loopback failure severity threshold selector (`Critical`, `High` *(Important)*, `Medium` *(Moderate)*, `Low` *(BestPractice)*).
   - Tag/list editors for **Ignored Rule IDs** (`Sast.IgnoreRules`, e.g., `DS126858`) and **Ignored Path Globs** (`Sast.IgnoreGlobs`).
   - Toggle: `Allow Missing DevSkim CLI (Skip Instead of Fail)` (`Sast.AllowMissingTool`).
   - Numeric input: `SAST Timeout (Seconds)` (`Sast.TimeoutSeconds`).
   - Optional text input: `Custom SAST Command Override` (`Sast.CustomCommand`).

2. **Section 2 — DiffGuard Guardrail (Stage 6 — Human Review Gate):**
   - Toggle: `Enable Post-Review DiffGuard Guardrail` (`Guardrail.Enabled`).
   - Minimum risk severity threshold selector (`Low`, `Medium`, `High`, `Critical`).
   - **Cross-Model LLM-as-a-Judge Configuration:**
     - Toggle: `Enable LLM-as-a-Judge Backdoor Analysis` (`Guardrail.EnableLlmJudge`).
     - Judge Model selector / input with quick presets for [`jetelain/Flint`](https://ollama.com/jetelain/Flint) and [`jetelain/Gemma-4-26B-A4B`](https://ollama.com/jetelain/Gemma-4-26B-A4B), plus live availability indicator from `ILlmModelCatalogService`.
     - Numeric input: `Max Judge Chunk Bytes` (`Guardrail.MaxJudgeChunkBytes`, default `120,000`).
     - Toggle: `Filter Deleted Lines from Judge Prompt` (`Guardrail.FilterDeletedLines`).
   - **Blast Radius Thresholds (Absolute + Relative):**
     - `MaxCodebaseImpactPercent` (default `10.0%`), `MaxFileImpactPercent` (default `15.0%`), `MaxChangedFiles` (default `25`), `MaxChangedLines` (default `1,500`).
   - **Payload & Obfuscation Thresholds:**
     - `ShannonEntropyThreshold` (default `4.50`), `EntropyMinStringLength` (default `20`), `MinSuspiciousBase64Length` (default `64`), `MinSuspiciousHexLength` (default `64`), `MaxLineLength` (default `500`).
   - **Editable Well-Known Safe Lists (Add / Remove / Reset to Defaults):**
     - `WellKnownSafeDomains` (e.g., `github.com`, `*.nuget.org`, `schemas.microsoft.com`)
     - `WellKnownSafePackages` (e.g., `Microsoft.*`, `System.*`, `Pmad.*`, `xunit*`)
     - `AllowedIpAddresses`
     - `AllowedImageExtensions` (default `.png, .jpg, .jpeg, .gif, .webp, .ico`)
     - `ExcludedFileGlobs` & `TestFileGlobs`
     - Toggles: `EnableTestTolerance` and `FlagPrivateNetworkIps`.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Add `TaskSecurityReport? SecurityReport` to [`TaskReviewSnapshot`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) and extract it in [`TaskReviewService`](../../src/NightCodeForge.Web/Services/TaskReviewService.cs).
- [ ] Implement `SecurityPresentationHelper` in `src/NightCodeForge.Web/Services/` (badge classes, disposition labels, scanner summaries, filter predicates, and action descriptors).
- [ ] Create shared `SecurityFindingsList.razor` and `SecurityReviewPanel.razor`, and integrate the unified alert banner (`#alert-security-review`), single 4th Security metric card (`#card-metric-security`), filter chips, file jump links, per-file diff-tree badges, and 1-click actions (`Allow Domain`, `Allow Package`, `Ignore Rule`, `Draft Review Comment`) into [`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor) and [`DiffWorkspace.razor`](../../src/NightCodeForge.Web/Components/Review/DiffWorkspace.razor).
- [ ] Integrate expandable Security Findings (`SecurityFindingsList.razor`) into [`StageConversationViewer.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor) / [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor) for both Stage 4 (`sast`) and Stage 6 (`sast` + `security-guardrail`) runs.
- [ ] Create `SecuritySettingsTab.razor` covering both DevSkim SAST and DiffGuard Guardrail settings, and register it in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) for both Global and Workspace scopes.
- [ ] Write unit and bUnit tests in `tests/NightCodeForge.Web.Tests` verifying `SecurityPresentationHelper`, unified 4th card states (`pass`/`warn`/`fail`), unified alert banner & auto-merge downgrade notice, filter chips, finding click file selection, 1-click allowlist/ignore-rule/draft-comment actions, Inspector Stage 4 & Stage 6 rendering, and `SecuritySettingsTab.razor` Global & Workspace saving.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`, and run the full test suite on completion per [AGENTS.md](../../AGENTS.md).
