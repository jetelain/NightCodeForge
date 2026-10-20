---
name: ncf-ideation
version: 1
description: Guided interactive brainstorming and architectural decomposition for new features into docs/tasks with YAML frontmatter and database sync.
---

# Ideation & Architecture Skill (`ncf-ideation`)

## 1. Executive Role & Objective
This skill guides developers through interactive brainstorming, technical discovery, and architectural decomposition for new features, subsystems, or refactorings in a repository.

It is designed for **external AI harnesses** (Antigravity IDE, Claude Code, Cursor, or terminal assistants) operating directly on the workspace repository.

It translates developer intent and architectural concepts into well-scoped, PR-sized **NightCodeForge** task markdown files stored in `docs/tasks/`, where they are automatically synchronized into the NightCodeForge database.

---

## 2. Managing Task Files in `docs/tasks/`

NightCodeForge manages project tasks as markdown files located under:
```
docs/tasks/
├── 2026-10-20-implement-model-latency-tracker.md
├── 2026-10-20-add-sliding-window-percentiles.md
└── archive/
```

### File Naming & Organization
- Name new task files `docs/tasks/{yyyy-MM-dd}-{slug}.md` or `docs/tasks/{slug}.md` (e.g. `docs/tasks/model-latency-tracker.md`).
- NightCodeForge canonicalizes filenames to `{yyyy-MM-dd}-{slug}.md` upon synchronization.
- When tasks are completed or cancelled, NightCodeForge moves them to `docs/tasks/archive/`.

---

## 3. Markdown Task Specification & YAML Frontmatter Schema

Each task created under `docs/tasks/` must contain valid YAML frontmatter followed by structured requirements.

### YAML Frontmatter Schema

```yaml
---
title: "Implement ModelLatencyTracker in OllamaRouter"
priority: 3
merge_mode: "manual_review_required"
base_branch: "main"
depends_on: []
---
```

#### Schema Field Definitions:
- `title` *(string, required)*: Clear, imperative capability title (e.g. `Implement ModelLatencyTracker in OllamaRouter`).
- `priority` *(integer, optional, default: 3)*: Priority level from 1 to 5:
  - `1`: **Critical / Urgent (P1)** — Blockers, hotfixes, critical bugs.
  - `2`: **High (P2)** — Architectural prerequisites and foundational milestones.
  - `3`: **Normal (P3)** — Standard feature development (default).
  - `4`: **Low (P4)** — Minor enhancements, non-critical polish.
  - `5`: **Lowest / Trivial (P5)** — Cosmetic cleanup, typos.
- `merge_mode` *(string, optional, default: "manual_review_required")*:
  - `"manual_review_required"`: Requires maintainer sign-off in NightCodeForge Review Room before merging.
  - `"automatic"`: Automatically merges to base branch once pipeline stages, unit tests, and SAST checks pass.
- `base_branch` *(string, optional, default: workspace default, e.g. "main")*: Target Git branch for branching and merging.
- `depends_on` *(list of integers or strings, optional, default: `[]`)*: Prerequisite tasks that must complete first. Supports numeric IDs (`[ 101 ]`), markdown filenames (`[ "2026-10-20-task-alpha.md" ]`, `[ "task-alpha.md" ]`), or task slugs/titles (`[ "task-alpha" ]`, `[ "Task Alpha" ]`). Upon synchronization, NightCodeForge resolves string references to database IDs and normalizes the markdown file in-place to canonical integer IDs.
- `id` *(integer, omit for new tasks)*: NightCodeForge database ID. Leave omitted when creating new tasks; NightCodeForge populates it upon synchronization.
- `status` *(string, omit for new tasks)*: Workflow status (`Suggested`, `Open`, `InProgress`, `Review`, `Closed`, `Failed`). Newly created tasks start as `Suggested`.

### Markdown Body Specification

```markdown
# <Imperative Task Title>

## 📋 Requirements & Description

### Summary
<Concise explanation of the user or system value delivered by this task.>

### Implementation Checklist
- [ ] <Sub-task 1: Concrete technical item to implement>
- [ ] <Sub-task 2: Concrete technical item to implement>
- [ ] <Sub-task 3: Unit test implementation>

### Affected Files
- `<path/to/source.ext>`: <Short description of intended changes>
- `<tests/path/to/test.ext>`: <Unit test assertions>

### Acceptance Criteria
- [ ] <Verifiable criterion 1: expected behaviors, status codes, payload shapes>
- [ ] <Verifiable criterion 2: error handling and domain exceptions>
- [ ] <Verifiable criterion 3: deterministic unit test verification>

### Prerequisite Dependencies
- <None, or descriptive list of prerequisite tasks>
```

---

## 4. Automatic Database Synchronization

NightCodeForge maintains continuous bidirectional synchronization between markdown files in `docs/tasks/` and its database through `TaskMarkdownSyncService`:

1. **Automatic Intake & ID Allocation:**
   - A real-time `FileSystemWatcher` continuously monitors `docs/tasks/*.md`.
   - When a new markdown file is written, NightCodeForge creates a `ForgeTask` record in the database with status `Suggested`, registers dependencies, and allocates a unique numeric ID.
   - NightCodeForge rewrites the frontmatter in-place with `id: <allocated_id>`.
2. **Synchronized Modifications:**
   - Edits made on disk to `title`, `priority`, `merge_mode`, `base_branch`, `description`, or `depends_on` are immediately detected and synced to the database.
   - Each modification is recorded as an immutable temporal audit snapshot (`ForgeTaskHistory`).
3. **Bidirectional Reflection:**
   - Changes made inside NightCodeForge (workflow stage progression, status updates, agent execution logs, maintainer clarifications) are automatically reflected back into the markdown file under `## ❓ Human Clarifications (Q&A)`.

---

## 5. Managing Dependencies

Dependencies define the execution DAG (Directed Acyclic Graph):

1. **Prerequisite Semantics:**
   - A task cannot enter the active queue or be scheduled until all tasks listed in its `depends_on` have completed and merged.
2. **Referencing Existing Tasks:**
   - Use numeric database IDs: `depends_on: [ 101, 102 ]`.
   - Or reference existing tasks by file slug or title: `depends_on: [ "core-database-setup", "OAuth Authentication" ]`.
3. **Flexible Batch Authoring:**
   - When decomposing a feature into a batch of interdependent tasks, external harnesses and human developers do **not** need to wait for database IDs to be assigned.
   - Simply reference prerequisite tasks by filename (`"task-alpha.md"`), slug (`"task-alpha"`), or title (`"Task Alpha"`).
   - NightCodeForge analyzes the batch, orders task creation topologically so prerequisites receive IDs first, links the relationships in the database, and rewrites the markdown files in-place with normalized numeric IDs.
4. **Validation & Cycle Prevention:**
   - Referenced task IDs or names must exist within the workspace or within the pending batch.
   - Circular dependencies (`A -> B -> A`) and self-references (`A -> A`) are detected by cycle detection (`HasCycleDfs`) and rejected.

---

## 6. Sizing & Granularity Principles

1. **One Task = One Complete Pull Request:**
   - Every task represents a complete PR that builds deterministically, passes unit tests, passes SAST security gates, and undergoes code review.
2. **Never Create Micro-Tasks:**
   - Do NOT emit standalone tasks for trivial items (e.g. "Add config key", "Create empty interface", "Fix typo"). Group related sub-tasks into a cohesive feature task.
3. **Never Create Monolithic Epics:**
   - Break large features into milestone-sized PRs where each milestone compiles, has passing tests, and leaves the codebase in a healthy state.

---

## 7. Interactive Ideation Workflow

### Step 1: Codebase Exploration
- Examine repository layout, project manifests (`.csproj`, `package.json`, etc.), and `.nightcodeforge/config.json`.
- Inspect existing classes, domain models, services, and test patterns to identify reusable code and avoid duplication.
- Check existing `docs/tasks/` to ensure the proposed work does not overlap with existing tasks.

### Step 2: Interactive Interview
Engage the developer with focused, clarifying questions:
- *"What is the main user problem, capability, or architectural goal being targeted?"*
- *"Are there existing classes, abstractions, or services in the repository that should be extended rather than rebuilt?"*
- *"What test harness exists (unit tests, CLI invocation, integration tests) and what is the testing strategy?"*
- *"Are there specific non-functional constraints (e.g., performance budgets, thread safety, backward compatibility)?"*

### Step 3: Architectural Synthesis & Task Decomposition
Synthesize the requirements into sequential, PR-sized tasks following the contract in §3.

### Step 4: Write Task Markdown Files
Create the markdown files directly in `docs/tasks/` with YAML frontmatter. NightCodeForge will immediately synchronize them into the database and assign task IDs.
