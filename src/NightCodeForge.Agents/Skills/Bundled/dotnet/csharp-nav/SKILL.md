---
name: csharp-nav
description: "Default C# navigation for AI agents — prefer this over grep/rg for any symbol-level question: where a type or member is defined, who references or calls it, which types implement an interface, what a type's members/public API are, or what an external BCL/NuGet type does (csnav decompiles sources that are not in the repo). Resolves symbols across the whole solution including .razor and .cshtml, filters by symbol kind and access level, and emits deterministic JSON for programmatic chaining. Fall back to rg only for literal strings, comments, config values, and non-C# files."
stack: dotnet
---

# C# Code Navigation with `csnav`

`csnav` is a fast, semantic, and token-efficient C# code navigation engine powered by Roslyn and ILSpy. It uses a warm background daemon to answer queries in milliseconds.

Always prefer `csnav` over `grep` / `rg` when the task is a C# symbol question (*"where is X defined"*, *"find references/usages of X"*, *"what implements I"*, *"list members or public API of T"*, *"show type hierarchy"*, *"what does System.X do"*):
- **Semantic Awareness**: Understands inheritance, overrides, interface implementations, and call sites without noise from comments, strings, or similarly named variables.
- **Decompiles External BCL & NuGet Sources**: Inspects implementation code of framework and package types that are not in the repository (impossible with `grep`/`rg`).
- **First-Class Razor & CSHTML Support**: Transparently navigates Blazor components (`.razor`) and ASP.NET Core views (`.cshtml`). Roslyn source generator output and mapped line spans (`#line`) are translated back to original files with exact line numbers and disk code snippets.
- **Token Efficiency & Chaining**: Filters by symbol kind and accessibility, and returns deterministic `--format json` for programmatic chaining or clean `--format markdown` summaries.
- **Warm Daemon**: Solutions are kept indexed in memory and automatically kept in sync via file watchers (no manual `sync` subcommand is needed).
- **When to fall back to `rg` / `grep`**: Only for literal strings, comments, configuration values, and non-C# files.

---

## Global Options & Stream Hygiene

All `csnav` subcommands accept these global options:
- `-s, --solution <path>`: Path to `.slnx`, `.sln`, `.csproj`, or directory (auto-detected by walking up from `pwd` if omitted).
- `-f, --framework <tfm>`: Target framework moniker filter (e.g. `net10.0`) for multi-targeted projects.
- `--format <markdown|json|text>`: Output format (default `markdown`, configurable via `CSNAV_FORMAT` or `CSNAV_DEFAULT_FORMAT` environment variables).
- `--no-color`: Disable ANSI color codes (also disabled automatically when `NO_COLOR` is set or output is redirected).

> **Stream Hygiene for `--format json`**:
> Daemon lifecycle logs (`[csnav] Starting background daemon...`, `[csnav] Daemon ready...`, `[csnav] Workspace initialized and file watcher active.`) are emitted exclusively on `stderr`.
> When parsing JSON output (`--format json`), **keep `stderr` separate from `stdout` (do NOT merge streams with `2>&1`)** so `stdout` remains pure, valid JSON.

---

## Command Reference

> **Singular & Plural Verbs**: Both singular and plural command names are accepted across navigation commands (`definition` / `definitions`, `implementations` / `implementation`, `references` / `reference`), along with short aliases (`def`, `impl`, `refs`, `api`, `explore`, `type`).
>
> **Doc-ID Prefixes**: Wherever `--symbol` (or `--type` on `inspect`/`decompile`) accepts a documentation comment ID, the full Roslyn prefix set is supported:
> - `T:` — Type (`class`, `interface`, `struct`, `record`, `enum`, `delegate`), e.g. `T:MyNamespace.OrderService`
> - `M:` — Method or constructor, e.g. `M:MyNamespace.OrderService.ProcessOrder(System.Int32)`
> - `P:` — Property or indexer, e.g. `P:MyNamespace.Order.Id`
> - `F:` — Field or enum member, e.g. `F:MyNamespace.OrderService._repository`
> - `E:` — Event, e.g. `E:MyNamespace.OrderService.OrderProcessed`

### 1. `csnav definition` (aliases: `definitions`, `def`, `goto-def`, `find-def`) — Go to Definition
Navigates directly to where a symbol is defined in source code, or discovers external metadata assembly and documentation comment ID.

```bash
# Go to definition by symbol name or Doc-ID (T:, M:, P:, F:, E:)
csnav definition --symbol "IOrderRepository.GetOrder" --format json
csnav def --symbol "T:MyApp.Services.OrderService" --format json

# Go to definition at exact source location (supports .cs, .razor, and .cshtml)
csnav definition --file "src/Services/OrderService.cs" --line 42 --column 18 --format json
csnav definition --file "src/Web/Components/OrderCard.razor" --line 15 --column 20 --format json
```

**Key Options:**
- `--symbol <name-or-docId>`: Symbol name, qualified name, or Doc-ID (`T:...`, `M:...`, `P:...`, `F:...`, `E:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve symbol at 1-indexed source location.
- `--project <name>`: Restrict definitions to a specific project.
- `--include-generated`: Include definitions in generated files (default `false`).
- `--limit <int>` / `--offset <int>`: Pagination controls (default `--limit 50`, max `500`, default `--offset 0`).
- `--format <markdown|json|text>`: Output format (default `markdown`).

---

### 2. `csnav implementations` (aliases: `implementation`, `impl`, `goto-impl`, `find-impl`) — Go to Implementation
Finds all concrete implementations, overrides, or derived classes/interfaces for any interface, class, or member.

```bash
# Find implementations of an interface (finds C# classes and Blazor .razor components)
csnav implementations --symbol "IOrderRepository" --format json
csnav impl --symbol "T:MyApp.IOrderRepository" --format json

# Find implementations of an interface method
csnav implementations --symbol "IOrderRepository.GetOrder" --format json

# Find implementations at exact call-site source location
csnav implementations --file "src/Services/OrderService.cs" --line 42 --column 18 --format json

# Exclude implementations residing in test projects
csnav implementations --symbol "IOrderRepository" --exclude-tests --format json
```

**Key Options:**
- `--symbol <name-or-docId>`: Symbol name, qualified name, or Doc-ID (`T:...`, `M:...`, `P:...`, `E:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve symbol at 1-indexed source location.
- `--exclude-tests`: Excludes implementations in test projects.
- `--project <name>`: Restrict implementations to a single project.
- `--include-generated`: Include implementations in generated code (default `false`).
- `--limit <int>` / `--offset <int>`: Pagination controls (default `--limit 50`, max `500`, default `--offset 0`).
- `--format <markdown|json|text>`: Output format (default `markdown`).

---

### 3. `csnav references` (aliases: `reference`, `refs`, `ref`, `find-refs`) — Find References
Finds all usages, call sites, implementations, and reads/writes of a symbol.

```bash
# Find references by symbol name or Doc-ID (e.g. T:, M:, P:, F:, E:)
csnav references --symbol "ProcessOrder" --format json
csnav refs --symbol "T:MyApp.Domain.Order" --format json

# Exclude references in test projects
csnav references --symbol "IOrderRepository.SaveOrder" --exclude-tests --format json

# Find references at exact source file location (1-indexed line and column, works in .cs, .razor, or .cshtml)
csnav references --file "src/Services/OrderService.cs" --line 42 --column 18 --format json
csnav references --file "src/Web/Components/OrderCard.razor" --line 12 --column 18 --format json

# Filter references to a specific project
csnav references --symbol "Order" --project "MyApp.Core" --format json
```

**Key Options:**
- `--symbol <name-or-docId>`: Symbol name, qualified name, or Doc-ID (`T:...`, `M:...`, `P:...`, `F:...`, `E:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve symbol at 1-indexed source location.
- `--exclude-tests`: Excludes usages in test projects (matches `*.Tests`, `*.Test`, etc.).
- `--project <name>`: Restrict references to a single project.
- `--include-generated`: Include references in generated code (default `false`).
- `--include-overrides` / `--include-implementations`: Include virtual overrides and interface implementations (both default `true`).
- `--limit <int>` / `--offset <int>`: Pagination controls (default `--limit 50`, max `500`, default `--offset 0`).
- `--format <markdown|json|text>`: Output format (default `markdown`).

---

### 4. `csnav search` (aliases: `api`, `find`) — Public API & Symbol Search
Searches type and member declarations using wildcard, substring, or regex patterns across source code and external referenced assemblies.

```bash
# Search by pattern (wildcard * supported; positional argument)
csnav search "*Repository" --format json
csnav api "*Repository" --format json

# Search solution code only (fastest, excludes NuGet/BCL)
csnav search "*Service" --scope solution --format json

# Filter search to a specific project
csnav search "*Command" --project "MyApp.Cli" --format json

# Include external dependencies (NuGet & .NET BCL)
csnav search "JsonSerializer*" --scope all --format json

# Filter by symbol kind (case-insensitive, comma-separated values supported)
csnav search "I*Handler" --kind interface --format json
csnav search "*Order*" --kind class,record --scope solution --format json

# Search within XML documentation comments (summary, remarks, param, returns)
csnav search --doc "calculates sha256" --scope solution --format markdown
```

**Key Options:**
- `<pattern>`: Positional search pattern (e.g. `Order*`, `*Repository`, `Find*`). Optional when `--doc` is provided.
- `--scope <all|solution|source|dependencies|<assembly-or-project>>`: `solution` (or `source`) for project sources only, `dependencies` for external assemblies only, `all` for both (default `all`), or a specific assembly/project name.
- `--project <name>`: Restrict search to a specific project.
- `--kind <kind>`: Case-insensitive declaration kind filter (comma-separated list supported). Accepted values:
  - Composite kinds: `all` (default), `type` (matches `class`, `interface`, `struct`, `record`, `enum`, `delegate`), `member` (matches `method`, `constructor`, `property`, `field`, `event`).
  - Specific type kinds: `class` (also matches `record`), `interface`, `struct`, `record`, `enum`, `delegate`.
  - Specific member kinds: `method`, `constructor`, `property`, `field`, `event`.
- `--access <public|internal|all>`: Minimum accessibility (default `public`).
- `--doc <text>`: Multi-token full-text search across XML documentation comments (`summary`, `remarks`, `param`, `returns`).
- `--limit <int>` / `--offset <int>`: Pagination controls (default `--limit 50`, max `500`, default `--offset 0`).

---

### 5. `csnav inspect` (aliases: `explore`, `type`) — Type & Member Inspection
Inspects constructors, methods, properties, fields, events, base types, and implemented interfaces.

```bash
# Inspect type by fully qualified name, simple name, or T: Doc-ID
csnav inspect --type "MyApp.Services.OrderService" --format json
csnav explore --type "T:MyApp.Services.OrderService" --format json

# Inspect only declared members (exclude inherited object/base members)
csnav inspect --type "MyApp.Services.OrderService" --no-inherited --format json

# Inspect type defined or referenced at source location
csnav inspect --file "src/MyApp/OrderService.cs" --line 10 --column 15 --format json

# Include internal and private members
csnav inspect --type "MyApp.Services.OrderService" --access all --format json
```

**Key Options:**
- `--type <name-or-docId>`: Type name, fully qualified name, or `T:...` Doc-ID.
- `--file <path> [--line <int>] [--column <int>]`: Source file location containing the type declaration or reference.
- `--no-inherited`: Show only members declared directly on this type (recommended to control output size).
- `--access <public|internal|private|all>`: Minimum accessibility (default `public`).
- `--limit <int>` / `--offset <int>`: Member pagination controls (default `--limit 50`, max `500`, default `--offset 0`).

---

### 6. `csnav decompile` — Decompile External Assemblies
Decompiles external NuGet or .NET BCL libraries to readable C# source code, automatically mapping reference assemblies (`.Ref`) to runtime implementation assemblies.

> **Standalone Fallback**: `csnav decompile --type <Type>` works for .NET BCL/runtime types even if the solution workspace failed to load or has `0` projects, making it a reliable diagnostic tool when workspace loading is broken.

```bash
# Decompile an entire external BCL type
csnav decompile --type "System.Uri" --format markdown

# Decompile a specific member (recommended to keep output compact)
csnav decompile --type "System.Uri" --member "IsLoopback" --format markdown

# Decompile by Doc-ID or qualified symbol
csnav decompile --symbol "T:System.Uri" --format markdown
csnav decompile --symbol "M:System.Uri.IsWellFormedUriString" --format markdown

# Decompile NuGet package symbol
csnav decompile --assembly "System.Text.Json" --type "System.Text.Json.JsonSerializer"
```

**Key Options:**
- `--type <name-or-docId>`: Fully qualified type name (or `T:...` Doc-ID) to decompile.
- `--member <name>`: Specific member name to decompile instead of the entire type (optional).
- `--assembly <name>`: External assembly name (optional).
- `--symbol <docId-or-name>`: Qualified symbol name or Doc-ID (`T:...`, `M:...`, `P:...`, `F:...`, `E:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve external symbol at a source call site and decompile it.

---

### 7. `csnav server` — Daemon Management
Controls the warm background daemon managing the Roslyn workspace.

```bash
# Check status of the background daemon (supports --format json)
csnav server status
csnav server status --format json

# Explicitly start the background daemon (supports --format json)
csnav server start

# Force full workspace reload from disk
csnav server reload

# Gracefully stop daemon
csnav server stop
```

---

## Pagination & JSON Output Contract

When `--format json` is used, list-returning commands (`search`, `definition`, `implementations`, `references`, `inspect`) return a consistent pagination and metadata envelope:

| Field | Type | Description |
| :--- | :--- | :--- |
| `totalMatches` / `totalDefinitions` / `totalImplementations` / `totalReferences` / `totalMembers` | `int` | Total matching items across the workspace before pagination. |
| `offset` | `int` | 0-based starting index of the returned page. |
| `limit` | `int` | Maximum page size requested (default `50`, clamped to `1..500`). |
| `truncated` | `bool` | `true` when additional items exist beyond this page (`offset + count < total`). |
| `nextOffset` | `int?` | Exact value to pass to `--offset <nextOffset>` for the next page (present when `truncated: true`, omitted when `false`). |
| `indexState` | `string` | (`search` only) `"ready"` when external assembly indexing is complete. |
| `snapshotVersion` | `long?` | Monotonically increasing workspace snapshot version. |
| `stale` | `bool?` | `true` only while a background workspace reload is in progress; omitted otherwise. |

**Deterministic Pagination Loop:**
1. Run command with `--limit 50 --offset 0 --format json`.
2. While `response.truncated == true`, repeat the command with `--offset <response.nextOffset>`.

### Stable Chaining Fields Across Commands
Result items in `--format json` include stable location and identity fields designed to be piped directly into follow-up commands:
- `search` results (`results[]`): `name`, `kind`, `signature`, `containingType`, `assembly`, `isExternal`, `summary`, `file`, `line`, `endLine`, `symbolId`.
- `definition` (`target` & `definitions[]`): `target.definedIn`, `target.line`, and item `file`, `line`, `column`, `endLine`, `kind`, `containingType`, `containingMember`, `assemblyName`, `isMetadata`, `isGenerated`, `snippet`, `symbolId`.
- `implementations` (`target` & `implementations[]`) & `references` (`target` & `references[]`): `target.definedIn`, `target.line`, and item `file`, `line`, `column`, `kind`, `containingType`, `containingMember`, `isGenerated`, `snippet`.

**Chaining Workflow (`search` → `inspect` / `definition` → `references`):**
- Pass `symbolId` (e.g. `"T:..."` or `"M:..."`) from `search` or `definition` directly into `--symbol <symbolId>` of `definition`, `implementations`, `references`, or `decompile` for zero-ambiguity resolution.
- Pass `file` + `line` (+ `column`) or `target.definedIn` + `target.line` from one command directly into `--file` and `--line` of the next command.

---

## Daemon Failure & Recovery

### Cold-Start Cost vs. Warm Queries
- **First Query (Cold Start)**: Automatically spawns the background daemon and blocks while Roslyn loads and compiles the solution (`stderr` prints `[csnav] Starting background daemon...` and `[csnav] Daemon ready...`). **Give the first command a generous timeout (e.g. `120s`–`300s` on large solutions).**
- **Subsequent Queries (Warm Daemon)**: Answered from the in-memory Roslyn snapshot in milliseconds.

### Diagnosing & Recovering a Stuck or Unhealthy Daemon
- **Symptoms**:
  - `csnav server status` stays on `State: loading` (never reaches `ready`) or reports `State: unhealthy` with `Projects: 0`.
  - Queries return `INTERNAL_ERROR` (mentioning MSBuild, build host, or workspace failure) or `WORKSPACE_LOADING`.
- **Recovery Procedure**:
  1. Stop the stale daemon:
     ```bash
     csnav server stop
     ```
  2. Re-run your query (or run `csnav server start`) with a generous timeout to spawn a fresh daemon.
  3. Alternatively, if the daemon is running but out of sync with `.csproj` changes, run `csnav server reload`.
  4. If workspace loading continues to fail due to broken project files or missing SDKs, you can still use `csnav decompile --type <FullTypeName>` standalone to inspect BCL/runtime types.

---

## Common Pitfalls & Gotchas

1. **Arguments are named flags, not positionals** (except `search <pattern>`):
   - ❌ `csnav references "OrderService"` (fails)
   - ✅ `csnav references --symbol "OrderService"`
   - ✅ `csnav inspect --type "OrderService"`
   - ✅ `csnav search "*OrderService*"` (`search` is the only command with a positional `<pattern>` argument)
2. **Controlling output size on `inspect` and `decompile`**:
   - For `inspect`, prefer `--no-inherited` (excludes `System.Object` / base members) and `--access public` (default) before using `--limit`.
   - For `decompile`, pass `--member <MemberName>` whenever you only need a single method or property rather than decompiling an entire class.
3. **`--exclude-tests` vs `--scope` / `--project`**:
   - `--exclude-tests` is supported on `references` and `implementations`.
   - On `search`, use `--scope solution` to exclude external packages or `--project <ProjectName>` to target a specific project.
4. **Self-healing on CLI usage errors & `AMBIGUOUS_SYMBOL`**:
   - A non-zero exit code accompanied by usage help indicates a mistyped flag or missing required option — read the usage hint and re-issue the command.
   - An `AMBIGUOUS_SYMBOL` error returns a `candidates` list containing each overload/type's `symbolId` (`M:...`, `T:...`) and `definedIn:line`. Re-run immediately with `--symbol "<symbolId>"`.

---

## Agent Guidelines & Workflows

1. **Investigating an Unknown Codebase:**
   - Use `csnav search "*"` or `csnav search "*Service"` (or `csnav api`) with `--scope solution --format json` to map available domain services.
   - Use `csnav inspect --type <Type> --no-inherited --format json` (or `csnav explore`) to inspect API contracts before reading full files.

2. **Navigating to Definitions & Implementations:**
   - Run `csnav definition --symbol <SymbolName>` (or `csnav def`) or `csnav definition --file <Path> --line <Line>` to jump straight to where a type or member is declared.
   - Run `csnav implementations --symbol <InterfaceName>` (or `csnav impl`) to find all concrete classes/structs implementing an interface or all overrides of a method.

3. **Refactoring or Changing a Method:**
   - Before modifying a method, run `csnav references --symbol <MethodName> --exclude-tests` (or `csnav refs`) to see all production call sites.
   - Run `csnav references --symbol <MethodName>` without `--exclude-tests` to see which unit tests need updating.

4. **Investigating External Dependencies:**
   - Use `csnav decompile --type <ExternalType> [--member <Member>]` to inspect the actual implementation when debugging third-party library behavior or framework internals.

5. **Navigating Blazor & Razor Files (`.razor`, `.cshtml`):**
   - Pass `.razor` or `.cshtml` files directly to `--file` for `definition`, `implementations`, or `references`.
   - References include usages in Blazor component templates and `@code` blocks with snippet previews extracted directly from the Razor file.
   - Interface implementations (e.g. `@implements IDisposable` or `@implements IComponent`) are discovered automatically.
