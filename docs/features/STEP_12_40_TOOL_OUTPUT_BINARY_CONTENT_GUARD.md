# 🛡️ Step 12.40: Tool Output & File Read Binary Content Guard

> **Feature Implementation Step — Context Protection & Binary Guardrail**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.2D: Multimodal Vision & Context Compaction](../archive/phase-12/STEP_12_2D_MULTIMODAL_VISION_SUPPORT.md) | [Step 12.22A: Diagnostic Shell Service](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md)  
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
1. **Detection Signals:** Classify content as binary when inspecting the payload (or a bounded prefix sample up to a centralized threshold) detects:
   - NUL bytes (`\0`) prior to any string sanitization, **or**
   - A high density of non-printable ASCII control characters (excluding `\t`, `\r`, `\n`, and valid ANSI escape sequences stripped before analysis), **or**
   - A high density of UTF-8 replacement characters (`U+FFFD`).
2. **Zero False Positives on Valid Text:** Valid UTF-8 international text (accented Latin characters, CJK scripts, emojis, box-drawing characters, and ANSI-colored compiler/test logs) must **never** be misclassified as binary.
3. **No Magic Numbers:** All inspection sample sizes, ratio thresholds, and diagnostic strings must be defined as named constants.

### 2.2 `bash` Tool Output Guard (`SandboxBashToolModule`)
1. **Preserve Raw Offloading:** When binary output is detected, continue writing the raw captured output to `/tmp/bash-...` (when the `/tmp` offload mount is available) so diagnostic workflows remain intact.
2. **Inline Notice Instead of Binary Bytes:** Replace the inline body with a concise, structured notice containing:
   - `[BINARY OUTPUT DETECTED: ... — omitted from inline context to protect token budget. Full output saved to /tmp/...]` (including the character/byte size and `/tmp` path when offloaded).
   - An actionable hint guiding the agent toward text-structured output (e.g., passing `-f cobertura` / `-f xml` / `--format json` to coverage/CLI tools, or inspecting with `file`, `strings`, or bounded `hexdump`).
3. **Non-Bypassable Safety Guardrail:** Setting `disable_filter: true` on `bash` bypasses semantic CLI output compression, but **must not** disable or bypass the binary content guard.
4. **Telemetry & Logging:** Log a warning when the binary guard triggers (recording command/output size without dumping binary payload into logs) and accurately reflect the bounded output length in `BashExecutionAuditEntry`.

### 2.3 `read` Tool Binary Guard (`WorkspaceFileToolModule`)
1. **Reject / Replace Raw Binary Reads:** When `read` targets a non-image binary file (e.g., `.coverage`, `.dll`, `.exe`, `.pdb`, `.zip`, `.db`, `.sqlite`, or content detected as binary via byte/NUL/control-character inspection):
   - Do **not** inline decoded binary bytes into the tool response.
   - Return a concise structured notice reporting the file path, file size, and detected binary format/extension, advising the agent to use appropriate inspection tools or structured text formats.
2. **Preserve Multimodal Image Handling:** Supported image files handled by the existing multimodal vision pipeline (Step 12.2D) must continue working without regression.

---

## 3. Deliverables & Testing Checklist

- [ ] **Core Binary Guardrail Helper (`NightCodeForge.Core`):**
  - [ ] Add deterministic binary content detector and centralized threshold/notice constants.
- [ ] **Agent Tool Integration (`NightCodeForge.Agents`):**
  - [ ] Enforce binary output guard in `SandboxBashToolModule` (including when `disable_filter: true`), preserving `/tmp` offloading and logging a warning.
  - [ ] Enforce binary file guard in `WorkspaceFileToolModule` (`read`) without breaking multimodal image inspection.
- [ ] **Automated Testing (`NightCodeForge.Core.Tests` & `NightCodeForge.Agents.Tests`):**
  - [ ] Unit tests verifying binary detection on `.coverage`-style payloads, NUL-byte streams, control-character streams, and `U+FFFD`-heavy streams.
  - [ ] Unit tests verifying zero false positives on multilingual UTF-8 text (Latin accents, CJK, emojis) and ANSI-colored build/test output.
  - [ ] Unit tests in `NightCodeForge.Agents.Tests` confirming `SandboxBashToolModule` omits binary output even with `disable_filter: true`, preserves `/tmp` offload reference, and `WorkspaceFileToolModule` blocks binary file reads while preserving text and image handling.
