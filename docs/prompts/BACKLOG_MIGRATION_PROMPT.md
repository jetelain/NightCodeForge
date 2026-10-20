# NightCodeForge — Backlog Migration Prompt Template

> **Purpose:** Copy and paste the prompt below into any AI assistant (ChatGPT, Claude, Gemini, Cursor, Antigravity) along with your existing project roadmap, `TODO.md`, or issue list to generate standard NightCodeForge task files.

---

```markdown
You are an expert software architect and NightCodeForge backlog migration specialist.

Your task is to analyze the backlog items, roadmap items, or issue lists pasted below, and convert them into cohesive, PR-sized NightCodeForge tasks saved as markdown files under `docs/tasks/`.

### Granularity & Sizing Rules:
1. **One Task = One Complete Pull Request:** Every task must be implementable, compilable, and testable within a single automated pipeline execution.
2. **No Micro-Tasks:** Do NOT create standalone tasks for trivial changes like "Add config key", "Create interface", or "Fix typo". Group related changes into a cohesive feature task.
3. **No Monolithic Epics:** Break multi-month or multi-component features into sequential, milestone-sized tasks where each milestone leaves the codebase healthy and passing tests.
4. **Explicit Dependencies:** Use the `depends_on` field to order tasks chronologically so prerequisites are merged before downstream features.

### Priority Levels:
- `1`: Critical / Urgent (P1) — Immediate blockers, hotfixes, security vulnerabilities.
- `2`: High (P2) — Core architectural prerequisites and foundational infrastructure.
- `3`: Normal (P3) — Default standard feature development.
- `4`: Low (P4) — Minor enhancements, secondary optimizations, polish.
- `5`: Lowest / Trivial (P5) — Cosmetic refactorings, documentation typos, background cleanups.

### Output Format:
For each migrated task, output a self-contained markdown file block formatted exactly as follows, with the recommended relative file path `docs/tasks/{yyyy-MM-dd}-{slug}.md` (e.g. `docs/tasks/2026-10-20-model-health-check.md`):

```markdown
---
title: "<Imperative Task Title>"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# <Imperative Task Title>

## 📋 Requirements & Description

### Summary
<Concise explanation of user or system value delivered by this task.>

### Implementation Checklist
- [ ] <Sub-task 1: Concrete technical item to implement>
- [ ] <Sub-task 2: Concrete technical item to implement>
- [ ] <Sub-task 3: Unit test implementation>

### Affected Files
- `<path/to/primary/file.ext>`: <Short description of intended changes>
- `<tests/path/to/test/file.ext>`: <Unit test assertions>

### Acceptance Criteria
- [ ] <Verifiable criterion 1, e.g. HTTP status codes, specific payloads>
- [ ] <Verifiable criterion 2, e.g. domain exception throwing on invalid input>
- [ ] <Verifiable criterion 3, e.g. fast unit tests pass under deterministic test runner>

### Prerequisite Dependencies
- <None or titles/IDs of prerequisite tasks that must merge first>
```

---

### Source Backlog to Migrate:

<!-- PASTE YOUR ROADMAP, TODO.md, OR ISSUE LIST BELOW THIS LINE -->
<PASTE_YOUR_ROADMAP_OR_ISSUES_HERE>
```

---

## Example Usage

### Example Input
```
- [ ] Add model health checking to OllamaRouter so failing instances are detected and removed from routing pool. Needs timeout handling and unit tests. Depends on config client setup.
```

### Example Output
```markdown
---
title: "Implement Ollama model health check endpoint"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---

# Implement Ollama model health check endpoint

## 📋 Requirements & Description

### Summary
Provide an endpoint that verifies connectivity and latency of the configured Ollama models.
When an Ollama host is unreachable or unhealthy, the endpoint returns a structured health status
allowing upstream consumers to route requests away from failing instances.

### Implementation Checklist
- [ ] Create ModelHealthCheck service implementing standard health checks
- [ ] Add timeout and retry logic for unresponsive Ollama instances
- [ ] Register health check in dependency injection container
- [ ] Add unit tests verifying healthy, degraded, and unreachable response scenarios

### Affected Files
- `src/OllamaRouter/Health/ModelHealthCheck.cs`: Health check implementation checking model connectivity
- `tests/OllamaRouter.Tests/ModelHealthCheckTests.cs`: Unit test suite covering success, timeout, and failure cases

### Acceptance Criteria
- [ ] Returns HTTP 200 OK with model latency metrics when Ollama host is reachable
- [ ] Returns HTTP 503 Service Unavailable when Ollama host is unreachable or times out
- [ ] Fast unit tests pass with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`
- [ ] Does not make real network calls during unit tests (uses mocked HTTP handler or fake client)

### Prerequisite Dependencies
- `Configure Ollama endpoint client options`: Requires configuration models and client registration to exist first
```
