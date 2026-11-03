# 🛡️ Step 12.28C: Review Room Risk Report UI & Security Guardrail Settings (`SecurityGuardrailSettingsTab.razor`)

> **Feature Implementation Step (Part 3 of 3 — Review Room Risk Alerts, Interactive Findings Panel & Configurable Guardrail Settings UI)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.28A: Deterministic Diff, Payload, Supply-Chain & Blast-Radius Risk Scanner](STEP_12_28A_DETERMINISTIC_DIFF_AND_SUPPLY_CHAIN_RISK_SCANNER.md), [Step 12.28B: Cross-Model LLM-as-a-Judge Backdoor Scanner & Post-Review Guardrail Stage](STEP_12_28B_LLM_JUDGE_BACKDOOR_SCANNER_AND_POST_REVIEW_GUARDRAIL.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Web`](../../src/NightCodeForge.Web), [`tests/NightCodeForge.Web.Tests`](../../tests/NightCodeForge.Web.Tests)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Review Room Risk Reporting (`Review.razor` & `SecurityGuardrailReportPanel.razor`):**
   - Present all detected model-introduced security, payload, supply-chain, and blast-radius risks directly in the **Review Room** (`/review/{TaskId}`) so the maintainer can inspect every finding alongside the side-by-side Git diff.
   - Display a prominent warning banner when `MergeMode.Automatic` was automatically downgraded to `MergeMode.ManualReview` due to detected risks.
   - Allow clicking any finding with a `FilePath` to immediately select and jump to that file in `DiffWorkspace.razor`.
2. **Configurable Well-Known Lists, Thresholds & Judge Model UI (`SecurityGuardrailSettingsTab.razor`):**
   - Provide a dedicated **Security Guardrail** configuration tab in `/settings` (backed by `ISecurityGuardrailSettingsService`) so maintainers can customize:
     - The Cross-Model **LLM-as-a-Judge** model (`jetelain/Flint`, `jetelain/Gemma-4-26B-A4B`, or custom model ID).
     - **Blast Radius thresholds** ($N\%$ codebase impact, $M$ files, $X$ changed lines).
     - **Payload & Entropy thresholds** (Shannon entropy bits/char, min Base64/Hex length, max line length).
     - **Well-Known Lists** (`WellKnownSafeDomains`, `WellKnownSafePackages`, `AllowedIpAddresses`, `AllowedImageExtensions`, and `ExcludedFileGlobs`).

---

## 2. Architecture & Technical Specification

### 2.1 Review Room Integration (`TaskReviewService`, `Review.razor`, `SecurityGuardrailReportPanel.razor`)

1. **`TaskReviewSnapshot` Enrichment ([`TaskReviewService.cs`](../../src/NightCodeForge.Web/Services/TaskReviewService.cs)):**
   - Extract `SecurityGuardrailReport?` from `PipelineDataBagKeys.SecurityGuardrailReport` in `TaskReviewService.GetSnapshotAsync` and attach it to `TaskReviewSnapshot`.
   - Keep all parsing and presentation mapping inside `TaskReviewService` (or a dedicated `SecurityGuardrailPresentationHelper`) in accordance with `AGENTS.md` UI separation rules.

2. **Auto-Merge Downgrade Alert Banner (`Review.razor`):**
   - When `_snapshot.SecurityGuardrailReport is { HasRisks: true }`:
     - Render a prominent alert banner (`id="alert-security-guardrail-risks"`) at the top of `Review.razor`:
       - If `AutoMergeDowngraded == true`:
         `"🛡️ Auto-Merge Blocked by Security Guardrail: {Count} potential risk(s) were detected after code review. Merge mode was automatically changed from Automatic to Manual Review."`
       - Otherwise:
         `"🛡️ Security Guardrail Alert: {Count} potential risk(s) detected in this task's changes. Please review all findings below before merging."`

3. **Security Guardrail Card in Quality Gate Metrics Strip (`Review.razor`):**
   - Add a 4th gate card (`id="card-metric-security-guardrail"`) next to Code Coverage, Compiler Warnings, and Test Suite:
     - **Clean (`HasRisks == false`):** `gate-pass` styling, value `0 Risks`, subtitle `Judge: {JudgeModelId ?? "Deterministic"}` + `${CodebaseImpactPercent:F1}% impact`.
     - **Risks Detected (`HasRisks == true`):** `gate-fail` styling, value `{Findings.Count} Risk(s)`, subtitle showing highest severity (`Critical` / `High` / `Medium`).

4. **Interactive Findings Panel (`SecurityGuardrailReportPanel.razor`):**
   - Rendered between the Review Room header card and `DiffWorkspace.razor` whenever `SecurityGuardrailReport.HasRisks == true` (and optionally expandable when clean to view scan telemetry).
   - Displays a structured list of `SecurityRiskFinding` items grouped or badged by `SecurityRiskCategory`:
     - **Category Badges:**
       - `🧠 Backdoor / LLM Judge` (`BackdoorPattern`)
       - `🌐 Public IP / Unknown URL` (`HardcodedNetworkEndpoint`)
       - `📦 Suspicious Payload / Binary` (`SuspiciousPayload`)
       - `📚 Unverified New Dependency` (`UnverifiedDependency`)
       - `⬇️ Dependency Version Downgrade` (`DependencyDowngrade`)
       - `📐 Blast Radius Exceeded` (`ExcessiveBlastRadius`)
     - **Each Finding Row Shows:**
       - Severity badge (`Critical`, `High`, `Medium`, `Low`), Rule ID (`NET-URL-001`, `DEP-NEW-001`, `LLM-JUDGE-001`, etc.), and Title.
       - Clickable file & line link (`FilePath:LineNumber`) that invokes `OnSelectFile(FilePath)` to highlight the file in `DiffWorkspace.razor`.
       - Detailed explanation and monospace offending code snippet (when present).
       - Quick **"Add to Well-Known Allowlist"** action button for `NET-URL-001` (adds domain to `WellKnownSafeDomains`) and `DEP-NEW-001` (adds package to `WellKnownSafePackages`) so maintainers can whitelist legitimate new domains or libraries directly from the Review Room in one click.

---

### 2.2 Security Guardrail Settings Persistence & UI (`ISecurityGuardrailSettingsService` & `SecurityGuardrailSettingsTab.razor`)

1. **`ISecurityGuardrailSettingsService`:**
   - Persists and loads `SecurityGuardrailOptions` (supporting global defaults with optional per-workspace `.nightcodeforge/security-guardrail.json` override).
   - Provides helper methods `AddWellKnownDomainAsync(string domain)`, `AddWellKnownPackageAsync(string packagePattern)`, and `ResetToDefaultsAsync()`.

2. **`SecurityGuardrailSettingsTab.razor` (in `/settings`):**
   - **Section 1: Cross-Model LLM-as-a-Judge Configuration:**
     - Toggle: `Enable LLM-as-a-Judge Backdoor Analysis`.
     - Judge Model selector / input with quick presets for [`jetelain/Flint`](https://ollama.com/jetelain/Flint) and [`jetelain/Gemma-4-26B-A4B`](https://ollama.com/jetelain/Gemma-4-26B-A4B), plus live availability indicator from `ILlmModelCatalogService`.
     - Max diff chunk size input (`MaxJudgeChunkBytes`, default `120 KB`).
   - **Section 2: Blast Radius Thresholds (Absolute + Relative):**
     - Numeric inputs for:
       - Max Codebase Impact % (`MaxCodebaseImpactPercent`, default `10.0%`)
       - Max File Impact % (`MaxFileImpactPercent`, default `15.0%`)
       - Max Affected Files (`MaxChangedFiles`, default `25`)
       - Max Changed Lines (`MaxChangedLines`, default `1,500`)
   - **Section 3: Payload & Obfuscation Thresholds:**
     - Numeric inputs for Shannon Entropy Threshold (default `4.50`), Min Literal Length (`20`), Min Suspicious Base64 Length (`64`), Min Suspicious Hex Length (`64`), and Max Line Length (`500`).
   - **Section 4: Editable Well-Known Safe Lists:**
     - Tag/list editors (with Add, Remove, and Reset to Defaults) for:
       - **Well-Known Safe URL Domains** (`WellKnownSafeDomains`, e.g., `github.com`, `*.nuget.org`, `schemas.microsoft.com`)
       - **Well-Known Safe External Packages** (`WellKnownSafePackages`, e.g., `Microsoft.*`, `System.*`, `Pmad.*`, `xunit*`)
       - **Allowed Public IPs** (`AllowedIpAddresses`)
       - **Allowed Valid Image Extensions** (`AllowedImageExtensions`, default `.png, .jpg, .jpeg, .gif, .webp, .ico`)

---

## 3. Definition of Done (DoD) Checklist

- [ ] Implement `ISecurityGuardrailSettingsService` for loading, saving, and updating `SecurityGuardrailOptions` (including 1-click domain/package allowlisting and reset-to-defaults).
- [ ] Enrich `TaskReviewSnapshot` and `TaskReviewService` with `SecurityGuardrailReport` and presentation helpers.
- [ ] Create `SecurityGuardrailReportPanel.razor` and integrate the Security Guardrail alert banner, 4th quality gate metric card, interactive file jump links, and 1-click allowlist buttons into `Review.razor`.
- [ ] Create `SecurityGuardrailSettingsTab.razor` and register it in `/settings` (`Settings.razor`).
- [ ] Write bUnit and service unit tests in `tests/NightCodeForge.Web.Tests` verifying `Review.razor` risk banner rendering, auto-merge downgrade notice, finding click file selection, 1-click allowlist addition, and `SecurityGuardrailSettingsTab.razor` configuration saving.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

