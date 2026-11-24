# 🛡️ Step 11.55: Benchmark Verification Environment Parity, .NET SDK Resilience & Vitest Matching

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Proposed  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `NightCodeForge.Infrastructure`  
> **Dependencies:** Crucible Benchmark Engine (`v1-smoke` dataset & `BenchmarkVerifier`)  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the `v1-smoke-Flint-latest-20261123-214242` benchmark run (28 instances) revealed multiple benchmark harness failures causing false negatives and provisioning errors across C#, Python, JavaScript, TypeScript, and Rust tasks:

1. **Windows Long Path Checkout Failure (`facebook__docusaurus-9897`):** `BenchmarkWorkspaceManager.ProvisionAsync` failed at `git checkout --detach FETCH_HEAD` with exit code `128` (`Filename too long`) because Windows MAX_PATH (260 chars) was exceeded without `core.longpaths true`.
2. **Missing Verification Dependencies & Wiped Environments (`pallets__flask-5014`, `psf__requests-1921`, `ncf-cr-py-001`, `axios__axios-5892`, `vuejs__core-11915`):**
   - Dataset entries leave `custom_container_image` empty, running all languages in the default container (Python 3.12 externally-managed without `pytest`, Node 18 without `node_modules`, `.NET 10` only).
   - `BenchmarkVerifier.RunImplementationTestsAsync` runs `ResetToBaseAsync` (`git clean -fd`, deleting `node_modules` / `.venv`) and spins up a fresh verification container (losing `/tmp/venv` / `/tmp/npm-global`) before executing `pytest`, `npm run test:mocha`, or `npx vitest run` with no dependency installation step, producing exit code `127` (`pytest: command not found`) or missing module failures.
   - Rust tasks (`burntsushi__ripgrep-2576`, `ncf-cr-rust-001`) lacked `$HOME/.cargo/bin` on the default container `PATH`, forcing the agent to prepend `export PATH="$HOME/.cargo/bin:$PATH"` on every invocation.
3. **`.NET` `global.json` SDK Pinning (`ncf-cr-csharp-003`, `spectreconsole__spectre-console-1338`, `autofac__autofac-1424`):** `BenchmarkVerifier` sets `DOTNET_ROLL_FORWARD=LatestMajor`, which only governs runtime roll-forward—not `global.json` SDK resolution. Repositories pinning older SDKs (e.g., Serilog `7.0.102` with `rollForward: "latestFeature"`) fail with exit code `155` inside `.NET 10` containers.
4. **Subdirectory Solutions & Repo-Specific Test Configs (`spectreconsole__spectre-console-1458`, `vuejs__core-11915`):** Root-level `dotnet test` fails with exit code `1` when the `.sln` or test `.csproj` lives in a subdirectory (e.g., `src/Spectre.Console.sln`), and bare `npx vitest run` fails on monorepos requiring an explicit config (e.g., `-c vitest.unit.config.ts`).
5. **Vitest JUnit Test Identifier Mismatch (`vuejs__core-11915`):** Dataset assertions use Vitest's ` > ` separator (`packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > simple div`), while `JUnitXmlTestResultParser` joins `classname` and `name` with `.` and `TestAssertionEvaluator.NormalizeTestIdentifier` does not normalize ` > `, causing `0/45` test assertion matches.

---

## 2. Required Behaviors & Constraints

### 2.1 Windows Long Path Support in Workspace Provisioning
- `BenchmarkWorkspaceManager.ProvisionAsync` MUST configure `git config core.longpaths true` in the newly initialized workspace before executing `git checkout --detach FETCH_HEAD`.
- Deep repository paths (such as `facebook__docusaurus-9897` i18n test fixtures) MUST check out cleanly on Windows without failing with exit code `128`.

### 2.2 Multi-Stack Verification Dependency Parity
- Verification execution MUST support per-stack container images or deterministic pre-test dependency setup hooks so test suites run in an environment matching their language stack:
  - **Python:** Ensure `pytest` and the repository's editable package/dependencies are provisioned in an isolated virtual environment (compatible with PEP 668 externally-managed Python 3.12+ environments) prior to running `pytest`, rather than failing with exit code `127`.
  - **JavaScript / TypeScript:** Ensure required `node_modules` dependencies are installed or restored prior to running Mocha, Vitest, or Jest after workspace reset (`git clean -fd`).
  - **Rust:** Ensure `$HOME/.cargo/bin` (and active toolchain binaries) are present on the default container `PATH` for both agent execution and verification containers without requiring manual `export PATH=...` workarounds.
- Any dependency installation or pre-test setup executed during verification MUST NOT alter the extracted `model_patch` or leak agent-modified source/build artifacts into clean verification runs.

### 2.3 `.NET` `global.json` SDK Roll-Forward Resilience
- Both benchmark agent execution and `BenchmarkVerifier` test execution for C# repositories MUST handle `global.json` SDK version constraints so repositories pinning older .NET SDK versions build and test reliably on `.NET 10` containers without exiting with code `155`.
- Any `global.json` mitigation (such as temporarily bypassing, overriding, or scoping SDK resolution during execution/verification) MUST leave the repository clean so `global.json` edits or deletions never pollute extracted `git diff` patches (`model_patch`) or evaluation `TestPatch` application.

### 2.4 Subdirectory Solution / Project & Test Config Resolution
- When no `.sln`, `.slnx`, or `.csproj` exists at the repository root and no explicit target is specified, default C# verification command resolution MUST locate the repository's solution or test project(s) in subdirectories (e.g., `src/Spectre.Console.sln` or `test/**/*.csproj`) rather than running bare `dotnet build` / `dotnet test` at `/workspace` root.
- Dataset and verifier test command resolution MUST preserve/apply repository-specific test runner configurations (such as `-c vitest.unit.config.ts` for Vue) when required to run unit suites cleanly.

### 2.5 Vitest JUnit Identifier Normalization
- `TestAssertionEvaluator.NormalizeTestIdentifier` in `src/NightCodeForge.Benchmark.Cli/Verification/TestResultParsers.cs` MUST normalize Vitest's ` > ` hierarchy separator alongside `.py::`, `::`, and `/` so dataset assertions using `path > suite > subsuite > test` match parsed JUnit XML `classname.name` outcomes deterministically.
- Existing normalization behavior for xUnit/NUnit, pytest, Cargo, and Jest test names MUST remain intact.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Configure `core.longpaths true` before `git checkout --detach FETCH_HEAD` in `BenchmarkWorkspaceManager.ProvisionAsync`, with unit test verification.
- [ ] Support deterministic per-stack dependency provisioning / container parity across Python, Node (JS/TS), and Rust (including `$HOME/.cargo/bin` on `PATH`) without contaminating `model_patch`.
- [ ] Add `.NET` `global.json` SDK roll-forward resilience for C# execution and verification without polluting `git diff` patches, with unit tests.
- [ ] Support subdirectory `.sln`/test `.csproj` discovery in C# verification command resolution and repo-specific test config flags (e.g. Vitest unit config), with unit tests.
- [ ] Normalize ` > ` in `TestAssertionEvaluator.NormalizeTestIdentifier` and add unit tests covering Vitest ` > ` matching against JUnit XML outcomes.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
