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
| **1** | **Incomplete Shell Escape-Hatch Detection** | Blocks process substitution (`<(...)`, `>(...)`), environment/parameter expansion (`$VAR`, `${VAR}`), dollar-quoting (`$'...'`, `$"..."`), brace expansion (`{a,b}`), input redirection (`<`), heredocs (`<<`), subshell grouping (`(...)`), and multiline newline/control-character injection outside quotes, while safely allowing read-only stderr redirection (`2>&1`, `2>/dev/null`). |
| **2** | **Permissive `find`, `rg`, and `sed` Flags** | Enforces per-command rule validators blocking arbitrary execution and file writes via `find -exec`/`-execdir`/`-ok`/`-okdir`/`-delete`/`-fprint`/`-fprint0`/`-fprintf`/`-fls`, `rg --pre`/`--pre-glob`/`--hostname-bin`, and `sed -i`/`--in-place` (including bundled short flags such as `-ni` or `-Ei.bak`). |
| **3** | **Incomplete `git` Flag & Subcommand Inspection** | Blocks global `git` execution/path overrides (`-c*`, `--config-env`, `--exec-path`, `--upload-pack`, `--paginate`, `-p`, `--git-dir`, `--namespace`, `--super-prefix`, `--bare`), blocks file-writing diff/log/grep flags (`--output`, `--ext-diff*`, `git grep -O*`/`--open-files-in-pager`), and restricts `git branch`, `git tag`, and `git remote` to read-only listing/query flags and subcommands (`AllowPositionalArguments = false` on `branch`/`tag`) so an agent cannot create, rename, or delete branches/tags/remotes in read-only mode. |
| **4** | **Code Duplication & Translator Integration** | Maintaining a forked validator in `NightCodeForge.Agents` duplicates `Pmad.AI` guardrails and prevents direct plug-in into `Pmad.AI.Tools.LenientToolDispatcherOptions.CommandValidator` ([Step 11.43B](STEP_11_43B_PMAD_AI_LENIENT_TOOL_DISPATCHER_AND_DELEGATING_CHAT_CLIENT_MIGRATION.md)). |

---

## 2. Detailed Requirements & Specifications

### 2.1 Package Reference (`Pmad.AI` `0.0.2`)

1. Add the `Pmad.AI` `0.0.2` NuGet package reference (`<PackageReference Include="Pmad.AI" Version="0.0.2" />`) to [`src/NightCodeForge.Agents/NightCodeForge.Agents.csproj`](../../src/NightCodeForge.Agents/NightCodeForge.Agents.csproj).

### 2.2 NightCodeForge `ReadOnlyCommandValidator` Configuration

1. **Clean-Break Replacement:**
   - Remove the custom regex/token parsing implementation in [`src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs`](../../src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs).
   - Provide a shared, pre-configured `Pmad.AI.Guardrails.ReadOnlyCommandValidator` instance (or factory/wrapper in `NightCodeForge.Agents.Tools`) configured via `ReadOnlyCommandValidatorOptions` (`Dialect = ShellDialect.Bash`).

2. **Allowed Read-Only Binaries (`AllowedBinaries`):**
   - Retain all default `Pmad.AI` allowed binaries (`find`, `rg`, `grep`, `cat`, `ls`, `git`, `head`, `tail`, `wc`, `cd`) and built-in `CommandRules` (`git`, `find`, `rg`, `sed`).
   - Register the additional read-only inspection utilities permitted in NightCodeForge workspaces and stage directives in `AllowedBinaries`:
     - `fd`, `locate`, `which`, `where`, `whereis`, `tree`, `dir`, `pwd`, `ripgrep`, `egrep`, `fgrep`, `sed`, `awk`, `file`, `stat`, `nl`, `sort`, `uniq`, `cut`, `tr`, `diff`, `cmp`, `less`, `more`, `echo`, `printf`, `true`, `false`, `test`, `csnav`.

3. **Custom `csnav` Command Rule (`CommandRules["csnav"]`):**
   - Register a declarative `CommandValidationRule` for `csnav` (the C# Roslyn navigation CLI):
     - **Global flags taking arguments (`FlagsWithArgs`):** `"-s"`, `"--solution"`, `"-f"`, `"--framework"`, `"--format"`.
     - **Allowed subcommands (`AllowedSubcommands`):**
       - `references`, `refs`, `find-refs`
       - `definition`, `definitions`, `def`, `find-def`, `goto-def`
       - `implementations`, `implementation`, `impl`, `find-impl`, `goto-impl`
       - `search`, `api`, `find`, `api-search`, `apis`
       - `inspect`, `explore`, `type`
       - `decompile`
       - `sync`
       - `server`
     - **Nested `server` subcommand rule (`NestedSubcommands["server"]`):**
       - `AllowedSubcommands = ["status", "start", "reload"]` (strictly blocking `csnav server stop`).
     - Reject disallowed subcommands (such as `csnav skill` / `csnav skills install` or `csnav server stop`) and missing subcommands (when not `-h`/`--help`/`-v`/`--version`, via default `AllowNoSubcommand = false`) with a clear diagnostic reason.

4. **Built-In Hardened Rules Preserved from `Pmad.AI`:**
   - **`git`:** Only read-only subcommands (`status`, `log`, `diff`, `show`, `blame`, `ls-files`, `grep`, `rev-parse`, `branch`, `tag`, `ls-tree`, `describe`, `shortlog`, `remote`, `cat-file`, `check-ref-format`) are permitted; `git branch` and `git tag` enforce `AllowPositionalArguments = false` and block mutating flags; `git remote` only allows no-subcommand or `show`/`get-url`; `git grep` blocks `-O*` / `--open-files-in-pager`; global `-c*`, `--config-env`, `--exec-path`, `--upload-pack`, `--paginate`, `-p`, `--output`, `--ext-diff*`, `--git-dir`, `--namespace`, `--super-prefix`, and `--bare` are rejected.
   - **`find`:** `-exec`, `-execdir`, `-ok`, `-okdir`, `-delete`, `-fls`, `-fprint`, `-fprint0`, and `-fprintf` are rejected.
   - **`rg`:** `--pre`, `--pre-glob`, and `--hostname-bin` are rejected.
   - **`sed`:** `-i*` (including bundled flags such as `-ni` or `-Ei.bak`) and `--in-place` are rejected.
   - **Shell Operators, Paths & Pipelines:**
     - Relative/non-system path execution (`./script.sh`, `/tmp/evil`) is rejected; only bare command names or `/bin/...` and `/usr/bin/...` system paths are allowed.
     - Pipelines (`|`), logical chains (`&&`, `||`, `;`), and multiline commands are permitted only when **every** segment in the chain passes validation (and dangling/empty segments are rejected).
     - Output redirection (`>`, `>>`), input redirection (`<`, `<<`), command substitution (`` `...` ``, `$(...)`), process substitution (`<(...)`, `>(...)`), subshell grouping (`(...)`), brace expansion (`{a,b}`), dollar-quoting (`$'...'`, `$"..."`), and variable expansion outside single quotes (`$VAR`, `${VAR}`, `$1`, `$?`, `$$`) are rejected, while safe stderr redirection (`2>&1`, `2>/dev/null`) remains allowed.

### 2.3 Integration in `CanonicalTools.BashAsync`

1. In [`CanonicalTools.BashAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs), when the toolset is operating in read-only mode (`_isReadOnly == true`):
   - Validate the incoming command using the configured `Pmad.AI.Guardrails.ReadOnlyCommandValidator`.
   - When validation fails (`!result.IsAllowed`), log the warning and fail fast by throwing `InvalidOperationException` with the validator's rejection reason (`result.Reason`), matching `WriteAsync` and `EditAsync` read-only fail-fast semantics.

---

## 3. Verification & Test Strategy

Update [`tests/NightCodeForge.Agents.Tests/ReadOnlyCommandValidatorTests.cs`](../../tests/NightCodeForge.Agents.Tests/ReadOnlyCommandValidatorTests.cs) to verify both existing NightCodeForge command expectations and the newly hardened `Pmad.AI` 0.0.2 guardrails:

1. **Allowed Read-Only Commands:**
   - Standard file/text inspection (`ls -la`, `cat src/File.cs`, `head -n 20`, `tail -n 20`, `wc -l`, `rg -n -- 'pattern' src`, `find . -maxdepth 3 -name "*.cs"`, `sed -n "10,20p" file.cs`, `awk '{print $1}' file.txt`, `fd -e cs`, `tree src/`, `stat`, `diff`, `pwd`).
   - Safe stderr redirection (`rg pattern src 2>/dev/null`, `git status 2>&1`).
   - Safe `git` subcommands (`git status`, `git log -n 5`, `git diff HEAD~1`, `git show HEAD`, `git branch`, `git branch -a`, `git branch --show-current`, `git tag`, `git tag -l "v1.*"`, `git remote -v`, `git remote show origin`, `git ls-files`, `git grep "interface"`, `git rev-parse HEAD`, `git blame src/File.cs`).
   - Safe `csnav` subcommands (`csnav search "*Symbol*"`, `csnav api-search -p "*Symbol*"`, `csnav references -t IWorkspaceSymbolService`, `csnav refs --symbol Foo`, `csnav definition -t CrucibleAgentHarness`, `csnav implementations --symbol IFoo`, `csnav inspect --type Foo`, `csnav explore -t IdeationService`, `csnav decompile --type System.Uri`, `csnav sync`, `csnav server status`, `csnav server start -s NightCodeForge.sln`, `csnav -s NightCodeForge.sln server reload`).
2. **Blocked Mutating & Escape-Hatch Commands:**
   - Disallowed binaries and relative path execution (`rm`, `mv`, `cp`, `touch`, `mkdir`, `chmod`, `dotnet`, `npm`, `python`, `bash -c`, `sh -c`, `curl`, `wget`, `./script.sh`, `/tmp/evil`).
   - Mutating `git` subcommands and flags (`git checkout`, `git commit`, `git reset`, `git clean`, `git push`, `git stash`, `git branch new-branch`, `git branch -D old`, `git tag v1.0`, `git tag -d v1.0`, `git remote add origin url`, `git -c core.pager=less status`, `git diff --output=/tmp/out.patch`, `git grep -Osh foo`).
   - Disallowed `csnav` subcommands and missing subcommand (`csnav skills install`, `csnav skill install`, `csnav server stop`, `csnav`, `csnav -s NightCodeForge.sln`).
   - Mutating `find`, `rg`, and `sed` flags (`find . -delete`, `find . -exec rm {} +`, `find . -fprint /tmp/out.txt`, `rg --pre=sh pattern`, `sed -i "s/a/b/" file.cs`, `sed -ni "s/a/b/" file.cs`, `sed --in-place file.cs`).
   - Shell redirection, expansion, and substitution (`echo hi > file.txt`, `cat < /etc/passwd`, `echo $(whoami)`, ``echo `id` ``, `echo $HOME`, `find . $'-exec' rm -rf {} +`, `diff <(ls a) <(ls b)`).
3. **`CanonicalTools.BashAsync` Read-Only Enforcement:**
   - Verify that `CanonicalTools.BashAsync` throws `InvalidOperationException` (without invoking `IExecutionEnvironment`) when given a disallowed or mutating command in read-only mode, and executes normally when given a permitted read-only command.

---

## 4. Definition of Done (DoD) Checklist

- [ ] Add `Pmad.AI` (`0.0.2`) package reference in `src/NightCodeForge.Agents/NightCodeForge.Agents.csproj`.
- [ ] Replace the custom in-repo `ReadOnlyCommandValidator` implementation with `Pmad.AI.Guardrails.ReadOnlyCommandValidator` configured with NightCodeForge's `AllowedBinaries` and `csnav` `CommandValidationRule` (allowing read-only `csnav` subcommands and `server status|start|reload`, while blocking `skills` and `server stop`).
- [ ] Wire `CanonicalTools.BashAsync` to validate read-only commands via the configured `Pmad.AI` validator and throw `InvalidOperationException` when disallowed.
- [ ] Update and expand `ReadOnlyCommandValidatorTests` in `tests/NightCodeForge.Agents.Tests` to cover `Pmad.AI` 0.0.2 hardened rules (`git` flags/branch/tag/remote mutations, `find -exec`/`-delete`, `rg --pre`, `sed -i`, `csnav skills`/`server stop`, variable expansion, and process substitution).
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

