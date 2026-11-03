# 🛡️ Step 11.43A: `Pmad.AI` 0.0.2 Upgrade & `ReadOnlyCommandValidator` Guardrail Migration

> **Bug Fix, Security Hardening & Library Consolidation Step (Read-Only Shell Guardrails)**  
> *Parent Plan:* [Bug Fixes & Resiliency Roadmap](README.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Proposed  
> *Target Projects:* [`src/NightCodeForge.Agents`](../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)  
> *Related Steps:* [Step 11.43B: `Pmad.AI` `LenientToolDispatcher` & Delegating Chat Client Migration](STEP_11_43B_PMAD_AI_LENIENT_TOOL_DISPATCHER_AND_DELEGATING_CHAT_CLIENT_MIGRATION.md), [Step 11.38C: Shared Agent Session & Toolset Factorization](STEP_11_38C_CRUCIBLE_AND_IDEATION_SHARED_SESSION_AND_TOOLSET_FACTORIZATION.md)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Motivation

`NightCodeForge.Agents` currently maintains an in-repo static [`ReadOnlyCommandValidator`](../../src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs) (~382 lines) used to enforce read-only `bash` execution during exploratory stages (`task-review`, `ideation`, and read-only review/diagnostic contexts).

Compared to the hardened [`Pmad.AI.Guardrails.ReadOnlyCommandValidator`](file:///C:/Users/Julien/source/repos/jetelain/PmadAI/src/Pmad.AI/Guardrails/ReadOnlyCommandValidator.cs) (~1,280 lines) introduced in `Pmad.AI` `0.0.2`, the in-repo validator has several security and maintenance gaps:

| # | Gap in Current In-Repo `ReadOnlyCommandValidator` | Hardened `Pmad.AI` 0.0.2 `ReadOnlyCommandValidator` Behavior |
| :--- | :--- | :--- |
| **1** | **Incomplete Shell Escape-Hatch Detection** | Blocks process substitution (`<(...)`, `>(...)`), environment/parameter expansion (`$VAR`, `${VAR}`), brace expansion, input redirection (`<`), heredocs (`<<`), and multiline newline/control-character injection outside quotes, while safely allowing read-only stderr redirection (`2>&1`, `2>/dev/null`). |
| **2** | **Permissive `find`, `rg`, and `sed` Flags** | Enforces per-command rule validators blocking arbitrary execution and file writes via `find -exec`/`-execdir`/`-ok`/`-delete`/`-fprint`, `rg --pre`/`--pre-glob`/`-z`, and `sed -i`/`--in-place` or `w`/`W`/`e` script directives. |
| **3** | **Incomplete `git` Flag & Subcommand Inspection** | Blocks global `git` execution/path overrides (`-c`, `--config-env`, `--exec-path`, `--git-dir`, `--work-tree`), blocks file-writing diff/log flags (`--output`, `--ext-diff`, `--textconv`), and restricts `git branch` and `git tag` to read-only listing/query flags so an agent cannot create, rename, or delete branches/tags in read-only mode. |
| **4** | **Code Duplication & Translator Integration** | Maintaining a forked validator in `NightCodeForge.Agents` duplicates `Pmad.AI` guardrails and prevents direct plug-in into `Pmad.AI.Tools.LenientToolDispatcherOptions.CommandValidator` ([Step 11.43B](STEP_11_43B_PMAD_AI_LENIENT_TOOL_DISPATCHER_AND_DELEGATING_CHAT_CLIENT_MIGRATION.md)). |

---

## 2. Detailed Requirements & Specifications

### 2.1 Package Upgrade (`Pmad.AI` `0.0.2`)

1. Update the `Pmad.AI` NuGet package reference in [`src/NightCodeForge.Agents/NightCodeForge.Agents.csproj`](../../src/NightCodeForge.Agents/NightCodeForge.Agents.csproj) from `0.0.1` to `0.0.2`.

### 2.2 NightCodeForge `ReadOnlyCommandValidator` Configuration

1. **Clean-Break Replacement:**
   - Remove the custom regex/token parsing implementation in [`src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs`](../../src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs).
   - Provide a shared, pre-configured `Pmad.AI.Guardrails.ReadOnlyCommandValidator` instance (or factory/wrapper in `NightCodeForge.Agents.Tools`) configured via `ReadOnlyCommandValidatorOptions`.

2. **Allowed Read-Only Binaries (`AllowedCommands`):**
   - Retain all default `Pmad.AI` allowed commands (`ls`, `dir`, `cat`, `head`, `tail`, `wc`, `grep`, `egrep`, `fgrep`, `rg`, `find`, `sed`) and built-in `CommandRules` (`git`, `find`, `rg`, `sed`).
   - Register the additional read-only inspection utilities permitted in NightCodeForge workspaces and stage directives:
     - `fd`, `tree`, `pwd`, `echo`, `sort`, `uniq`, `cut`, `tr`, `diff`, `stat`, `file`, `nl`, `less`, `more`, `which`, `whereis`, `awk`.

3. **Custom `csnav` Command Rule (`CommandRules["csnav"]`):**
   - Register a `CommandValidationRule` for `csnav` (the C# Roslyn navigation CLI):
     - **Allowed subcommands (`AllowedSubcommands`):** `search`, `refs`, `outline`, `inspect`, `hierarchy`, `deps`, `decompile`, `stats`.
     - **Export Flag Guardrail (`Validator`):** Reject `--export` (and `--export=...`) across `csnav` subcommands (specifically `csnav deps --export <path>`), because `--export` writes a dependency graph file to disk.
     - Reject any unknown or missing `csnav` subcommand with a clear diagnostic message.

4. **Built-In Hardened Rules Preserved from `Pmad.AI`:**
   - **`git`:** Only read-only subcommands and read-only flags on `git branch` / `git tag` are permitted; global `-c` / `--config-env` / `--exec-path` / `--git-dir` / `--work-tree` and output-writing flags (`--output`, `--ext-diff`, `--textconv`) are rejected.
   - **`find`:** `-exec`, `-execdir`, `-ok`, `-okdir`, `-delete`, `-fls`, `-fprint`, `-fprint0`, and `-fprintf` are rejected.
   - **`rg`:** `--pre`, `--pre-glob`, `-z`, and `--search-zip` are rejected.
   - **`sed`:** `-i` / `--in-place` and `w` / `W` / `e` script commands are rejected.
   - **Shell Operators & Pipelines:**
     - Pipelines (`|`) and logical chains (`&&`, `||`, `;`) are permitted only when **every** segment in the chain passes validation.
     - Output redirection (`>`, `>>`), input redirection (`<`, `<<`), command substitution (`` `...` ``, `$(...)`), process substitution (`<(...)`, `>(...)`), variable expansion (`$VAR`, `${VAR}`), and backgrounding (`&`) are rejected, while safe stderr redirection (`2>&1`, `2>/dev/null`) remains allowed.

### 2.3 Integration in `CanonicalTools.BashAsync`

1. In [`CanonicalTools.BashAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs), when the toolset is operating in read-only mode (`_isReadOnly == true`):
   - Validate the incoming command using the configured `Pmad.AI.Guardrails.ReadOnlyCommandValidator`.
   - When validation fails (`!result.IsAllowed`), fail fast by throwing `InvalidOperationException` with the validator's rejection reason (`result.Reason`), preserving existing fail-fast semantics for read-only stages.

---

## 3. Verification & Test Strategy

Update [`tests/NightCodeForge.Agents.Tests/ReadOnlyCommandValidatorTests.cs`](../../tests/NightCodeForge.Agents.Tests/ReadOnlyCommandValidatorTests.cs) to verify both existing NightCodeForge command expectations and the newly hardened `Pmad.AI` 0.0.2 guardrails:

1. **Allowed Read-Only Commands:**
   - Standard file/text inspection (`ls -la`, `cat src/File.cs`, `head -n 20`, `tail -n 20`, `wc -l`, `rg -n -- 'pattern' src`, `find . -maxdepth 3 -name '*.cs'`, `sed -n '10,20p' file.cs`, `awk '{print $1}' file.txt`, `fd`, `tree`, `stat`, `diff`).
   - Safe stderr redirection (`rg pattern src 2>/dev/null`, `git status 2>&1`).
   - Safe `git` subcommands (`git status`, `git log -n 5`, `git diff HEAD~1`, `git show HEAD`, `git branch -a`, `git branch --show-current`, `git tag -l`, `git ls-files`, `git grep`, `git rev-parse HEAD`, `git blame`).
   - Safe `csnav` subcommands (`csnav search Foo`, `csnav refs Bar`, `csnav outline File.cs`, `csnav inspect Symbol`, `csnav hierarchy BaseType`, `csnav deps`, `csnav decompile Type`, `csnav stats`).
2. **Blocked Mutating & Escape-Hatch Commands:**
   - Disallowed binaries (`rm`, `mv`, `cp`, `touch`, `mkdir`, `chmod`, `dotnet`, `npm`, `python`, `bash -c`, `sh -c`, `curl`, `wget`).
   - Mutating `git` subcommands and flags (`git checkout`, `git commit`, `git reset`, `git clean`, `git push`, `git stash`, `git branch new-branch`, `git branch -D old`, `git tag v1.0`, `git tag -d v1.0`, `git -c core.pager=less status`, `git diff --output=/tmp/out.patch`).
   - Mutating `csnav` flags and unknown subcommands (`csnav deps --export graph.dot`, `csnav deps --export=graph.json`, `csnav index`, `csnav`).
   - Mutating `find`, `rg`, and `sed` flags (`find . -delete`, `find . -exec rm {} ;`, `rg --pre=sh pattern`, `sed -i 's/a/b/' file.cs`, `sed 's/a/b/w out.txt' file.cs`).
   - Shell redirection, expansion, and substitution (`echo hi > file.txt`, `cat < /etc/passwd`, `echo $(whoami)`, ``echo `id` ``, `echo $HOME`, `diff <(ls a) <(ls b)`).

---

## 4. Definition of Done (DoD) Checklist

- [ ] Upgrade `Pmad.AI` package reference to `0.0.2` in `src/NightCodeForge.Agents/NightCodeForge.Agents.csproj`.
- [ ] Replace the custom in-repo `ReadOnlyCommandValidator` implementation with `Pmad.AI.Guardrails.ReadOnlyCommandValidator` configured with NightCodeForge's `AllowedCommands` and `csnav` `CommandValidationRule` (blocking `--export`).
- [ ] Wire `CanonicalTools.BashAsync` to validate read-only commands via the configured `Pmad.AI` validator and throw `InvalidOperationException` when disallowed.
- [ ] Update and expand `ReadOnlyCommandValidatorTests` in `tests/NightCodeForge.Agents.Tests` to cover `Pmad.AI` 0.0.2 hardened rules (`git` flags/branch/tag mutations, `find -exec`/`-delete`, `rg --pre`, `sed -i`, `csnav deps --export`, variable expansion, and process substitution).
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

