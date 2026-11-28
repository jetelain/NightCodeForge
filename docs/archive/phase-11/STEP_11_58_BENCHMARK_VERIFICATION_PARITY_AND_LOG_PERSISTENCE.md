# ðŸ›¡ï¸ Step 11.58: Benchmark Multi-Stack Verification Parity, Solution Discovery & Verification Log Persistence

> **Parent Plan:** [docs/archive/11_EXTEND_FEATURES.md](../11_EXTEND_FEATURES.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets`  
> **Dependencies:** [Step 11.55](STEP_11_55_BENCHMARK_VERIFICATION_ENVIRONMENT_AND_DOTNET_SDK_RESILIENCE.md), `BenchmarkVerifier`, `BenchmarkTestCommandResolver`, `CrucibleBenchmarkRunner`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the `v1-smoke-Flint-latest-20261124-123321` benchmark run (`18 / 28` completed instances at inspection time: 6 passed, 8 failed, 4 timed out) revealed four benchmark verification bugs causing direct false negatives on instances where the model produced valid fixes, as well as missing verification log persistence:

1. **Wrong Subdirectory `.sln` Selected (`spectreconsole__spectre-console-1458`):** When no `.sln`/`.csproj` exists at the repository root, `BenchmarkTestCommandResolver.DiscoverDotNetTargets` selects a single solution ordered by `(IsLowPriorityTarget, CountPathSegments, OrdinalIgnoreCase)`. Because `docs` is not deprioritized alongside `sample`/`benchmark`/`example`, `docs/Docs.sln` sorts alphabetically ahead of `src/Spectre.Console.sln`. Verification builds and tests `docs/Docs.sln`, which does not include `Spectre.Console.Tests`, resulting in `F2P: 0/1` despite a valid patch in `AnsiConsoleExtensions.Input.cs`.
2. **`ExitCode` Verification + WSL Bind-Mount Socket Failure (`axios__axios-5892`):** The dataset configures `axios__axios-5892` with `test_result_format: ExitCode` and `custom_test_command: npm run test:mocha`. During execution, `152` unit tests pass, while 1 unrelated pre-existing test (`supports http with nodejs > should support sockets`) fails with `Uncaught Error: listen ENOTSUP: operation not supported on socket ./test.sock` because `/workspace` is bind-mounted from Windows/WSL. Relying solely on process exit code turns an unrelated environment limitation into `0/3` F2P and `0/30` P2P failures.
3. **Python 3.12 Runtime & Dependency Mismatch (`pallets__flask-5014`, `psf__requests-1921`):**
   - `pallets__flask-5014` fails test verification with exit code `4` and zero parsed test results: the generic container setup installs latest `pytest` (8/9) and unpinned `Werkzeug`, triggering `AttributeError: module '_pytest.monkeypatch' has no attribute 'notset'` in `tests/conftest.py` before JUnit XML is written.
   - `psf__requests-1921` fails all `113` assertions (`0/6` F2P, `0/107` P2P) because the 2014 `requests` codebase imports `collections.MutableMapping` (removed in Python 3.10+) inside the default Python 3.12 container, and tests reference `httpbin.org`.
4. **Discarded `VerificationLog` Diagnostics:** `BenchmarkVerifier` captures `VerificationLog` (combined verification stdout/stderr), but `CrucibleBenchmarkRunner` never persists it to disk or links it in `results.json`, making root-cause diagnosis of verification failures require manual container reproduction.

---

## 2. Required Behaviors & Constraints

### 2.1 Test-Aware `.NET` Solution Discovery
- Subdirectory `.sln`/`.slnx` discovery MUST deprioritize non-application/non-test directories (`docs`, `doc`, `resources`, `scripts`, `tools`) alongside `sample`, `benchmark`, and `example`.
- When multiple candidate solutions exist outside the repository root, solution selection MUST prefer solutions that actually reference test projects (or match the repository's primary source/test layout) rather than relying purely on alphabetical ordering across unrelated subtrees such as `docs/Docs.sln`.

### 2.2 Structured Mocha Verification & Bind-Mount Socket Resilience
- Mocha-based Node/JavaScript verification instances (such as `axios__axios-5892` in `v1-smoke`) MUST NOT rely solely on `TestResultFormat.ExitCode` across the entire `npm run test:mocha` suite when individual tests fail due to sandbox bind-mount restrictions (`ENOTSUP` on Unix domain sockets).
- Verification MUST emit and parse structured per-test results (e.g., JUnit XML via a Mocha JUnit reporter) or scope execution away from unsupported Unix-socket tests so `TestAssertionEvaluator` evaluates the exact `fail_to_pass` and `pass_to_pass` assertions accurately without false negatives.

### 2.3 Python Verification Runtime & Dependency Compatibility
- Python benchmark verification MUST execute against a compatible Python runtime and pinned dependency set matching the target repository's requirementsâ€”whether via per-instance container images (`custom_container_image`), installing repository-pinned requirement files prior to or instead of unpinned `pytest`, or curating Python 3.12-compatible dataset instances.
- Verification of valid Python patches MUST NOT fail during test collection or module import due to unpinned latest `pytest`/`Werkzeug` incompatibilities (exit code `4`) or Python 3.10+ stdlib removals (`collections.MutableMapping`).

### 2.4 Persist `VerificationLog` Diagnostics
- `CrucibleBenchmarkRunner` MUST persist `BenchmarkVerificationResult.VerificationLog` to disk under the benchmark output directory (at minimum whenever verification fails or errors, or per evaluated instance) alongside `transcripts/`.
- `BenchmarkInstanceResult` in `results.json` SHOULD expose the relative or absolute path to the persisted verification log file so verification failures (e.g., wrong solution target, pytest collection errors, `ENOTSUP`) can be inspected directly without re-running containers.

---

## 3. Definition of Done (DoD) Checklist

- [x] Update `.NET` subdirectory solution discovery to deprioritize `docs`/`doc`/`resources`/`scripts`/`tools` and prefer solutions containing test projects, with unit tests covering `docs/Docs.sln` vs. `src/<Repo>.sln`.
- [x] Replace `ExitCode`-only verification on `axios__axios-5892` (and equivalent Mocha suites) with structured per-test JUnit XML reporting or bind-mount-safe test scoping, verified by unit tests.
- [x] Ensure Python benchmark verification uses compatible runtimes and pinned repository dependencies (via per-instance `custom_container_image`, pinned dependency provisioning, or Python 3.12-compatible instance curation) so `pallets__flask-5014` and `psf__requests-1921` style setups collect and execute tests cleanly.
- [x] Persist verification command stdout/stderr (`VerificationLog`) to disk under the benchmark output directory and reference the log path from `BenchmarkInstanceResult`, with unit tests.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
