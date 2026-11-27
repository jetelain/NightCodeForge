# 🛡️ Step 12.40: Tool Output & File Read Binary Content Guard

> **Feature Implementation Step — Context Protection & Binary Guardrail**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.2D: Multimodal Vision & In-Loop Image Inspection](../archive/phase-12/STEP_12_2D_MULTIMODAL_VISION_AND_IN_LOOP_IMAGE_INSPECTION.md) | [Step 12.6: Smart CLI & Bash Output Filtering](../archive/phase-12/STEP_12_6_SMART_CLI_AND_BASH_OUTPUT_FILTERING.md) | [Step 12.22A: Diagnostic Shell Service](../archive/phase-12/STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md) | [Step 12.46A: Git Branch File Viewer](../archive/phase-12/STEP_12_46A_GIT_BRANCH_FILE_VIEWER.md) *(Completed — shared `BinaryContentDetector`)*  
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

## 2. Current Baseline & Reuse

Step 12.46A introduced a shared deterministic detector in [`BinaryContentDetector`](../../src/NightCodeForge.Core/Security/BinaryContentDetector.cs) (tested in [`BinaryContentDetectorTests`](../../tests/NightCodeForge.Core.Tests/BinaryContentDetectorTests.cs)), currently consumed by `GitRepositoryBrowserService` and `TelemetryProcessRunner`:
- **Already implemented in baseline:** `DefaultRawByteSampleSize` (8 KB), control-character and `U+FFFD` density thresholds/counts, `BinaryDetectionReason` / `BinaryContentInspectionResult`, `InspectBytes`, `IsBinary`, `InspectText`, `TryDecodeText`, `TryDecodeTextPreview`, BOM-aware UTF-32/UTF-16/UTF-8 decoding (checking UTF-32 before UTF-16), incomplete trailing UTF-8 sequence trimming, and CSI/OSC ANSI escape skipping.
- **Not yet implemented (Step 12.40 scope):** Extending `BinaryContentDetector` (rather than introducing a duplicate detector) for dilution-resistant inline-window inspection, truncation-aware bounded disk-prefix sampling, known non-image binary extensions, and centralized guard notice formatting; plus integrating the guard into `CliOutputFilterHelpers`, `SandboxBashToolModule`, and `WorkspaceFileToolModule`.

---

## 3. Required Behaviors & Constraints

### 3.1 Deterministic Binary Content Detection
1. **Reuse & Extend `BinaryContentDetector`:** Extend [`BinaryContentDetector`](../../src/NightCodeForge.Core/Security/BinaryContentDetector.cs) as the single source of truth for binary classification across tool outputs and file reads. Classify content as binary when inspecting the payload detects:
   - NUL bytes (`\0`) prior to any string sanitization (outside valid UTF-16/UTF-32 BOM-decoded text), **or**
   - A high density of non-printable ASCII control characters (excluding `\t`, `\r`, `\n`, and valid ANSI escape sequences — both CSI color/cursor codes and OSC sequences such as terminal hyperlinks — stripped before analysis), **or**
   - A high density of UTF-8 replacement characters (`U+FFFD`).
   - Note that host process execution (`SystemProcessRunner`) currently strips `\0` bytes before returning `CommandResult`, whereas container execution (`PersistentContainerShellSession`) preserves them; therefore `bash` binary detection must catch binary payloads via both NUL-byte presence (when preserved) and control-character / `U+FFFD` density heuristics.
2. **Inspection Windows, Dilution Resistance & Encoding Boundaries:**
   - **In-memory string payloads (`bash` output & CLI output formatting):** Inspection must evaluate the inlineable window (bounded up to `WorkflowDefaults.MaxToolInlineChars`) in a **dilution-resistant** manner (e.g., evaluating bounded segments/chunks inside the inlineable window in addition to overall window inspection). Binary content appearing after large leading text, inside `stderr`, or before a large trailing text block beyond `MaxToolInlineChars` must not be diluted below global density thresholds and slip into the inline prompt.
   - **Files on disk (`read`):** Inspection must read a bounded leading raw-byte prefix sample (using `BinaryContentDetector.DefaultRawByteSampleSize`) before calling `File.ReadAllLinesAsync`, avoiding full-file memory loads. The bounded sample inspection must **accurately signal whether the stream/file extends beyond the sampled prefix** (even when only the prefix bytes were read into a buffer of `DefaultRawByteSampleSize` bytes) so incomplete trailing multi-byte UTF-8 sequences and unaligned trailing UTF-16/UTF-32 code units split at the sample boundary are trimmed/aligned rather than producing spurious trailing `U+FFFD` characters.
3. **Zero False Positives on Valid Text:**
   - Empty or whitespace-only payloads (including 0-byte files without a known binary extension) must **never** be classified as binary.
   - Valid UTF-8 international text (accented Latin characters, CJK scripts, emojis, box-drawing characters, and ANSI-colored compiler/test logs) must **never** be misclassified as binary. Density and segmented-window thresholds must tolerate isolated stray replacement or control characters in otherwise valid text without triggering false positives.
   - Valid UTF-32 (`FF FE 00 00` / `00 00 FE FF`) and UTF-16 (`FF FE` / `FE FF`) text files with a recognized BOM (checking UTF-32 BOMs before UTF-16 BOMs so `FF FE 00 00` is not misidentified as UTF-16LE) must be decoded with the matching encoding and evaluated via the same control-character / `U+FFFD` text analysis, rather than being misclassified solely because raw bytes contain `0x00` or blindly trusted solely because a BOM prefix matches.
4. **No Magic Numbers:** All inspection sample sizes, chunk/window bounds, ratio thresholds, known binary extension lists, and diagnostic strings must be defined as named constants.

### 3.2 `bash` Tool Output Guard (`SandboxBashToolModule` & CLI Output Formatting)
1. **Execution Order & Non-Bypassable Safety Guardrail:**
   - Perform binary detection on the captured command output (inspecting `StandardOutput` and `StandardError` individually when present as well as the combined inlineable window) **before** emitting the non-zero exit-code warning log and **before** running semantic CLI output filters (so matching filters such as `dotnet test` never attempt to parse binary output).
   - Setting `disable_filter: true` on `bash` bypasses semantic CLI output compression, but **must not** disable or bypass the binary content guard.
   - Enforce the same binary guard inside `CliOutputFilterHelpers.FormatCommandOutput` (which also backs `CliOutputFilterHelpers.FilterPrimingOutput` for deterministic stage priming) as defense-in-depth **before** any `ICliOutputFilter` lookup and regardless of `enableSmartFilter`:
     - Preserve `prefix`, honor `maxInlineChars`, return `WasSmartFiltered = false` and `WasTruncated = true` even when the raw binary payload length is `<= maxInlineChars` (ensuring `FilterPrimingOutput` always substitutes the structured binary notice rather than returning raw binary output), and omit contradictory text-oriented footers (`use grep/rg/sed`) or duplicate `/tmp` offload references.
2. **Preserve Raw Offloading (and Handle Unavailable `/tmp`):**
   - When binary output is detected, continue writing the raw captured output to `/tmp/bash-...` when the `/tmp` offload mount is available so diagnostic workflows remain intact.
   - When `/tmp` is unavailable (`TrySaveRawOutputToTmp` returns `null`), still emit the structured binary notice with size and actionable hint without referencing a nonexistent `/tmp/...` path.
3. **Inline Notice Instead of Binary Bytes:** Replace the inline body with a concise, structured notice while preserving the leading `Exit Code: {exitCode}\n` prefix:
   - `[BINARY OUTPUT DETECTED: ... — omitted from inline context to protect token budget. Full output saved to /tmp/...]` (including the character/byte size, and the `/tmp` path when offloaded).
   - An actionable hint guiding the agent toward text-structured output (e.g., passing `-f cobertura` / `-f xml` / `--format json` to coverage/CLI tools, or inspecting with `file`, `strings`, or bounded `hexdump`).
4. **Telemetry, Audit & Structured Result Semantics:**
   - Log a warning when the binary guard triggers (recording command/output size without dumping binary payload into logs), and ensure non-zero exit-code warning logs also avoid dumping raw binary payload when binary output is detected.
   - In `BashExecutionAuditEntry`, accurately reflect `RawChars` (original captured length) and `FilteredChars` (bounded notice length), set `WasSmartFiltered = false` and `WasTruncated = true`, and do **not** persist raw binary payload in `RawOutput` (store a concise sanitized placeholder/notice instead so persisted turn metadata does not bloat the database or contain invalid `\0` characters).
   - In `BashToolExecutionResult`, set `IsTruncated = true` when the binary guard triggers, and whenever `StandardError` contains binary content (including on timeout paths), sanitize `BashToolExecutionResult.Error` with a concise notice so binary bytes do not leak to callers such as `EnvironmentDiagnosticShellService`.

### 3.3 `read` Tool Binary Guard (`WorkspaceFileToolModule`)
1. **Inspection Precedence Order:** In `WorkspaceFileToolModule.ReadAsync`, enforce checks in this exact order:
   1. Existing sandbox path resolution (`SandboxPathResolver.ResolveHostPath`), early non-vision image rejection (`!_supportsVision` for `AttachmentConstants.ImageExtensions` → `ModelVisionNotSupportedException`), attachment symlink validation (`ValidateAndResolveAttachmentReadPathAsync`), and resolved-path multimodal image handling (`ReadImageAsync`) continue **unchanged** and take precedence (Step 12.2D).
   2. Existing missing-file check (`!File.Exists(readPath)` → `"Error: File not found: '{path}'"`) runs **before** binary extension or byte inspection so missing binary-named paths (e.g., `missing.dll`) still report file-not-found rather than a misleading binary notice.
   3. Known non-image binary extension check (checking the resolved `readPath` extension with fallback to requested `path`, consistent with image extension resolution) and bounded leading raw-byte inspection (opening read-only with permissive file sharing such as `FileShare.ReadWrite | FileShare.Delete` and accurately flagging sample truncation) run **before** calling `File.ReadAllLinesAsync`, so large binary files (including extensionless binaries and `/tmp/bash-*.txt` offloaded binary captures) are rejected without loading the full file into memory.
   4. **Paginated Mixed-File Defense-in-Depth:** After `File.ReadAllLinesAsync` selects the requested line slice (within `_maxReadFileBytes`), inspect the selected slice with the text binary detector before formatting/returning so `offset` pagination past a clean text header into binary data in a mixed text+binary file (e.g., an offloaded `/tmp/bash-*.txt` capture from a combined command) is also blocked with the structured notice rather than returning raw binary lines.
2. **Known Binary Extensions & Byte-Level Detection Parity:**
   - The known non-image binary extension set must explicitly exclude `AttachmentConstants.ImageExtensions` and text-based formats (such as `.svg`, `.html`, `.htm`, `.xhtml`, `.xml`, `.json`), while covering common non-image binary formats (e.g., `.coverage`, `.dll`, `.exe`, `.pdb`, `.zip`, `.nupkg`, `.db`, `.sqlite`, `.pdf`, `.docx` — note that rich document attachments such as `.pdf` and `.docx` are read via their converted `.md` sidecars rather than raw binary bytes).
   - Bounded leading raw-byte inspection must detect **all** binary signals — BOM-aware `0x00` NUL bytes, non-printable ASCII control-character density, **and** UTF-8-encoded `U+FFFD` (`EF BF BD`) / replacement-character density after decoding — because `/tmp/bash-*.txt` captures offloaded after `SystemProcessRunner` have `.txt` extensions, zero `\0` bytes, and valid UTF-8 encoding containing `U+FFFD` sequences.
   - Empty (0-byte) files without a known binary extension pass through normal text handling without false positives.
3. **Structured Notice & Logging:**
   - Do **not** inline decoded binary bytes into the tool response and do **not** throw an unhandled exception for blocked binary files.
   - Return a concise structured notice (`[BINARY FILE DETECTED: ...]`) reporting the file path, file byte size, and detected binary format/extension or binary content signal, advising the agent to use appropriate inspection tools or structured text formats.
   - Log a concise warning when a binary file read is blocked (recording path, size, and detected format/signal without dumping file bytes).

---

## 4. Deliverables & Testing Checklist

- [ ] **Core Binary Guardrail Extensions (`NightCodeForge.Core`):**
  - [ ] Extend [`BinaryContentDetector`](../../src/NightCodeForge.Core/Security/BinaryContentDetector.cs) with dilution-resistant inline-window text inspection (up to `WorkflowDefaults.MaxToolInlineChars`), truncation-aware bounded file/stream prefix sampling (preserving UTF-8 multi-byte and UTF-16/UTF-32 code-unit boundary safety), known non-image binary extension classification, and centralized notice formatting constants.
  - [ ] Enforce binary output defense-in-depth inside `CliOutputFilterHelpers.FormatCommandOutput` (covering `FilterPrimingOutput` as well, ensuring `WasSmartFiltered = false` and `WasTruncated = true` even for short binary outputs `<= maxInlineChars`, without appending text-oriented footers).
- [ ] **Agent Tool Integration (`NightCodeForge.Agents`):**
  - [ ] Enforce binary output guard in `SandboxBashToolModule` prior to non-zero exit-code logging and semantic CLI filtering (non-bypassable even when `disable_filter: true`), inspecting both individual streams and combined inlineable output, preserving the `Exit Code:` prefix, preserving `/tmp` offloading when available (and handling unavailable `/tmp` cleanly), logging a warning without dumping binary payload, sanitizing `BashToolExecutionResult.Error` when `stderr` is binary, setting `IsTruncated = true`, and storing sanitized `BashExecutionAuditEntry.RawOutput` (`WasSmartFiltered = false`, `WasTruncated = true`).
  - [ ] Enforce binary file guard in `WorkspaceFileToolModule` (`read`) in strict precedence order (existing sandbox/early non-vision image check/symlinks/image read → missing-file check → known non-image binary extension & truncation-aware bounded raw-byte/control/`U+FFFD` inspection → `File.ReadAllLinesAsync` → defense-in-depth binary check on paginated line slices) and log a concise warning without dumping bytes.
- [ ] **Automated Testing (`NightCodeForge.Core.Tests` & `NightCodeForge.Agents.Tests`):**
  - [ ] Unit tests extending [`BinaryContentDetectorTests`](../../tests/NightCodeForge.Core.Tests/BinaryContentDetectorTests.cs) and CLI filter tests verifying binary detection on `.coverage`-style payloads, NUL-byte streams, control-character streams, `U+FFFD`-heavy streams, UTF-8-encoded `EF BF BD` byte prefixes, dilution-resistant detection of binary content after large leading text / inside `stderr` / before large trailing text, truncation-aware bounded file-prefix sampling with multi-byte UTF-8 and UTF-16 split across the sample boundary, and `CliOutputFilterHelpers.FormatCommandOutput` / `FilterPrimingOutput` (including short binary outputs `<= maxInlineChars`).
  - [ ] Unit tests verifying zero false positives on empty/whitespace payloads (including 0-byte files), UTF-32 and UTF-16 BOM text files (with and without binary corruption), multilingual UTF-8 text (Latin accents, CJK, emojis), isolated stray `U+FFFD` or control characters, `.svg` files, and ANSI-colored (CSI/OSC) build/test output.
  - [ ] Unit tests in `NightCodeForge.Agents.Tests` confirming `SandboxBashToolModule` preserves `Exit Code:`, omits binary output even with `disable_filter: true`, skips smart CLI filtering on binary output, preserves `/tmp` offload reference (and handles unavailable `/tmp`), sanitizes `BashToolExecutionResult.Error` and `BashExecutionAuditEntry` (`WasSmartFiltered = false`, `WasTruncated = true`), and avoids dumping binary payloads into logs; and `WorkspaceFileToolModule` preserves existing `ModelVisionNotSupportedException` precedence, returns file-not-found for missing `.dll` paths, blocks binary file reads (including extensionless binaries, `/tmp/bash-*.txt` offloaded binary captures without NUL bytes, and mixed text+binary files paged via `offset` past a clean 8 KB header) before reading/returning binary lines while preserving `.svg`, empty, normal text, and multimodal image handling.
  - [ ] Run fast tests during development (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) and the full test suite on completion per [AGENTS.md](../../AGENTS.md).
