# 🛡️ Step 12.40: Tool Output & File Read Binary Content Guard

> **Feature Implementation Step — Context Protection & Binary Guardrail**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.2D: Multimodal Vision & In-Loop Image Inspection](../archive/phase-12/STEP_12_2D_MULTIMODAL_VISION_AND_IN_LOOP_IMAGE_INSPECTION.md) | [Step 12.22A: Diagnostic Shell Service](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective & Problem Statement

During autonomous execution (`ncf-task1-implementation`), an agent invoked `dotnet-coverage collect -- dotnet test --no-build` without specifying `-f cobertura` or `-f xml`, producing Microsoft's **binary** `output.coverage` file, and then ran `cat output.coverage`.

Because tool output truncation (`WorkflowDefaults.MaxToolInlineChars = 50 * 1024`) currently bounds inline content strictly by **character count**, 51,200 characters of decoded binary bytes and UTF-8 replacement characters (`U+FFFD`) were injected directly into the LLM prompt:
- Normal source code at 51,200 chars consumes ~12–15k tokens.
- Binary bytes and `U+FFFD` sequences tokenize via BPE byte-fallback at ~1.4–1.6+ tokens per character, spiking session context to **~80.9k tokens** in a single tool call and triggering 600s LLM request timeouts.

This step establishes a deterministic **Binary Content Guard** across both shell tool output (`bash`) and file reading (`read`) so raw binary payloads never enter LLM context.

---

## 2. Required Behaviors & Constraints

### 2.1 Deterministic Binary Content Detection
1. **Detection Signals:** Classify content as binary when inspecting the payload detects:
   - NUL bytes (`\0`) prior to any string sanitization (outside valid UTF-16/UTF-32 BOM-decoded text), **or**
   - A high density of non-printable ASCII control characters (excluding `\t`, `\r`, `\n`, and valid ANSI escape sequences — both CSI color/cursor codes and OSC sequences such as terminal hyperlinks — stripped before analysis), **or**
   - A high density of UTF-8 replacement characters (`U+FFFD`).
   - Note that host process execution (`SystemProcessRunner`) currently strips `\0` bytes before returning `CommandResult`, whereas container execution (`PersistentContainerShellSession`) preserves them; therefore `bash` binary detection must catch binary payloads via both NUL-byte presence (when preserved) and control-character / `U+FFFD` density heuristics.
2. **Inspection Windows & Encoding Boundaries:**
   - **In-memory string payloads (`bash` output & CLI output formatting):** Inspection must cover at least the entire inlineable window (up to `WorkflowDefaults.MaxToolInlineChars`). When `stdout` and `stderr` are combined, binary content appearing after leading text or inside `stderr` must not slip past a small prefix sample into the inline prompt.
   - **Files on disk (`read`):** Inspection must read a bounded leading raw-byte prefix sample (using a named constant, e.g., 8 KB) before calling `File.ReadAllLinesAsync`, avoiding full-file memory loads. When decoding bounded UTF-8 byte prefixes, trim or ignore any incomplete trailing multi-byte UTF-8 sequence split at the sample buffer boundary so valid multi-byte UTF-8 characters (CJK, emojis, accented Latin) do not produce spurious trailing `U+FFFD` characters.
3. **Zero False Positives on Valid Text:**
   - Empty or whitespace-only payloads (including 0-byte files without a known binary extension) must **never** be classified as binary.
   - Valid UTF-8 international text (accented Latin characters, CJK scripts, emojis, box-drawing characters, and ANSI-colored compiler/test logs) must **never** be misclassified as binary. Density thresholds must tolerate isolated stray replacement or control characters in otherwise valid text without triggering false positives.
   - Valid UTF-32 (`FF FE 00 00` / `00 00 FE FF`) and UTF-16 (`FF FE` / `FE FF`) text files with a recognized BOM (checking UTF-32 BOMs before UTF-16 BOMs so `FF FE 00 00` is not misidentified as UTF-16LE) must be decoded with the matching encoding and evaluated via the same control-character / `U+FFFD` text analysis, rather than being misclassified solely because raw bytes contain `0x00` or blindly trusted solely because a BOM prefix matches.
4. **No Magic Numbers:** All inspection sample sizes, ratio thresholds, known binary extension lists, and diagnostic strings must be defined as named constants.

### 2.2 `bash` Tool Output Guard (`SandboxBashToolModule` & CLI Output Formatting)
1. **Execution Order & Non-Bypassable Safety Guardrail:**
   - Perform binary detection on the captured command output **before** emitting the non-zero exit-code warning log and **before** running semantic CLI output filters (so matching filters such as `dotnet test` never attempt to parse binary output).
   - Setting `disable_filter: true` on `bash` bypasses semantic CLI output compression, but **must not** disable or bypass the binary content guard.
   - Enforce the same binary guard inside `CliOutputFilterHelpers.FormatCommandOutput` (which also backs `CliOutputFilterHelpers.FilterPrimingOutput` for deterministic stage priming) as defense-in-depth so binary payloads cannot enter LLM context via synthetic conversation priming either.
2. **Preserve Raw Offloading (and Handle Unavailable `/tmp`):**
   - When binary output is detected, continue writing the raw captured output to `/tmp/bash-...` when the `/tmp` offload mount is available so diagnostic workflows remain intact.
   - When `/tmp` is unavailable (`TrySaveRawOutputToTmp` returns `null`), still emit the structured binary notice with size and actionable hint without referencing a nonexistent `/tmp/...` path.
3. **Inline Notice Instead of Binary Bytes:** Replace the inline body with a concise, structured notice while preserving the leading `Exit Code: {exitCode}\n` prefix:
   - `[BINARY OUTPUT DETECTED: ... — omitted from inline context to protect token budget. Full output saved to /tmp/...]` (including the character/byte size, and the `/tmp` path when offloaded).
   - An actionable hint guiding the agent toward text-structured output (e.g., passing `-f cobertura` / `-f xml` / `--format json` to coverage/CLI tools, or inspecting with `file`, `strings`, or bounded `hexdump`).
4. **Telemetry, Audit & Structured Result Semantics:**
   - Log a warning when the binary guard triggers (recording command/output size without dumping binary payload into logs), and ensure non-zero exit-code warning logs also avoid dumping raw binary payload when binary output is detected.
   - In `BashExecutionAuditEntry`, accurately reflect `RawChars` (original captured length) and `FilteredChars` (bounded notice length), set `WasSmartFiltered = false` and `WasTruncated = true`, and do **not** persist raw binary payload in `RawOutput` (store a concise sanitized placeholder/notice instead so persisted turn metadata does not bloat the database or contain invalid `\0` characters).
   - In `BashToolExecutionResult`, set `IsTruncated = true` when the binary guard triggers, and if `StandardError` itself contains binary content, sanitize `BashToolExecutionResult.Error` with a concise notice so binary bytes do not leak to callers such as `EnvironmentDiagnosticShellService`.

### 2.3 `read` Tool Binary Guard (`WorkspaceFileToolModule`)
1. **Inspection Precedence Order:** In `WorkspaceFileToolModule.ReadAsync`, enforce checks in this exact order:
   1. Existing sandbox path resolution and attachment symlink validation (`ValidateAndResolveAttachmentReadPathAsync`).
   2. Existing multimodal image handling (`AttachmentConstants.ImageExtensions` $\rightarrow$ `ReadImageAsync` / `ModelVisionNotSupportedException`) continues unchanged and takes precedence (Step 12.2D).
   3. Existing missing-file check (`!File.Exists(readPath)` $\rightarrow$ `"Error: File not found: '{path}'"`) runs **before** binary extension or byte inspection so missing binary-named paths (e.g., `missing.dll`) still report file-not-found rather than a misleading binary notice.
   4. Known non-image binary extension check and bounded leading raw-byte inspection run **before** calling `File.ReadAllLinesAsync`, so large binary files (including extensionless binaries and `/tmp/bash-*.txt` offloaded binary captures) are rejected without loading the full file into memory.
2. **Known Binary Extensions & Byte-Level Detection Parity:**
   - The known non-image binary extension set must explicitly exclude `AttachmentConstants.ImageExtensions` and text-based formats (such as `.svg`, `.html`, `.htm`, `.xhtml`, `.xml`, `.json`), while covering common non-image binary formats (e.g., `.coverage`, `.dll`, `.exe`, `.pdb`, `.zip`, `.nupkg`, `.db`, `.sqlite`, `.pdf`, `.docx`).
   - Bounded leading raw-byte inspection must detect **all** binary signals — BOM-aware `0x00` NUL bytes, non-printable ASCII control-character density, **and** UTF-8-encoded `U+FFFD` (`EF BF BD`) / replacement-character density after decoding — because `/tmp/bash-*.txt` captures offloaded after `SystemProcessRunner` have `.txt` extensions, zero `\0` bytes, and valid UTF-8 encoding containing `U+FFFD` sequences.
   - Empty (0-byte) files without a known binary extension pass through normal text handling without false positives.
3. **Structured Notice & Logging:**
   - Do **not** inline decoded binary bytes into the tool response and do **not** throw an unhandled exception for blocked binary files.
   - Return a concise structured notice (`[BINARY FILE DETECTED: ...]`) reporting the file path, file byte size, and detected binary format/extension or binary content signal, advising the agent to use appropriate inspection tools or structured text formats.
   - Log a concise warning when a binary file read is blocked (recording path, size, and detected format/signal without dumping file bytes).

---

## 3. Deliverables & Testing Checklist

- [ ] **Core Binary Guardrail Helper (`NightCodeForge.Core`):**
  - [ ] Add deterministic binary content detector (handling NUL bytes, CSI/OSC ANSI stripping, UTF-32/UTF-16 BOM-aware decoding, UTF-8 multi-byte trailing-boundary trimming, stray `U+FFFD` tolerance, full inline-window scanning up to `WorkflowDefaults.MaxToolInlineChars`, and bounded leading raw-byte file sampling) along with centralized threshold, extension, and notice constants.
  - [ ] Enforce binary output defense-in-depth inside `CliOutputFilterHelpers.FormatCommandOutput` (covering `FilterPrimingOutput` as well).
- [ ] **Agent Tool Integration (`NightCodeForge.Agents`):**
  - [ ] Enforce binary output guard in `SandboxBashToolModule` prior to non-zero exit-code logging and semantic CLI filtering (non-bypassable even when `disable_filter: true`), preserving the `Exit Code:` prefix, preserving `/tmp` offloading when available (and handling unavailable `/tmp` cleanly), logging a warning without dumping binary payload, sanitizing `BashToolExecutionResult.Error` when `stderr` is binary, setting `IsTruncated = true`, and storing sanitized `BashExecutionAuditEntry.RawOutput` (`WasSmartFiltered = false`, `WasTruncated = true`).
  - [ ] Enforce binary file guard in `WorkspaceFileToolModule` (`read`) in strict precedence order (sandbox/symlinks $\rightarrow$ multimodal images $\rightarrow$ missing-file check $\rightarrow$ known non-image binary extension & bounded raw-byte/control/`U+FFFD` inspection $\rightarrow$ `File.ReadAllLinesAsync`) and log a concise warning without dumping bytes.
- [ ] **Automated Testing (`NightCodeForge.Core.Tests` & `NightCodeForge.Agents.Tests`):**
  - [ ] Unit tests verifying binary detection on `.coverage`-style payloads, NUL-byte streams, control-character streams, `U+FFFD`-heavy streams, UTF-8-encoded `EF BF BD` byte prefixes, binary content after leading text / in `stderr`, and `CliOutputFilterHelpers.FormatCommandOutput` / `FilterPrimingOutput`.
  - [ ] Unit tests verifying zero false positives on empty/whitespace payloads (including 0-byte files), UTF-32 and UTF-16 BOM text files (with and without binary corruption), multilingual UTF-8 text (Latin accents, CJK, emojis — including multi-byte sequences split across the sample buffer boundary), isolated stray `U+FFFD`, `.svg` files, and ANSI-colored (CSI/OSC) build/test output.
  - [ ] Unit tests in `NightCodeForge.Agents.Tests` confirming `SandboxBashToolModule` preserves `Exit Code:`, omits binary output even with `disable_filter: true`, skips smart CLI filtering on binary output, preserves `/tmp` offload reference (and handles unavailable `/tmp`), sanitizes `BashToolExecutionResult.Error` and `BashExecutionAuditEntry` (`WasSmartFiltered = false`, `WasTruncated = true`), and avoids dumping binary payloads into logs; and `WorkspaceFileToolModule` returns file-not-found for missing `.dll` paths, blocks binary file reads (including extensionless binaries and `/tmp/bash-*.txt` offloaded binary captures without NUL bytes) before reading all lines while preserving `.svg`, empty, normal text, and multimodal image handling.
  - [ ] Run fast tests during development (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) and the full test suite on completion per [AGENTS.md](../../AGENTS.md).
