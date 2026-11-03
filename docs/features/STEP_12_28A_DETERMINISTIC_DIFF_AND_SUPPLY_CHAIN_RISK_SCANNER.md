# 🛡️ Step 12.28A: Deterministic Diff, Payload, Supply-Chain & Blast-Radius Risk Scanner (`IDiffRiskScanner`)

> **Feature Implementation Step (Part 1 of 3 — `Pmad.AI.Guardrails.Diff` Deterministic Scanner Integration & NCF Domain Wiring)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Upstream Library Spec:* [`Pmad.AI` Diff Security Guardrails Specification (`Pmad.AI.Guardrails.Diff`)](PMAD_AI_DIFF_SECURITY_GUARDRAILS_SPEC.md)  
> *Companion Steps:* [Step 12.28B: Cross-Model LLM-as-a-Judge Backdoor Scanner & Post-Review Guardrail Stage](STEP_12_28B_LLM_JUDGE_BACKDOOR_SCANNER_AND_POST_REVIEW_GUARDRAIL.md), [Step 12.28C: Review Room Risk Report UI & Security Guardrail Settings](STEP_12_28C_REVIEW_ROOM_RISK_REPORT_AND_GUARDRAIL_SETTINGS_UI.md)  
> *Reference POC:* [`C:\Workspace\POC\GitDiffDeterministicScanner`](file:///C:/Workspace/POC/GitDiffDeterministicScanner)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Agents`](../../src/NightCodeForge.Agents), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective & Threat Model

Recent empirical studies show that open-weight coding models (including Qwen 3.5+ and similar frontier open-weight families) can introduce subtle backdoors, covert payloads, or risky supply-chain changes under rare conditions.

Unlike external PR scanners that focus on untrusted human prompt injections, this guardrail focuses strictly on **model-introduced risks** produced during autonomous task execution:
1. **Network & Authentication Backdoor Indicators:** Hard-coded public IP addresses, hard-coded URLs outside a configurable well-known safe domain list, and hard-coded credential equality checks (`password == "..."`).
2. **Suspicious Payloads & Obfuscation:** Hard-coded secrets/API keys, high Shannon entropy literals, Base64 or Hex encoded hidden payloads, Trojan Source Unicode BiDi/invisible/homoglyph obfuscation, and newly added/modified binary files (except verified valid image files).
3. **Supply-Chain Risks (Typosquatting & Downgrades):** Newly added external library dependencies not present on a configurable well-known safe package allowlist, and any downgrade of an existing dependency version ($V_{\text{new}} < V_{\text{old}}$).
4. **Excessive Blast Radius (Absolute + Relative):** Changes that impact more than $N\%$ of the repository's codebase, affect more than $M$ files, or modify more than $X$ lines of code.

> [!IMPORTANT]
> **Scope Exclusion:** Prompt injection detection rules (`PI001`–`PI013` from the POC) are intentionally **excluded**. All deterministic analyzers in this step target code, binaries, dependencies, and diff scope produced by the coding agent.

---

## 2. Architecture & Technical Specification

### 2.1 Domain Models & Configuration (`SecurityGuardrailOptions`)

Define strongly-typed configuration and finding models in `NightCodeForge.Core`:

1. **`SecurityRiskCategory` Enum:**
   - `BackdoorPattern` (LLM-as-a-Judge findings in Step 12.28B, hardcoded auth backdoor comparisons, dangerous runtime code loading)
   - `HardcodedNetworkEndpoint` (hardcoded public IPv4/IPv6 addresses, non-allowlisted URLs, known exfiltration/tunneling endpoints)
   - `SuspiciousPayload` (secrets/tokens, high Shannon entropy strings, Base64/Hex hidden payloads, Unicode Trojan Source/homoglyphs, non-image binary files)
   - `UnverifiedDependency` (new external package dependency not matching the well-known safe package list)
   - `DependencyDowngrade` (package version downgraded to a lower version)
   - `ExcessiveBlastRadius` (diff exceeds relative $N\%$ codebase impact, absolute $M$ files, or absolute $X$ changed lines)

2. **`SecurityRiskSeverity` Enum:**
   - `Low = 0`, `Medium = 1`, `High = 2`, `Critical = 3`

3. **`SecurityRiskFinding` Record:**
   - `string RuleId` (e.g., `NET-IP-001`, `NET-URL-001`, `PAY-B64-001`, `PAY-BIN-001`, `DEP-NEW-001`, `DEP-DOWN-001`, `SCOPE-PCT-001`, `LLM-JUDGE-001`)
   - `string Title`
   - `SecurityRiskCategory Category`
   - `SecurityRiskSeverity Severity`
   - `string? FilePath` (workspace-relative path)
   - `int? LineNumber`
   - `string Description`
   - `string? Snippet`

4. **`SecurityGuardrailOptions` (Configurable Thresholds & Well-Known Lists):**
   - **Master & Judge Settings (used with Step 12.28B):**
     - `bool Enabled { get; set; } = true;`
     - `bool EnableLlmJudge { get; set; } = true;`
     - `List<string> PreferredJudgeModels { get; set; } = ["jetelain/Flint", "jetelain/Gemma-4-26B-A4B"];`
     - `string? ExplicitJudgeModelId { get; set; }`
     - `int MaxJudgeChunkBytes { get; set; } = 120_000;`
   - **Blast Radius Thresholds (Absolute + Relative):**
     - `double MaxCodebaseImpactPercent { get; set; } = 10.0;` ($N\%$: max percentage of repository lines of code modified)
     - `double MaxFileImpactPercent { get; set; } = 15.0;` (max percentage of repository tracked files modified)
     - `int MaxChangedFiles { get; set; } = 25;` ($M$: max number of modified/added/deleted files)
     - `int MaxChangedLines { get; set; } = 1_500;` ($X$: max total `AddedLines + DeletedLines`)
   - **Payload, Entropy & Obfuscation Thresholds:**
     - `double ShannonEntropyThreshold { get; set; } = 4.50;`
     - `int EntropyMinStringLength { get; set; } = 20;`
     - `int MinSuspiciousBase64Length { get; set; } = 64;`
     - `int MinSuspiciousHexLength { get; set; } = 64;`
     - `int MaxLineLength { get; set; } = 500;`
   - **Configurable Well-Known Lists:**
     - `List<string> WellKnownSafeDomains`: Domains and wildcard patterns (`*.domain.tld`) exempt from hardcoded URL warnings. Default seeded list includes:
       - Local/loopback: `localhost`, `127.0.0.1`, `[::1]`
       - Standards & schemas: `schemas.microsoft.com`, `*.w3.org`, `www.w3.org`, `json-schema.org`, `tempuri.org`, `example.com`, `*.example.com`
       - Package & developer ecosystems: `github.com`, `*.github.com`, `*.githubusercontent.com`, `nuget.org`, `*.nuget.org`, `npmjs.org`, `*.npmjs.org`, `pypi.org`, `*.pypi.org`, `crates.io`, `*.crates.io`, `pkg.go.dev`, `ollama.com`, `*.ollama.com`, `microsoft.com`, `*.microsoft.com`, `dotnet.microsoft.com`
     - `List<string>AllowedIpAddresses`: IPv4/IPv6 addresses or CIDR ranges explicitly permitted in addition to standard loopback/RFC 5737 documentation addresses (`127.0.0.1`, `0.0.0.0`, `::1`).
     - `List<string> WellKnownSafePackages`: Package names and wildcard prefixes (`Prefix.*`) assumed safe across ecosystems. Default seeded list includes:
       - .NET: `Microsoft.*`, `System.*`, `Azure.*`, `xunit*`, `Moq`, `bunit*`, `FluentAssertions`, `CliWrap`, `DiffPlex`, `Hangfire*`, `OllamaSharp`, `StreamJsonRpc`, `Pmad.*`, `ModelContextProtocol*`, `OpenAI`
       - JS/TS / Python / Common: `playwright`, `@playwright/*`, `pytest`, `requests`, `pydantic`, `numpy`, `scipy`
     - `List<string> AllowedImageExtensions`: `[".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico"]`
     - `List<string> ExcludedFileGlobs`: `[".scratch/**", "docs/tasks/**", "docs/transcripts/**"]`

---

### 2.2 Analyzer 1: Network Anomalies & Backdoor Signatures

Scans added lines (`+`) in the unified Git diff (parsed via `DiffParser` from the POC):

1. **Hard-Coded Public IP Detection (`NET-IP-001`):**
   - Extracts IPv4 and IPv6 literals from added lines (both inside URLs like `http://93.184.216.34:8080/api` and standalone string literals like `"93.184.216.34"` or `IPAddress.Parse("...")`).
   - Uses `System.Net.IPAddress.TryParse` to classify the IP:
     - **Exempt:** Loopback (`127.0.0.0/8`, `::1`), unspecified (`0.0.0.0`, `::`), RFC 5737 documentation blocks (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`), and entries matching `AllowedIpAddresses`.
     - **Flagged (`Critical`):** Cloud instance metadata endpoint `169.254.169.254` (SSRF / credential theft target).
     - **Flagged (`High`):** Any globally routable public IPv4 or IPv6 address (and configurable option `FlagPrivateNetworkIps` for RFC 1918 `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` when not in test files).
2. **Hard-Coded URL Detection Outside Well-Known Domains (`NET-URL-001`, `NET-URL-002`):**
   - Extracts `http://`, `https://`, `ws://`, and `wss://` URLs from added lines.
   - Parses the URI host and checks it against:
     - Known exfiltration/tunneling services (`webhook.site`, `ngrok.io`, `ngrok-free.app`, `pipedream.net`, `requestbin.com`, `burpcollaborator.net`, `interact.sh`, `oastify.com`) $\rightarrow$ `SecurityRiskSeverity.Critical` (`NET-URL-002`).
     - `WellKnownSafeDomains` (exact case-insensitive match or `*.suffix` subdomain match) $\rightarrow$ Permitted.
     - Any other domain $\rightarrow$ `SecurityRiskSeverity.High` (`NET-URL-001`: *"Hard-coded URL to non-allowlisted domain '{host}'"*).
3. **Deterministic Backdoor & Dynamic Execution Signatures (`BD-AUTH-001`, `BD-EXEC-001`):**
   - Flags hardcoded password/token equality comparisons (`password == "..."`, `secret_key == "..."`, `auth_token == "..."`) in non-test production files (`BD-AUTH-001`, `High`).
   - Flags runtime binary assembly/code loading (`Assembly.Load(byte[])`, `Assembly.UnsafeLoadFrom`, `eval(`, `powershell -enc`) in added lines (`BD-EXEC-001`, `High`).

---

### 2.3 Analyzer 2: Suspicious Payloads, Secrets, Base64/Hex & Non-Image Binaries

1. **Hard-Coded Secrets & High Shannon Entropy (`PAY-SEC-001`, `PAY-ENT-001`):**
   - Matches known credential/token formats (`AKIA[0-9A-Z]{16}`, `ghp_[a-zA-Z0-9]{36}`, `github_pat_...`, `sk-[a-zA-Z0-9]{32,}`, `xox[baprs]-...`, PEM private key headers `-----BEGIN .* PRIVATE KEY-----`, hardcoded `api_key`/`secret_key`/`private_key` assignments).
   - Computes Shannon entropy $H(X) = -\sum P(x)\log_2 P(x)$ on quoted string literals with length $\ge \text{EntropyMinStringLength}$ (`20`). Flags literals with $H \ge \text{ShannonEntropyThreshold}$ (`4.50` bits/char) that are not benign URLs, file paths, or GUIDs.
2. **Base64 & Hex Hidden Payloads (`PAY-B64-001`, `PAY-HEX-001`):**
   - **Base64 Payloads (`PAY-B64-001`):**
     - Flags explicit Base64 decoding calls in added code (`Convert.FromBase64String`, `atob(`, `base64.b64decode`, `Buffer.from(..., 'base64')`) or Base64 string literals of length $\ge \text{MinSuspiciousBase64Length}$ (`64` chars, excluding valid `data:image/(png|jpeg|gif|webp);base64,...` URIs whose decoded header is a valid image).
   - **Hex Payloads (`PAY-HEX-001`):**
     - Flags explicit hex decoding (`Convert.FromHexString`, `Buffer.from(..., 'hex')`, `bytes.fromhex`), contiguous hex string literals `[0-9a-fA-F]{64,}` (excluding standard 40-char SHA-1 / 64-char SHA-256 commit/checksum constants when labeled as hashes), escaped hex sequences (`(\x[0-9a-fA-F]{2}){16,}`), and dense byte-array initializers (`new byte[] { 0x4D, 0x5A, ... }` $\ge 16$ hex bytes).
3. **Unicode Obfuscation & Trojan Source (`PAY-UNI-001`..`004`):**
   - Incorporates `ObfuscationScanner` from the POC:
     - BiDi directional overrides (`U+202A`–`U+202E`, `U+2066`–`U+2069`, CVE-2021-42574 Trojan Source) $\rightarrow$ `Critical`.
     - Zero-width / invisible characters (`U+200B`, `U+200C`, `U+200D`, `U+2060`, mid-line `U+FEFF`) $\rightarrow$ `High`.
     - Cyrillic confusable homoglyphs mixed into ASCII/Latin identifiers $\rightarrow$ `High`.
     - Added lines exceeding `MaxLineLength` (`500` chars) $\rightarrow$ `Medium`.
4. **Binary Files Except Valid Images (`PAY-BIN-001`, `PAY-BIN-002`):**
   - Inspects all added/modified files in the diff for:
     - Git binary diff markers (`Binary files a/... and b/... differ` or `GIT binary patch`), OR
     - Binary/executable/archive extensions (`.dll`, `.exe`, `.so`, `.dylib`, `.bin`, `.wasm`, `.pyc`, `.class`, `.jar`, `.war`, `.zip`, `.tar`, `.gz`, `.7z`, `.nupkg`), OR
     - Null bytes (`\0`) in the first 8 KB of the file on disk in the worktree.
   - **Valid Image Exemption & Magic-Byte Verification:**
     - If a binary file's extension is in `AllowedImageExtensions` (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.ico`), the scanner reads the file header from the worktree and verifies that the **magic bytes** match the declared image format:
       - `.png`: `89 50 4E 47 0D 0A 1A 0A`
       - `.jpg` / `.jpeg`: `FF D8 FF`
       - `.gif`: `47 49 46 38 37 61` (`GIF87a`) or `47 49 46 38 39 61` (`GIF89a`)
       - `.webp`: `52 49 46 46` (`RIFF`) at offset 0 and `57 45 42 50` (`WEBP`) at offset 8
       - `.ico`: `00 00 01 00`
     - Furthermore, it checks that the file does not contain embedded executable headers (`MZ` / PE, `\x7FELF`, `PK\x03\x04` ZIP polyglot).
     - Any non-image binary file (`PAY-BIN-001`, `High`) or disguised binary with mismatched image magic bytes (`PAY-BIN-002`, `Critical`) is flagged.

---

### 2.4 Analyzer 3: Supply-Chain Guardrails (New External Dependencies & Version Downgrades)

Inspects diffs of dependency manifest files (`*.csproj`, `Directory.Packages.props`, `Directory.Build.props`, `packages.config`, `package.json`, `requirements.txt`, `pyproject.toml`, `Cargo.toml`, `go.mod`):

1. **Manifest Diff Extraction:**
   - Extracts all removed (`-`) package declarations `(PackageId, OldVersion)` and all added (`+`) package declarations `(PackageId, NewVersion)` per manifest file:
     - MSBuild (`.csproj`, `.props`): `<PackageReference Include="..." Version="..." />` and `<PackageVersion Include="..." Version="..." />` (including child `<Version>...</Version>` elements).
     - `package.json`: entries inside `"dependencies"`, `"devDependencies"`, `"peerDependencies"`, `"optionalDependencies"`.
     - `requirements.txt` / `pyproject.toml` / `Cargo.toml` / `go.mod`: package name and version specifiers.
2. **New External Dependency Detection (`DEP-NEW-001`):**
   - A package `PackageId` is considered **newly added** if it appears in the added (`+`) declarations of the manifest diff and was **not** present in the removed (`-`) declarations (nor already declared elsewhere in the repository before the diff).
   - Checks `PackageId` against `WellKnownSafePackages` (case-insensitive exact match or wildcard prefix match such as `Microsoft.*`, `System.*`, `Pmad.*`).
   - If `PackageId` does not match `WellKnownSafePackages`, emits a `SecurityRiskFinding` (`DEP-NEW-001`, `Category = UnverifiedDependency`, `Severity = High`):
     - *"New external dependency '{PackageId}' ({NewVersion}) is not in the well-known safe package list. Verify package authenticity to prevent typosquatting or supply-chain attacks."*
3. **Dependency Version Downgrade Detection (`DEP-DOWN-001`):**
   - When a package `PackageId` appears in both removed (`-`) with `OldVersion` and added (`+`) with `NewVersion` (i.e. a version modification):
   - Normalizes and parses both version strings (stripping leading `v`, `^`, `~`, `>=`, `==`) using a semantic version comparator (` Major.Minor.Patch[-Prerelease]`).
   - If $V_{\text{new}} < V_{\text{old}}$, emits a `SecurityRiskFinding` (`DEP-DOWN-001`, `Category = DependencyDowngrade`, `Severity = High`):
     - *"Dependency '{PackageId}' was downgraded from version '{OldVersion}' to '{NewVersion}'. Downgrading dependencies can reintroduce known CVEs."*

---

### 2.5 Analyzer 4: Excessive Blast Radius (Too Large Change — Absolute & Relative)

Evaluates the overall diff scope (excluding files matching `ExcludedFileGlobs` such as `.scratch/**`, `docs/tasks/**`, `docs/transcripts/**`):

1. **Metrics Computed:**
   - `ChangedFilesCount`: Total non-excluded files added, modified, or deleted in the task diff.
   - `AddedLinesCount` and `DeletedLinesCount`: Total added (`+`) and deleted (`-`) lines across non-excluded files (`TotalChangedLines = AddedLinesCount + DeletedLinesCount`).
   - `TotalRepoFiles` and `TotalRepoLines`: Total tracked non-excluded source files and lines in the repository worktree (computed efficiently on the worktree).
   - `CodebaseImpactPercent`: $\frac{\text{TotalChangedLines}}{\max(1, \text{TotalRepoLines})} \times 100$.
   - `FileImpactPercent`: $\frac{\text{ChangedFilesCount}}{\max(1, \text{TotalRepoFiles})} \times 100$.
2. **Threshold Checks:**
   - **Relative Codebase Impact (`SCOPE-PCT-001`):** If `CodebaseImpactPercent > options.MaxCodebaseImpactPercent` (default `10.0%`), emit finding (`Category = ExcessiveBlastRadius`, `Severity = Medium`).
   - **Relative File Count Impact (`SCOPE-FILEPCT-001`):** If `FileImpactPercent > options.MaxFileImpactPercent` (default `15.0%`), emit finding (`Category = ExcessiveBlastRadius`, `Severity = Medium`).
   - **Absolute File Count (`SCOPE-FILES-001`):** If `ChangedFilesCount > options.MaxChangedFiles` (default `25`), emit finding (`Category = ExcessiveBlastRadius`, `Severity = Medium`).
   - **Absolute Line Count (`SCOPE-LINES-001`):** If `TotalChangedLines > options.MaxChangedLines` (default `1,500`), emit finding (`Category = ExcessiveBlastRadius`, `Severity = Medium`).

---

## 3. Definition of Done (DoD) Checklist

- [ ] Implement and publish `Pmad.AI.Guardrails.Diff` (`DeterministicDiffRiskScanner`, `DiffParser`, `DiffSplitter`, `NetworkAndBackdoorScanner`, `EntropyAndSecretScanner`, `EncodedPayloadScanner`, `ObfuscationScanner`, `BinaryAndImageScanner`, `DependencySupplyChainScanner`, and `BlastRadiusScanner`) in `Pmad.AI` per [`PMAD_AI_DIFF_SECURITY_GUARDRAILS_SPEC.md`](PMAD_AI_DIFF_SECURITY_GUARDRAILS_SPEC.md), and reference the updated `Pmad.AI` package in `NightCodeForge.Agents`.
- [ ] Define `SecurityRiskCategory`, `SecurityRiskSeverity`, `SecurityRiskFinding`, `DeterministicRiskScanResult`, and `SecurityGuardrailOptions` in `NightCodeForge.Core` and implement `IDiffRiskScanner` in `NightCodeForge.Agents` delegating to `Pmad.AI.Guardrails.Diff.DeterministicDiffRiskScanner` (with `EnablePromptInjectionRules = false`, worktree file header inspection for image magic-byte validation, and repository baseline line/file counts for relative $N\%$ impact calculation).
- [ ] Write integration and unit tests in `tests/NightCodeForge.Agents.Tests` and `tests/NightCodeForge.Core.Tests` verifying NCF configuration mapping, allowlist overrides, magic-byte image verification against worktree files, and blast-radius calculation.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

