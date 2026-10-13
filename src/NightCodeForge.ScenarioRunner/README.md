# NightCodeForge Scenario Runner

A specialized console application designed to test, validate, and demonstrate autonomous multi-agent workflows, quality gates, and backend infrastructure across realistic end-to-end scenarios.

All environments are **synthetic and ephemeral** by default (clean teardown on completion), with a `--keep` / `-k` flag to retain SQLite databases and Git repositories for inspection.

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

---

## Command-Line Arguments

| Flag | Short | Default | Description |
|---|---|---|---|
| `--scenario <id>` | `-s` | `0` | Run a specific scenario (1–8) |
| `--all` | `-a` | `false` | Run all 8 scenarios sequentially |
| `--keep` | `-k` | `false` | Retain temporary Git repository and SQLite DB on disk |
| `--mock-llm` | `-m` | `false` | Use synthetic deterministic LLM instead of live Ollama |
| `--endpoint <url>` | `-e` | `http://localhost:11434/` | Ollama Router HTTP endpoint |
| `--model <name>` | | `Qwen3.8-27B:latest` | Target Ollama model identifier |
| `--timeout <sec>` | `-t` | `180` | Per-scenario execution timeout in seconds |
| `--interactive` | `-i` | `false` | Force interactive console selection |
| `--help` | `-h` | | Display CLI options and usage |

---

## Test Scenarios Overview

| # | Scenario Name | Description | Key Validations |
|---|---|---|---|
| **1** | **Health & Infrastructure** | Validates SQLite WAL mode, Git CLI, and Ollama connectivity | Preloads model into VRAM via `/api/generate` |
| **2** | **Crucible Agent Reasoning** | Executes an agent turn with tool execution (`read`, `edit`, `bash`) | Streaming telemetry, reasoning loop, token metrics |
| **3** | **Human Interruption** | Universal interruption via `ask` or prompt injection | WIP checkpoints, worker release, and `llm-urgent` resumption |
| **4** | **Dependency DAG Engine** | Parent/child prerequisite blocking and cascade unblocking | Blocks premature execution, squash-merges, unblocks child |
| **5** | **Deterministic Quality Gates** | Zero-token CPU build verification & regression interception | Intercepts syntax errors, routes backward loopback without GPU |
| **6** | **End-to-End Workflow** | Full 6-stage autonomous lifecycle with auto-squash-merge | Review -> Pre-impl -> Impl -> Post-impl -> Code Review -> Verify |
| **7** | **Crash Resilience & Recovery** | Startup recovery for dirty workspaces and crashed jobs | Purges `.git/index.lock`, creates `WIP-RECOVERY` commits |
| **8** | **Workspace Concurrency** | Per-workspace mutual exclusion & non-blocking multi-workspace | Workspace lock serialization, cross-workspace parallel locks |

---

## Inspecting Kept Artifacts (`--keep`)

When `--keep` is passed, the runner leaves the temporary directory intact and displays ready-to-run inspection commands:

```powershell
# Inspect Git repository history and branches:
git -C "<path-to-repo>" status
git -C "<path-to-repo>" log --all --graph --oneline
git -C "<path-to-repo>" branch -a

# Inspect SQLite database state:
sqlite3 "<path-to-db>" "SELECT Id, Title, Status, ReadyForExecution FROM Issues;"
sqlite3 "<path-to-db>" "SELECT Id, StageId, Status, DurationMs FROM StageExecutionRecords;"
```
