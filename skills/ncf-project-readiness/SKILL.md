---
name: ncf-project-readiness
version: 1
description: Interactive remediation companion to ncf init and ncf audit; interprets scorecards, guides non-deterministic test and isolation fixes, and verifies readiness.
---

# Project Readiness Remediation Skill (`ncf-project-readiness`)

## 1. Executive Role & Objective
This skill serves as the **interactive remediation companion** to the deterministic `ncf init` and `ncf audit` CLI commands (from `NightCodeForge.Cli`).

While the `ncf` CLI deterministically generates configuration, merges `.gitignore`, updates `.editorconfig`, formats code, and validates readiness, this skill assists developers with **non-deterministic diagnostics and code remediation** when audits fail or emit warnings.

> **Crucial Rule:** Never re-implement deterministic checks in prose. Defer to the CLI audit output (`.nightcodeforge/audit-report.md` or console) as the authoritative source of truth.

---

## 2. The 5 Pillars of Readiness
The `ncf audit` command evaluates repositories across 5 pillars. This skill guides fixes for non-green results:

| Pillar | Diagnostic Check | Remediation Focus |
| :--- | :--- | :--- |
| **1. Git Health** | Committed HEAD, clean working tree, on primary branch (`main` or `master`). | Guide committing or stashing uncommitted changes; switch to canonical base branch. |
| **2. Build Determinism** | Clean, non-interactive build via CLI (`dotnet build --nologo` or stack equivalent). | Resolve missing package references, restore issues, or interactive build tool hooks. |
| **3. Test Fast Feedback** | Fast test suite passes on base branch (`Category!=Acceptance&Category!=E2E`). | Debug and fix failing unit tests; eliminate brittle timing assumptions or ordering dependencies. |
| **4. Isolation Readiness** | Scan for hardcoded machine paths (`C:\Users\...`, `/home/...`) and unmocked network/external services in unit tests. | Replace hardcoded absolute paths with workspace-relative paths or options; introduce test doubles (Moq, NSubstitute, stubs). |
| **5. NCF Configuration** | `.nightcodeforge/config.json` present, valid JSON, matching `ncfVersion`, skills installed. | Run `ncf init` to bootstrap or re-synchronize configuration and installed skills. |

---

## 3. Interactive Remediation Workflow

### Step 1: Ingest Audit Scorecard
- Check for `.nightcodeforge/audit-report.md` in the workspace root.
- If the report is missing or stale, instruct the user to run:
  ```bash
  ncf audit
  ```
  *(Or `ncf init` if the repository has not been initialized yet).*

### Step 2: Interpret Findings & Prioritize
Classify findings into:
- 🔴 **Blockers (Critical):** Broken builds, failing unit tests, or uncommitted git trees. These prevent NightCodeForge agents from establishing a deterministic baseline.
- 🟡 **Warnings (Advisory):** Unmocked external services, hardcoded file paths, or version mismatches between installed skills and CLI.
- 🟢 **Healthy:** Pillars that passed verification cleanly.

Always present a clear, prioritized action plan starting with 🔴 blockers.

### Step 3: Guide Code & Configuration Fixes

#### A. Remediation: Failing Base Unit Tests
- Execute fast tests using the stack's category-filtered command (e.g. `dotnet test --filter "Category!=Acceptance&Category!=E2E"`).
- Inspect the stack trace of failing tests.
- Guide the developer to fix broken assertions, update outdated test expectations, or correct initialization bugs.
- Verify that all fast tests pass locally before proceeding.

#### B. Remediation: Isolation & Unmocked External Dependencies
- Locate tests making real HTTP calls, socket connections, or disk writes to user directories.
- Refactor classes to inject service dependencies rather than instantiating concrete network clients.
- Introduce unit test mocks using Moq / NSubstitute or fake test handlers:
  ```csharp
  // Example: Mocking external HTTP client with HttpMessageHandler stub
  var mockHandler = new Mock<HttpMessageHandler>();
  mockHandler.Protected()
      .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
  ```

#### C. Remediation: Hardcoded Machine Paths
- Search for absolute paths (`/home/`, `C:\Users\`, `D:\`) in source and test files.
- Replace with `Path.Combine(AppContext.BaseDirectory, ...)` or injectable configuration parameters.

### Step 4: Re-Audit & Validate Green State
Once code fixes are applied, re-run:
```bash
ncf audit
```
Verify that `.nightcodeforge/audit-report.md` reports 🟢 across all pillars. Once green, the repository is fully prepared for NightCodeForge automated agent workflows.
