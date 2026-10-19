# NightCodeForge Scenario Runner

> **Assembly:** `NightCodeForge.ScenarioRunner` | **Layer:** CLI / Verification  
> **Role:** Console application orchestrating synthetic end-to-end multi-agent workflow scenarios, quality gates, and infrastructure tests.

---

A specialized console application designed to test, validate, and demonstrate autonomous multi-agent workflows, quality gates, and backend infrastructure across realistic end-to-end scenarios.

All environments are **synthetic and ephemeral** by default (clean teardown on completion), with a `--keep` / `-k` flag to retain SQLite databases and Git repositories for inspection.

> [!NOTE]
> **Workspace Isolation & Worktree Architecture:**
> All synthetic workspaces are assigned IDs strictly **larger than 1,000,000** (e.g. `1000001`, `1000002`) to prevent any path collisions with real NightCodeForge instances and WSL ext4 environments (`~/.nightcodeforge/wks/{workspaceId}`).
> All tasks run in dedicated native Git worktrees (`task-{taskId}`) enabling intra-workspace task parallelism and worktree-level crash recovery.

---

## Quick Start

### 1. Interactive Menu
Launch without arguments to use the interactive numbered console menu:
```powershell
dotnet run --project src/NightCodeForge.ScenarioRunner
```

### 2. Run All Scenarios (Offline / Fast Regression)
Uses an in-memory mock LLM client to validate pipelines, git operations, DAG blocking, and concurrency in seconds (~16s total):
```powershell
dotnet run --project src/NightCodeForge.ScenarioRunner -- --mock-llm --all
```

### 3. Run with Live Ollama Router (`Qwen3.8-27B:latest`)
Targets the Ollama Router running at `http://localhost:11434/` with real-time streaming telemetry and tool calls:
```powershell
# Scenario #1: Infrastructure Health & Model JIT warm-up
dotnet run --project src/NightCodeForge.ScenarioRunner -- -s 1

# Scenario #2: Crucible Agent live reasoning & tool execution
dotnet run --project src/NightCodeForge.ScenarioRunner -- -s 2

# Scenario #2 with persistent artifacts kept for post-run inspection:
dotnet run --project src/NightCodeForge.ScenarioRunner -- -s 2 --keep
```

### 4. Run in Container Mode (Docker Sandbox)
Executes scenarios inside an isolated Docker container sandbox (defaults to `ubuntu:24.04`) with bind-mounted workspace, toolchains, and package caches:
```powershell
# Run all scenarios in container mode with mock LLM:
dotnet run --project src/NightCodeForge.ScenarioRunner -- --mock-llm --all --container

# Run specific scenario in container mode with a custom image:
dotnet run --project src/NightCodeForge.ScenarioRunner -- -s 1 --mock-llm -c --image ubuntu:24.04
```

---

## Command-Line Arguments

| Flag | Short | Default | Description |
|---|---|---|---|
| `--scenario <id>` | `-s` | | Run a specific scenario (1–10) or scenario name filter |
| `--all` | `-a` | `false` | Run all scenarios sequentially |
| `--keep` | `-k` | `false` | Retain temporary Git repository and SQLite DB on disk |
| `--container` | `-c` | `false` | Run scenarios in Container mode (Docker sandbox) instead of BareHost |
| `--image <name>` | | `ubuntu:24.04` | Container image for container mode |
| `--mock-llm` | | `false` | Use synthetic deterministic LLM instead of live Ollama (`--offline`) |
| `--endpoint <url>` | `-e` | `http://localhost:11434` | Ollama Router HTTP endpoint |
| `--model <name>` | `-m` | `Qwen3.8-27B:latest` | Target Ollama model identifier |
| `--timeout <min>` | `-t` | `15` | Request timeout in minutes |
| `--interactive` | `-i` | `true` | Force interactive console selection menu |
| `--help` | `-h` | | Display CLI options and usage |

---

## Test Scenarios Overview

| # | Scenario Name | Description | Key Validations |
|---|---|---|---|
| **1** | **Health & Infrastructure** | Validates SQLite WAL mode, Git CLI, Ollama connectivity, and execution environment resolution | Preloads model into VRAM via `/api/generate`; validates BareHost or ContainerSandbox resolution |
| **2** | **Crucible Agent Reasoning** | Executes an agent turn with tool execution (`read`, `edit`, `bash`) | Streaming telemetry, reasoning loop, token metrics |
| **3** | **Human Interruption** | Universal interruption via `ask` or prompt injection | WIP checkpoints, worker release, and `llm-urgent` resumption |
| **4** | **Dependency DAG Engine** | Parent/child prerequisite blocking and cascade unblocking | Blocks premature execution, squash-merges, unblocks child |
| **5** | **Deterministic Quality Gates** | Zero-token CPU build verification & regression interception | Intercepts syntax errors, routes backward loopback without GPU |
| **6** | **End-to-End Workflow** | Full 6-stage autonomous lifecycle with auto-squash-merge | Review -> Pre-impl -> Impl -> Post-impl -> Code Review -> Verify |
| **7** | **Crash Resilience & Recovery** | Startup recovery for active worktrees and crashed tasks | Purges stale locks across base repos and worktree gitdir, creates `WIP-RECOVERY` commits in task worktrees, re-enqueues orphaned tasks |
| **8** | **Workspace Concurrency & Lock Isolation** | Intra-workspace task concurrency & cross-workspace isolation | Per-task worktree locks (`AcquireTaskLockAsync`), non-blocking parallel tasks in same workspace, cross-workspace isolation, emergency release |
| **9** | **Untrusted Triage Execution** | Sandboxed evaluation of untrusted incoming tasks and code | Restricted execution tier, secret scrubbing, network policy |
| **10** | **Git Mirror Sync & Merge** | Git mirror push/pull and automatic squash-merge lifecycle | Isolated working clones (`~/.nightcodeforge/wks/{id}`), mirror sync |

---

## Inspecting Kept Artifacts (`--keep`)

When `--keep` is passed, the runner leaves the temporary directory intact and displays ready-to-run inspection commands:

```powershell
# Inspect Git repository history and branches:
git -C "<path-to-repo>" status
git -C "<path-to-repo>" log --all --graph --oneline
git -C "<path-to-repo>" branch -a

# Inspect SQLite database state:
sqlite3 "<path-to-db>" "SELECT Id, Title, Status, ReadyForExecution FROM Tasks;"
sqlite3 "<path-to-db>" "SELECT Id, StageId, Status, DurationMs FROM StageExecutionRecords;"
```
