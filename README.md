# 🌙 NightCode**Forge**

> Let your GPU code for you overnight

[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Architecture Docs](https://img.shields.io/badge/Docs-Architecture-teal.svg)](docs/ARCHITECTURE.md)
[![Crucible Harness](https://img.shields.io/badge/Docs-Crucible%20Harness-firebrick.svg)](docs/CRUCIBLE.md)
[![Implementation Plan](https://img.shields.io/badge/Roadmap-Plan-orange.svg)](docs/PLAN.md)

---

## 🎯 Project Overview & Goals

🌙 NightCode **Forge** is an asynchronous developer queue and multi-agent orchestrator built specifically for software engineering workflows by combining **deterministic** steps and **local LLM-powered** agents.

⚡NightCode **Crucible** is a minimalist agent harness optimized for local LLMs.

Create your backlog and review tasks with agent assistance, and let implementation and automatic review flow overnight.

### The Problem: Single-GPU Contention & Idle Blocks
When running models locally (via Ollama, vLLM, llama-server, or local agent runtimes like Pi and OpenCode), hardware is strictly bounded by **available GPU VRAM**. On consumer or single-workstation setups, running multiple concurrent coding agents quickly results in severe VRAM thrashing, context truncation, or Out-Of-Memory (OOM) crashes. **Inference must run sequentially.**

At the same time, autonomous coding agents frequently encounter ambiguities:
* *"Should this configuration be stored in JSON or YAML?"*
* *"Is this public API change acceptable or should we maintain backward compatibility?"*

In conventional interactive tools, the agent pauses and **blocks the process** waiting for the human to answer. As a result, the GPU sits completely idle for hours—blocking all other pending work and wasting compute time.

### The Solution: Non-Blocking Context Switching
NightCodeForge eliminates idle GPU time through **automatic Git checkpointing and task context switching**:

1. **Autonomous Execution:** NightCodeForge dequeues a task and launches a specialized multi-agent pipeline on an isolated Git branch (`feature/task-{id}`).
2. **Instant Checkpoint on Human Interruption:** When any agent asks a question via `ask` (or `AskHumanQuestion`), the system:
   - Captures and serializes the agent's memory and chat history into persistent storage.
   - Executes a native Git WIP commit (`git add -A && git commit -m "WIP: ..." --no-verify`) to ensure zero untracked files are lost.
   - Terminates the background worker, immediately freeing the GPU.
3. **Queue Progression:** The single-worker GPU queue instantly picks up the next task in the backlog.
4. **Preemptive Resume:** When the developer answers the question via the web interface, the task returns to the queue with **highest priority (`llm-urgent`)**. When the GPU is freed, the branch is checked out, the previous context is restored with the human answer injected, and execution resumes seamlessly.

---

## ✨ Key Features

* 🛡️ **Strict VRAM & Hardware Protection (`WorkerCount = 1`):** Dedicated inference queue guarantees sequential execution, preventing VRAM overflow across all projects.
* 📁 **Multi-Workspace Architecture:** Manage multiple independent local repositories. Each task is tied to a workspace with its own Git working tree, base branch, and preferred model settings.
* 🔀 **Native Git CLI Context Switching:** Pure command-line interaction via `CliWrap` (strictly no `LibGit2Sharp` wrappers). Safe branch isolation, WIP commits, and squash-on-merge diffs.
* 🧩 **Extensible 3-Agent Workflow Pipeline:**
  1. **ForgeTask Review Agent:** Pre-inspects the codebase, clarifies requirements, and prompts the human proactively *before* any coding begins.
  2. **Implementation Agent:** Modifies code, runs builds (`dotnet build`), executes unit tests, and iterates.
  3. **Code Review & Fixes Agent:** Inspects diffs, runs static checks, identifies gaps or regressions, and applies automated fixes.
* 🎭 **Agent Profiles per Stage:** Customize the intelligence for each workflow step. Assign a specialized profile combining **Harness** (`Crucible`, `PiRpc`, or `Acp`), **Model Name** (e.g. `deepseek-r1:32b` for deep review vs `qwen2.5-coder:32b` for fast coding), **Thinking Effort** (None, Low, Medium, High, or custom token budget), and harness-specific launch configurations.
* 📝 **Stage-Specific Bootstrap Prompts:** Every stage has its own tailored System Persona and Bootstrap Context injection:
  - *Stage 1 (Review):* Focuses on architectural ambiguities, injecting task requirements, with strict instructions to prompt the human before coding.
  - *Stage 2 (Implementation):* Injects the approved technical plan and prior human Q&A, driving test-driven coding and compilation loops.
  - *Stage 3 (Review & Fixes):* Injects the live `git diff` against the base branch, running critical audit checklists and applying direct patches.
* 🛠️ **Model Context Protocol (MCP) & Modular Skills:**
  - **Native MCP Host:** Connect external MCP servers (e.g. SQLite, PostgreSQL, GitHub, custom CLIs) via `mcp_config.json`, leveraging Microsoft Agent Framework's native `Microsoft.Agents.Mcp` client.
  - **Modular Skills (`SKILL.md`):** Teach agents framework-specific best practices and project conventions on demand (`.agents/skills/`).
* 📜 **Native `AGENTS.md` Support:** Automatically loads repository instructions, architecture guidelines, and build/test commands from `AGENTS.md` or `.agents/AGENTS.md` and prepends them into every agent stage across all harnesses.
* 🔌 **Universal Agent Harness Abstraction:**
  - **Crucible (First-Class Native C#):** In-process via Microsoft Agent Framework & `Microsoft.Extensions.AI`.
  - **Pi JSON-RPC (First-Class):** Managed sub-process runner via stdio JSON-RPC.
  - **[Agent Client Protocol (ACP)](https://agentclientprotocol.com/) Compatible:** Plug any existing or future ACP-compliant coding agent (from Zed, JetBrains, or the open ACP Registry) without altering core code.
* ⏱️ **Fine-Grained Step Duration & Token Metrics:** Tracks exact elapsed execution time, prompt tokens, completion tokens, total tokens, and hardware target for every workflow stage.
* 📜 **Session Transcripts & Hybrid AI Diagnostics:** Automatically saves turn-by-turn markdown execution logs (`docs/transcripts/`) capturing model thoughts, tool calls, and compiler outputs. If a local model fails or gets stuck in a reasoning loop, a one-click **"Diagnose with AI"** feature evaluates the transcript using either **Cloud LLMs** (fast, zero local VRAM impact via OllamaRouter cloud models or cloud APIs) or **Local LLMs** (100% private & offline, queued sequentially) to pinpoint root causes and recommend prompt/skill fixes.
* 📁 **Markdown ForgeTask Mirroring (`docs/tasks/`):** Active non-resolved tasks are automatically mirrored to the workspace filesystem as date-prefixed Markdown files with YAML frontmatter (`docs/tasks/YYYY-MM-DD-<slug>.md`). Omitting the ID from the filename enables clean chronological sorting and avoids file renames when new files are dropped into the directory (the system simply injects the assigned `id` in-place). User edits to requirements, priority, or dependencies sync to the DB, while state and stage are protected by the orchestrator.
* 🔗 **ForgeTask Dependency Graph (DAG):** Define prerequisite dependencies between tasks with automatic cycle detection. A task stays locked until all its dependencies are completed and merged.
* 💡 **Interactive Ideation & Codebase Discovery Chat:** Ask questions about your repository in real time. The read-only discovery agent analyzes the code and proposes structured tasks with dependency links directly into the backlog.
* 🔀 **First-Class [OllamaRouter](https://github.com/jetelain/OllamaRouter) Integration:**
  - Works with native Ollama (`/api/chat`) and OpenAI-compatible (`/v1/chat/completions`) endpoints.
  - **Dual-Compute Topology (100% On-Premise):** Both **Host GPU (`Local`)** and **Dedicated LAN Server (`Remote`)** are private local workloads. ~95% of heavy dev-queue tasks route to the high-VRAM LAN server, leaving the Host GPU free to power real-time **Ideation Chat** concurrently without waiting!
  - **Optional Cloud Overflow:** Seamlessly overflow complex ideation chats or zero-VRAM AI diagnostics without queue waiting.
  - Dynamic model catalog discovery via `/api/tags`.
* 📬 **Human-in-the-Loop Inbox & Review Room:**
  - **Inbox:** Clean, consolidated dashboard of all blocked questions with quick text or multiple-choice answering.
  - **Monitoring:** Live token streaming, Git CLI logs, step duration gauges, and GPU routing telemetry via SignalR.
  - **Review Room:** Visual side-by-side Git diff viewer with one-click "Approve & Merge" or "Request Changes".

---

## 🚀 One-Command Repository Onboarding: `ncf` CLI (`dotnet tool`)

Preparing an existing repository (e.g. .NET, Node.js, Python, Rust, Go) for NightCodeForge is 100% deterministic and takes seconds with the `ncf` global tool:

```bash
# 1. Complete Environment Setup (WSL2, Docker, .NET 10 SDK, Web, and CLI):
powershell -ExecutionPolicy Bypass -File .\install.ps1

# Or build and install CLI tool locally from source in this repository:
.\install-cli.ps1

# Or publish and install Web application locally:
.\publish.ps1

# In your target repository:
ncf init        # Deterministic preparation (idempotent)
ncf audit       # Pre-flight readiness scorecard (🟢 / 🟡 / 🔴)
```

### What `ncf init` Does Deterministically
1. **`.gitignore` Merge:** Safely merges NCF-mandatory entries (`bin/`, `obj/`, `.vs/`, `TestResults/`, `*.trx`, runtime transcripts) inside managed markers without clobbering user entries.
2. **`.editorconfig` Enforcement:** Ensures `root = true`, LF line endings (`end_of_line = lf`), UTF-8 charset, and standard indent rules to completely prevent phantom diffs across environments.
3. **Shared Stack Detection:** Identifies the project stack (`dotnet`, `node`, `python`, `rust`, `go`) using the shared `CodeStackPresets` engine without drift.
4. **Agent Skills Installation:** Installs packaged skills (`ncf-backlog`, `ncf-backlog-migrator`, `ncf-project-readiness`) into `.agents/skills/` under managed blocks, discoverable by both external IDE agents and internal harnesses.
5. **Configuration Bootstrap:** Writes `.nightcodeforge/config.json` with detected build/test/format commands, base branch, and timeouts.
6. **Baseline Format Pass:** Runs the stack's code formatter (e.g. `dotnet format --no-restore`, `cargo fmt`, `ruff format .`, `gofmt -w .`) to guarantee a clean starting baseline.

### 5-Pillar Readiness Scorecard (`ncf audit`)
`ncf audit` performs a read-only check across 5 pillars, generating console feedback and `.nightcodeforge/audit-report.md`:
* **1. Git Health:** Validates committed HEAD on primary branch (`main`/`master`) with a clean working tree.
* **2. Build Determinism:** Verifies that CLI build succeeds cleanly non-interactively.
* **3. Test Fast Feedback:** Runs the fast test suite (`Category!=Acceptance&Category!=E2E`).
* **4. Isolation Readiness:** Scans for hardcoded machine paths and unmocked external network calls in unit tests.
* **5. NCF Configuration:** Validates `.nightcodeforge/config.json` and ensures installed skills match CLI versions.

> **Remediation Companion:** If the audit produces 🟡 warnings or 🔴 blockers, load the `ncf-project-readiness` skill in your AI assistant (Claude Code, Cursor, Antigravity) to guide code fixes.

#### Roadmap Commands (Future Stage 11)
The CLI architecture is designed to accommodate future commands:
* `ncf server` — Install and launch the NightCodeForge web dashboard.
* `ncf register` — Register the prepared workspace via Web API.
* `ncf import` — Bulk import `.nightcodeforge/tasks/*.md` into the database.

---

## 🏗️ Architecture at a Glance

```mermaid
flowchart TD
    subgraph UI ["Blazor Web Application"]
        Ideation["💡 Ideation Chat"]
        Backlog["📋 Backlog & DAG Graph"]
        Inbox["📬 Question Inbox"]
        Monitor["⚡ Live Telemetry"]
        Review["🔍 Review Room"]
    end

    subgraph Core ["Orchestration Engine"]
        DepEngine["DAG Dependency Engine"]
        Pipeline["Extensible Workflow Pipeline\n(Review ➔ Implement ➔ Review & Fix)"]
        HangfireUrgent["llm-urgent Queue (Priority 1)"]
        HangfireStandard["llm-inference Queue (Priority 2)"]
    end

    subgraph Router ["Inference Gateway (OllamaRouter)"]
        OllamaRouter["OllamaRouter (:11434)"]
        LocalGPU["Host GPU (Local Target)\n(Developer Workstation / Concurrent Ideation)"]
        RemoteServer["Dedicated LAN GPU Server (Remote Target)\n(High VRAM - ~95% Dev-Queue)"]
        CloudOverflow["Cloud Overflow (Optional External)"]
    end

    subgraph Workspace ["Target Codebase"]
        GitCli["Native Git CLI (CliWrap)\nBranch: feature/task-{id}"]
    end

    UI --> Core
    Ideation -.->|Concurrent Interactive Inference| OllamaRouter
    Core --> HangfireUrgent
    Core --> HangfireStandard
    HangfireUrgent -->|WorkerCount = 1| OllamaRouter
    HangfireStandard -->|WorkerCount = 1| OllamaRouter
    OllamaRouter --> LocalGPU
    OllamaRouter --> RemoteServer
    OllamaRouter -.-> CloudOverflow
    Core --> GitCli
```

---

## 🛠️ Technology Stack

| Component | Technology | Rationale |
| :--- | :--- | :--- |
| **Framework** | .NET 10 / C# | High-performance modern backend and reactive UI |
| **Web UI** | ASP.NET Core Blazor (Interactive Server) | Low-latency real-time developer dashboard |
| **Telemetry** | ASP.NET Core SignalR | Live terminal streaming and instant UI reactivity |
| **Background Queue** | Hangfire with SQLite storage | Reliable job scheduling with configurable worker limits (`WorkerCount = 1`) |
| **Git Operations** | `CliWrap` | Native CLI execution with robust stdout/stderr capture and cancellation |
| **AI Abstractions** | `Microsoft.Extensions.AI` | Unified client abstraction supporting Ollama and OpenAI-compatible endpoints |
| **Inference Gateway** | [OllamaRouter](https://github.com/jetelain/OllamaRouter) | Intelligent dynamic GPU selection (Local GPU vs Remote server vs Cloud) |
| **Database** | Entity Framework Core (SQLite WAL mode) | Zero-friction local deployment with full ACID guarantees |

---

## 📚 Documentation Links

* 📐 **Forge System Architecture:** Detailed orchestrator specifications, dev-queue, Git context switching, sandboxing, and data schema in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
* 🔥 **Crucible Harness Specification:** In-process native C# agent harness, canonical tools, lenient dispatcher, and token minimization engine in [docs/CRUCIBLE.md](docs/CRUCIBLE.md).
* 🗺️ **Implementation Roadmap:** Phased breakdown from solution scaffolding to full end-to-end testing in [docs/PLAN.md](docs/PLAN.md).
* ⚡ **Local LLM Optimizations:** High-value strategies for KV-cache acceleration, reasoning trace pruning, loop breaking, and CPU diagnostic offloading in [docs/LOCAL_LLM_OPTIMIZATIONS.md](docs/LOCAL_LLM_OPTIMIZATIONS.md).

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
