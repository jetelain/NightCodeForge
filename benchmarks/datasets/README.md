# NightCodeForge Crucible Benchmark Datasets

This directory stores **pinned selection manifests** and **crafted NightCodeForge-native review scenarios** under version control:

- `v1-smoke.manifest.json` — Fast regression suite (28 instances: ~54% C#, ~21% TS/JS, ~14% Python, ~11% Rust) across Implementation, Code Review, and ForgeTask Review.
- `v1-full.manifest.json` — Overnight model selection suite (90 instances: ~53% C#, ~20% TS/JS, ~16% Python, ~11% Rust) across Implementation, Code Review, and ForgeTask Review.
- `v1-code-review.jsonl` — 18 crafted Code Review scenarios (`Approve`, `MinorFix`, `RequestChanges`) testing Tier-1 surgical fixes, false-positive rates, and Tier-2 rejections with regression-test enforcement.
- `v1-task-review.jsonl` — 15 crafted ForgeTask Review scenarios (`Ready`, `Ready` + difficulty escalation, `NeedsClarification`) testing symbol grounding, YAML frontmatter preservation, difficulty escalation, and `ask` tool usage.

## Importing External Implementation Datasets

Large third-party Implementation patches and test suites are **not** committed to git. Materialize them locally under `~/.nightcodeforge/benchmarks/datasets/` using `ncf-bench dataset import`:

```bash
# C# / .NET (SWE-Sharp-Bench)
ncf-bench dataset import \
  --source swe-sharp-bench \
  --input /path/to/swe-sharp-bench.jsonl \
  --manifest benchmarks/datasets/v1-smoke.manifest.json \
  --out ~/.nightcodeforge/benchmarks/datasets/v1-smoke-csharp.jsonl

# TypeScript / JavaScript / Rust (SWE-bench Multilingual — pinned in v1-smoke / v1-full)
ncf-bench dataset import \
  --source swebench-multilingual \
  --input /path/to/swebench-multilingual.jsonl \
  --manifest benchmarks/datasets/v1-smoke.manifest.json \
  --out ~/.nightcodeforge/benchmarks/datasets/v1-smoke-multilingual.jsonl

# Multi-SWE-bench exports (use with manifests targeting "source": "multi-swe-bench" or omitting "source")
ncf-bench dataset import \
  --source multi-swe-bench \
  --input /path/to/multi-swe-bench.jsonl \
  --manifest /path/to/custom.manifest.json \
  --out ~/.nightcodeforge/benchmarks/datasets/custom-multiswe.jsonl

# Python (SWE-bench Verified)
ncf-bench dataset import \
  --source swebench-verified \
  --input /path/to/swebench-verified.jsonl \
  --manifest benchmarks/datasets/v1-smoke.manifest.json \
  --out ~/.nightcodeforge/benchmarks/datasets/v1-smoke-python.jsonl
```

## Pre-Flight Dataset Validation

Verify gold patches, candidate diffs, and task markdown frontmatter inside `NightCodeForgeContainerExecutor` before running model sweeps:

```bash
ncf-bench dataset validate --dataset ~/.nightcodeforge/benchmarks/datasets/v1-smoke-csharp.jsonl
ncf-bench dataset validate --dataset benchmarks/datasets/v1-code-review.jsonl --no-verify
ncf-bench dataset validate --dataset benchmarks/datasets/v1-task-review.jsonl
```

### Resumable Golden Patch Revalidation Script (`benchmarks/validate-golden.ps1`)

After changing NightCodeForge container management, verification wrappers, or test scorers, use `benchmarks/validate-golden.ps1` to revalidate the full suite (defaults to `v1-full`) with once-upfront compilation, incremental `summary.json`/`summary.md`/`validation.log` persistence, and `-Resume` support:

```powershell
# Revalidate the full v1-full suite (90 instances) with container verification
.\benchmarks\validate-golden.ps1 -CleanContainers

# Revalidate the fast v1-smoke suite (28 instances)
.\benchmarks\validate-golden.ps1 -Suite v1-smoke -CleanContainers

# Scope validation to specific stacks or categories
.\benchmarks\validate-golden.ps1 -Language csharp -CleanContainers
.\benchmarks\validate-golden.ps1 -Language python,rust -CleanContainers
.\benchmarks\validate-golden.ps1 -Category implementation -CleanContainers

# Fast patch-application and frontmatter-only check (no containers)
.\benchmarks\validate-golden.ps1 -Suite v1-full -NoVerify

# Resume an interrupted or partially failing validation (skips already-passed instances)
.\benchmarks\validate-golden.ps1 -Resume "$env:USERPROFILE\.nightcodeforge\benchmarks\validations\v1-full-<timestamp>" -CleanContainers
```

## Running Benchmarks with OllamaRouter

All commands below assume `ollama-router` is running and reachable at its default endpoint (`http://localhost:11434`; omit `--endpoint` to use this default, or pass `--endpoint <url>` to override).

If `ncf-bench` is not installed globally, replace `ncf-bench` with:

```bash
dotnet run --project src/NightCodeForge.Benchmark.Cli -- <command and flags>
```

### 1. Single Model Run

```bash
ncf-bench run \
  --dataset benchmarks/datasets/v1-code-review.jsonl \
  --provider ollama-router \
  --endpoint http://localhost:11434 \
  --models qwen3-coder:30b \
  --out benchmark-results/v1-cr-qwen3
```

### 2. Multi-Model Sweep (Comma-Separated `--models`)

Pass multiple model names in `--models` alongside optional `--judge-model`, `--filter`, `--limit`, `--concurrency`, and `--resume` flags:

```bash
ncf-bench run \
  --dataset benchmarks/datasets/v1-code-review.jsonl \
  --provider ollama-router \
  --endpoint http://localhost:11434 \
  --models qwen3-coder:30b,qwen2.5-coder:32b,deepseek-coder-v2:16b \
  --judge-model qwen3:32b \
  --judge-provider ollama-router \
  --judge-endpoint http://localhost:11434 \
  --concurrency 1 \
  --out benchmark-results/v1-cr-sweep

# Resume an interrupted run without re-running completed (candidate, instance) pairs:
ncf-bench run \
  --dataset benchmarks/datasets/v1-code-review.jsonl \
  --provider ollama-router \
  --endpoint http://localhost:11434 \
  --models qwen3-coder:30b,qwen2.5-coder:32b,deepseek-coder-v2:16b \
  --resume benchmark-results/v1-cr-sweep

# Subset quick check by instance ID or limit:
ncf-bench run \
  --dataset benchmarks/datasets/v1-task-review.jsonl \
  --provider ollama-router \
  --endpoint http://localhost:11434 \
  --models qwen3-coder:30b \
  --filter ncf-tr-csharp-001,ncf-tr-csharp-003 \
  --limit 2
```

### 3. Multi-Candidate Sweep with `--config candidates.json`

Use `--config` instead of `--models` when comparing models with distinct `thinkingEffort`, `contextWindowTokens`, `routingTarget` (`auto` | `local` | `remote`), or `enableStackSkills` settings:

```json
{
  "candidates": [
    {
      "candidateId": "qwen3-coder-30b-skills",
      "displayName": "Qwen3 Coder 30B (Skills On)",
      "modelName": "qwen3-coder:30b",
      "thinkingEffort": "medium",
      "contextWindowTokens": 65536,
      "routingTarget": "auto",
      "enableStackSkills": true
    },
    {
      "candidateId": "qwen3-coder-30b-noskills",
      "displayName": "Qwen3 Coder 30B (Skills Off)",
      "modelName": "qwen3-coder:30b",
      "thinkingEffort": "medium",
      "contextWindowTokens": 65536,
      "routingTarget": "remote",
      "enableStackSkills": false
    },
    {
      "candidateId": "deepseek-v2-16b-local",
      "displayName": "DeepSeek Coder V2 16B (Local)",
      "modelName": "deepseek-coder-v2:16b",
      "thinkingEffort": "low",
      "contextWindowTokens": 32768,
      "routingTarget": "local",
      "enableStackSkills": true
    }
  ]
}
```

Launch the sweep using `--config`:

```bash
ncf-bench run \
  --dataset ~/.nightcodeforge/benchmarks/datasets/v1-smoke-csharp.jsonl \
  --provider ollama-router \
  --endpoint http://localhost:11434 \
  --config candidates.json \
  --out benchmark-results/v1-smoke-csharp
```

### 4. Comparing Benchmark Reports

```bash
# Single run summary
ncf-bench report benchmark-results/v1-cr-sweep

# Cross-run / cross-branch regression comparison against a baseline
ncf-bench report \
  benchmark-results/v1-cr-baseline \
  benchmark-results/v1-cr-candidate \
  --baseline benchmark-results/v1-cr-baseline \
  --format markdown
```
