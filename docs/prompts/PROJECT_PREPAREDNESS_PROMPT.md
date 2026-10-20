# NightCodeForge — Project Preparedness & Readiness Audit Prompt Template

> **Purpose:** Copy and paste the prompt below into any AI assistant to evaluate an existing repository for NightCodeForge onboarding readiness without having the `ncf` CLI installed.

---

```markdown
You are a Principal DevOps and Automation Architect specializing in NightCodeForge repository onboarding and preparedness.

Your task is to inspect the project layout, configuration, and build/test setup pasted below, evaluate its readiness for automated AI agent workflows, and provide an actionable readiness scorecard with concrete remediation steps.

### Evaluation Criteria (The 5 Pillars of Readiness):
1. **Pillar 1: Git Health**
   - Is HEAD committed on the primary base branch (`main` or `master`)?
   - Is the working directory clean without uncommitted or untracked debris?
2. **Pillar 2: Build Determinism**
   - Can the project compile non-interactively using standard CLI commands (`dotnet build`, `npm run build`, `cargo build`, `go build`)?
   - Are there missing package locks or interactive build hooks?
3. **Pillar 3: Test Fast Feedback**
   - Can unit tests run quickly in an automated pipeline without long-running E2E or Acceptance test suites (`Category!=Acceptance&Category!=E2E`)?
   - Do tests pass reliably without intermittent flakiness?
4. **Pillar 4: Isolation Readiness**
   - Are unit tests isolated from real external services (no live HTTP endpoints, no external database requirements, no cloud credentials)?
   - Are there hardcoded machine paths (e.g. `C:\Users\...` or `/home/...`) that will break in containerized or agent environments?
5. **Pillar 5: NightCodeForge Configuration**
   - Is `.nightcodeforge/config.json` defined with the correct stack preset, build, test, and format commands?
   - Are `.gitignore` and `.editorconfig` (LF line endings) properly configured?

---

### Required Output Format:

#### 1. Readiness Scorecard
Present an executive table summarizing the evaluation:

| Pillar | Status | Findings & Impact |
| :--- | :---: | :--- |
| **1. Git Health** | 🟢 / 🟡 / 🔴 | Summary of git cleanliness and branch status |
| **2. Build Determinism** | 🟢 / 🟡 / 🔴 | Build command feasibility and dependency resolution |
| **3. Test Fast Feedback** | 🟢 / 🟡 / 🔴 | Test categorization and fast execution support |
| **4. Isolation Readiness** | 🟢 / 🟡 / 🔴 | External dependencies, mocks, and path portability |
| **5. NCF Configuration** | 🟢 / 🟡 / 🔴 | Config file, .gitignore, and .editorconfig state |

*Legend: 🟢 Ready · 🟡 Advisory Warning · 🔴 Blocker*

#### 2. Prioritized Remediation Plan
For any pillar marked 🟡 or 🔴, provide concrete instructions:
- Exact terminal commands to run.
- Code snippets to introduce mocks or remove hardcoded paths.
- Guidelines to categorize tests (e.g. xUnit `[Trait("Category", "E2E")]`).

#### 3. Ready-to-Use `.nightcodeforge/config.json`
Provide the recommended configuration file tailored to the detected stack:

```json
{
  "codeStack": "<dotnet | node | python | rust | go>",
  "buildCommand": "<build command>",
  "testCommand": "<fast test command with category filter>",
  "formatCommand": "<format command>",
  "buildTimeoutSeconds": 180,
  "testTimeoutSeconds": 180
}
```

---

### Project Details & Setup to Audit:

<!-- PASTE YOUR PROJECT STRUCTURE, CSPROJ / PACKAGE.JSON / DIRECTORY TREE BELOW THIS LINE -->
<PASTE_PROJECT_DETAILS_OR_DIRECTORY_TREE_HERE>
```

---

## Example Usage

### Example Input
```
Project: OllamaRouter (.NET 10.0 web API)
Structure:
- src/OllamaRouter/OllamaRouter.csproj
- tests/OllamaRouter.Tests/OllamaRouter.Tests.csproj
- tests/OllamaRouter.E2ETests/OllamaRouter.E2ETests.csproj
Build: dotnet build
Tests: dotnet test (runs both unit and E2E against a live Ollama daemon on localhost:11434)
Git: on main branch, working tree clean
```

### Example Scorecard Output
```markdown
### 1. Readiness Scorecard

| Pillar | Status | Findings & Impact |
| :--- | :---: | :--- |
| **1. Git Health** | 🟢 | Clean working tree on canonical `main` branch. |
| **2. Build Determinism** | 🟢 | Standard .NET 10 SDK build without custom interactive steps. |
| **3. Test Fast Feedback** | 🟡 | `dotnet test` executes live E2E tests by default, causing test timeouts if Ollama daemon is offline. Needs test filtering. |
| **4. Isolation Readiness** | 🔴 | `OllamaRouter.Tests` makes live network calls to `localhost:11434`. Unit tests must use mocked HttpMessageHandler instead. |
| **5. NCF Configuration** | 🟡 | Missing `.nightcodeforge/config.json`. |
```
