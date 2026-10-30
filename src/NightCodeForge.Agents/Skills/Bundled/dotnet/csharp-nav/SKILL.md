---
name: csharp-nav
description: High-performance C# navigation engine for AI agents and developers. Use when navigating, searching, or exploring C# codebases (including Blazor .razor and ASP.NET Core .cshtml files). Supports finding references, searching public APIs, exploring type hierarchies and members, and decompiling external BCL/NuGet assemblies.
stack: dotnet
---

# C# Code Navigation with `csnav`

`csnav` is a fast, semantic, and token-efficient C# code navigation engine powered by Roslyn and ILSpy. It uses a warm background daemon to answer queries in milliseconds.

Always prefer `csnav` over `grep` or file text searching when navigating C# code:
- **Semantic Awareness**: Understands inheritance, overrides, interface implementations, and call sites without noise from comments, strings, or similarly named variables.
- **First-Class Razor & CSHTML Support**: Transparently navigates Blazor components (`.razor`) and ASP.NET Core views (`.cshtml`). Roslyn source generator output and mapped line spans (`#line`) are translated back to original files with exact line numbers and disk code snippets.
- **Token Efficiency**: Returns structured data directly (`--format json`) or clean summaries (`--format markdown`), saving context window tokens.
- **Warm Daemon**: Solutions are kept indexed in memory and automatically kept in sync via file watchers.

---

## Command Reference

### 1. `csnav definition` (alias: `csnav def`, `csnav goto-def`, `csnav find-def`) — Go to Definition
Navigates directly to where a symbol is defined in source code, or discovers external metadata assembly and documentation comment ID.

```bash
# Go to definition by symbol name
csnav definition --symbol "IOrderRepository.GetOrder" --format json
# Or shorthand: csnav def --symbol "IOrderRepository.GetOrder" --format json

# Go to definition at exact source location (supports .cs, .razor, and .cshtml)
csnav definition --file "src/Services/OrderService.cs" --line 42 --column 18 --format json
csnav definition --file "src/Web/Components/OrderCard.razor" --line 15 --column 20 --format json
```

**Key Options:**
- `--symbol <name>`: Symbol name, qualified name, or doc ID (`M:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve symbol at source location.
- `--project <name>`: Restrict definitions to a specific project.
- `--include-generated`: Include definitions in generated code (default `false`).
- `--format <markdown|json|text>`: Output format (default `markdown`).

---

### 2. `csnav implementations` (alias: `csnav impl`, `csnav goto-impl`, `csnav find-impl`) — Go to Implementation
Finds all concrete implementations, overrides, or derived classes/interfaces for any interface, class, or member.

```bash
# Find implementations of an interface (finds C# classes and Blazor .razor components)
csnav implementations --symbol "IOrderRepository" --format json
# Or shorthand: csnav impl --symbol "IOrderRepository" --format json

# Find implementations of an interface method
csnav implementations --symbol "IOrderRepository.GetOrder" --format json

# Find implementations at exact call-site source location
csnav implementations --file "src/Services/OrderService.cs" --line 42 --column 18 --format json

# Exclude implementations residing in test projects
csnav implementations --symbol "IOrderRepository" --exclude-tests --format json
```

**Key Options:**
- `--symbol <name>`: Symbol name, qualified name, or doc ID (`M:...`).
- `--file <path> --line <int> [--column <int>]`: Resolve symbol at source location.
- `--exclude-tests`: Excludes implementations in test projects.
- `--project <name>`: Restrict implementations to a single project.
- `--include-generated`: Include implementations in generated code (default `false`).
- `--format <markdown|json|text>`: Output format (default `markdown`).

---

### 3. `csnav references` (alias: `csnav refs`) — Find References
Finds all usages, call sites, implementations, and reads/writes of a symbol.

```bash
# Find references by symbol name (e.g. method, class, interface, property)
csnav references --symbol "ProcessOrder" --format json
# Or shorthand: csnav refs --symbol "ProcessOrder" --format json

# Exclude references in test projects
csnav references --symbol "IOrderRepository.SaveOrder" --exclude-tests --format json

# Find references at exact source file location (1-indexed line and column, works in .cs, .razor, or .cshtml)
csnav references --file "src/Services/OrderService.cs" --line 42 --column 18 --format json
csnav references --file "src/Web/Components/OrderCard.razor" --line 12 --column 18 --format json

# Filter references to a specific project
csnav references --symbol "Order" --project "MyApp.Core" --format json
```

**Key Options:**
- `--symbol <name>`: Symbol name, qualified name, or doc ID (`M:...`).
- `--file <path> --line <int> --column <int>`: Resolve symbol at source location.
- `--exclude-tests`: Excludes usages in test projects (matches `*.Tests`, `*.Test`, etc.).
- `--project <name>`: Restrict references to a single project.
- `--limit <int>`: Max results (default `100`).
- `--format <markdown|json|text>`: Output format (default `markdown`, configurable via `CSNAV_FORMAT` or `CSNAV_DEFAULT_FORMAT` env var).
- `--no-color`: Disable ANSI color formatting (colors enabled for markdown and text by default unless output is redirected).

---

### 4. `csnav search` (alias: `csnav api`, `csnav find`) — Public API & Symbol Search
Searches type and member declarations using wildcard patterns across source code and external referenced assemblies.

```bash
# Search by pattern (wildcard * supported)
csnav search "*Repository" --format json
# Or shorthand: csnav api "*Repository" --format json

# Search solution code only (fastest)
csnav search "*Service" --scope solution --format json

# Include external dependencies (NuGet & .NET BCL)
csnav search "JsonSerializer*" --scope all --format json

# Filter by symbol kind (class, interface, method, property, enum, record, struct)
csnav search "I*Handler" --kind interface --format json

# Search within XML documentation comments (summary, param, returns)
csnav search --doc "calculates sha256" --format markdown
```

**Key Options:**
- `<pattern>`: Search pattern (e.g. `Order*`, `*Repository`, `Find*`).
- `--scope <solution|all>`: `solution` for project sources, `all` to include external dependencies (default `solution`).
- `--kind <type|class|interface|method|property|all>`: Filter by declaration kind.
- `--access <public|internal|all>`: Minimum accessibility (default `public`).
- `--doc <text>`: Full-text search across XML documentation comments (summary, param, returns).
- `--limit <int>`: Max results (default `50`).

---

### 5. `csnav inspect` (alias: `csnav explore`, `csnav type`) — Type & Member Inspection
Inspects constructors, methods, properties, fields, base types, and implemented interfaces.

```bash
# Inspect type by fully qualified name
csnav inspect --type "MyApp.Services.OrderService" --format json
# Or shorthand: csnav explore --type "MyApp.Services.OrderService" --format json

# Inspect only declared members (exclude inherited object/base members)
csnav inspect --type "MyApp.Services.OrderService" --no-inherited --format json

# Inspect type defined at source location
csnav inspect --file "src/MyApp/OrderService.cs" --line 10 --column 15 --format json

# Include internal and private members
csnav inspect --type "MyApp.Services.OrderService" --access all --format json
```

**Key Options:**
- `--type <name>`: Fully qualified type name.
- `--file <path>`: Source file containing the type declaration.
- `--no-inherited`: Show only members declared directly on this type.
- `--access <public|internal|private|all>`: Minimum accessibility (default `public`).

---

### 6. `csnav decompile` — Decompile External Assemblies
Decompiles external NuGet or .NET BCL libraries to readable C# source code, automatically mapping reference assemblies (`.Ref`) to runtime implementation assemblies.

```bash
# Decompile an entire external BCL type
csnav decompile --type "System.Uri" --format markdown

# Decompile a specific member
csnav decompile --type "System.Uri" --member "IsLoopback" --format markdown

# Decompile NuGet package symbol
csnav decompile --assembly "System.Text.Json" --type "System.Text.Json.JsonSerializer"
```

**Key Options:**
- `--type <name>`: Fully qualified type name to decompile.
- `--member <name>`: Specific member to decompile (optional).
- `--assembly <name>`: External assembly name (optional).

---

### 7. `csnav server` — Daemon Management
Controls the warm background daemon managing the Roslyn workspace.

```bash
# Check status of the background daemon
csnav server status

# Force workspace reload
csnav server reload

# Gracefully stop daemon
csnav server stop
```

---

## Agent Guidelines & Workflows

1. **Investigating an Unknown Codebase:**
   - Use `csnav search "*"` or `csnav search "*Service"` (or `csnav api`) with `--scope solution` to map available domain services.
   - Use `csnav inspect --type <Type> --no-inherited --format json` (or `csnav explore`) to inspect API contracts before reading full files.

2. **Navigating to Definitions & Implementations:**
   - Run `csnav definition --symbol <SymbolName>` (or `csnav def`) or `csnav definition --file <Path> --line <Line>` to jump straight to where a type or member is declared.
   - Run `csnav implementations --symbol <InterfaceName>` (or `csnav impl`) to find all concrete classes/structs implementing an interface or all overrides of a method.

3. **Refactoring or Changing a Method:**
   - Before modifying a method, run `csnav references --symbol <MethodName> --exclude-tests` (or `csnav refs`) to see all production call sites.
   - Run `csnav references --symbol <MethodName>` without `--exclude-tests` to see which unit tests need updating.

4. **Investigating External Dependencies:**
   - Use `csnav decompile --type <ExternalType>` to inspect the actual implementation when debugging third-party library behavior or framework internals.

5. **Token Efficiency:**
   - Prefer `--format json` for automated processing and data extraction.
   - File paths are automatically returned relative to current working directory (`pwd`), significantly reducing token overhead.
   - Use `--limit` when searching broad patterns to avoid unnecessary token usage.

6. **Navigating Blazor & Razor Files (`.razor`, `.cshtml`):**
   - You can pass `.razor` or `.cshtml` files directly to `--file` for `definition`, `implementations`, or `references`.
   - References will include usages in Blazor component templates and `@code` blocks with snippet previews extracted directly from the Razor file.
   - Interface implementations (e.g. `@implements IDisposable` or `@implements IComponent`) are discovered automatically.