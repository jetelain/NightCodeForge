# 🛠️ Step 9.19: Canonical Tool Signature Leniency & Edit Tool Robustness

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Agents`, `src/NightCodeForge.Core`, `tests/NightCodeForge.Agents.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Eliminate Casing & Shortcut Traps in Canonical Tools:** Ensure canonical tools exposed to LLM agents through [CanonicalAIFunctionFactory](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) leniently and transparently accept both `camelCase` and `snake_case` parameter representations.
2. **Resolve the `edit` Tool Conflict:** Resolve the structural discrepancy between array-based chunk edits (`edits: [{ oldText, newText }]` using camelCase) and single-edit shortcuts (`old_text`, `new_text` using snake_case). Allow models to invoke single replacements via either `oldText`/`newText` or `old_text`/`new_text` without dropped-argument runtime errors.
3. **Harmonize Schema Descriptions & Error Feedback:** Align error messages and parameter descriptions so agents are never misled into guessing conflicting parameter keys when an invocation payload is malformed.
4. **Harden Multi-Casing across All Canonical Tools:** Verify and normalize parameter binding across all canonical tool delegates ([read](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L21-L27), [write](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L29-L34), [edit](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L36-L56), [bash](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L58-L64), [load_skill](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L66-L70), [ask](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L72-L77), [propose_tasks](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L79-L86), [request_changes](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L88-L92), [web_search](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L97-L102)).

---

## 2. Problem Statement & Incident Analysis

### 2.1 Production Incident
During an autonomous run of the `task-review` stage, the agent attempted to edit a task specification file using the single-edit shortcut:

```json
{
  "path": "docs/tasks/2026-10-18-add-multiply-int-a-int-b-to-calculator-with-xml-docs-and-unit-tests.md",
  "oldText": "## Open Questions\n- None blocking...",
  "newText": "## Open Questions\n- None blocking...\n\n## Approved Technical Architecture..."
}
```

The tool invocation failed immediately with:
```
Error: provide 'edits' (array of { oldText, newText }) or both 'old_text' and 'new_text'.
```

The agent responded in confusion:
> *"The parameter name is wrong. Use the shortcut form of old_text/new_text — it says to use the edits array, but the schema shows properties old_text and new_text for the single-edit shortcut... Actually, the error says to use edits or both old_text and new_text. I used oldText/newText. Let me retry with edits."*

### 2.2 Root Cause Analysis
1. **Dissonance in Casing Standards:**
   - [EditChunk](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalTools.cs#L12) is defined in C# as `public record EditChunk(string OldText, string NewText);`. Under default System.Text.Json reflection conventions, its properties map to camelCase: `oldText` and `newText`.
   - The parameter description for `edits` explicitly states: `"One or more disjoint edits applied in order; each oldText must match the file exactly once"`.
   - However, the single-edit shortcut parameters on the delegate in [CanonicalAIFunctionFactory](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L39-L40) were named `old_text` and `new_text` (snake_case).
2. **Strict MAF Parameter Reflection:**
   - In Microsoft Agent Framework (`AIFunctionFactory.Create`), tool arguments are bound by exact parameter name matching against the C# delegate signature.
   - When the LLM flattens `{ oldText, newText }` to the root arguments object, MAF does not bind them to `old_text` and `new_text`. Both parameters remain `null`.
   - The validation check `if (old_text != null && new_text != null)` evaluates to `false`, triggering the tool error.
3. **Bypassing LenientToolDispatcher:**
   - While [LenientToolDispatcher](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs#L380-L402) contains normalization logic for aliases (`oldText`, `old_text`, `old_str`, `search`), canonical tools in [CrucibleAgentHarness.BuildAIFunctionList](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L1198) are created directly via `CanonicalAIFunctionFactory.CreateTools()`, bypassing dispatcher argument normalization.

---

## 3. Behavioral Specifications

### 3.1 `edit` Tool Signature & Argument Resolution
1. **Dual-Casing Parameter Acceptance:**
   The `edit` tool delegate in [CanonicalAIFunctionFactory](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) must accept both camelCase (`oldText`, `newText`) and snake_case (`old_text`, `new_text`) parameters:
   - Effective old text resolved as: `oldText ?? old_text`
   - Effective new text resolved as: `newText ?? new_text`
2. **Chunk Ingestion Leniency:**
   If `edits` is provided as an array of objects, the tool must accept chunks whether individual items provide `{ oldText, newText }` or `{ old_text, new_text }`.
3. **Deterministic Precedence:**
   - If `edits` contains one or more non-empty items, execute chunk edits via `tools.EditAsync(path, edits, ct)`.
   - Else if effective old text and effective new text are non-null, execute single edit via `tools.EditAsync(path, effectiveOld, effectiveNew, ct)`.
   - Else, return a clear, unambiguous error string stating:
     `"Error: provide 'edits' (array of { oldText, newText }) or single-edit replacement ('oldText'/'newText' or 'old_text'/'new_text')."`
4. **Description Polish:**
   Update the descriptions of `oldText` / `old_text` and `newText` / `new_text` to clearly indicate they are aliases for single text replacement.

### 3.2 Systemic Audit of Canonical Tools
Audit and apply dual-casing support to all canonical tools in [CanonicalAIFunctionFactory](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs):

| Tool | Parameters | Required Support |
| :--- | :--- | :--- |
| `read` | `path`, `offset`, `limit` | Accept `offset` / `start_line` / `startLine`, `limit` / `end_line` / `endLine` |
| `write` | `path`, `content` | Accept `path` / `file_path` / `filePath`, `content` / `text` |
| `edit` | `path`, `edits`, `old_text`, `new_text`, `oldText`, `newText` | Accept `oldText` $\leftrightarrow$ `old_text`, `newText` $\leftrightarrow$ `new_text` |
| `bash` | `command`, `timeout`, `working_directory`, `workingDirectory` | Accept `working_directory` $\leftrightarrow$ `workingDirectory` |
| `load_skill` | `skill_name`, `skillName` | Accept `skill_name` $\leftrightarrow$ `skillName` |
| `ask` | `questions` | Structured list per Step 9.17 |
| `propose_tasks` | `tasks`, `title`, `description`, `priority` | Retain flexible object mapping |
| `request_changes` | `feedback`, `reason` | Accept `feedback`, `reason` |
| `web_search` | `query`, `max_results`, `maxResults` | Accept `max_results` $\leftrightarrow$ `maxResults` |

### 3.3 LenientToolDispatcher Alignment
Ensure [LenientToolDispatcher](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs) argument extraction and alias dictionaries remain fully aligned with these canonical mappings so non-canonical invocations and MCP proxy bridges behave identically.

---

## 4. Implementation Checklist & Deliverables

- [ ] **CanonicalAIFunctionFactory:**
  - [ ] Add `oldText` and `newText` shortcut parameters alongside `old_text` and `new_text` in the `edit` tool delegate.
  - [ ] Support `old_text` / `new_text` key variants inside `EditChunk` deserialization if passed as raw dictionaries.
  - [ ] Add `workingDirectory` alias to `bash` tool.
  - [ ] Add `skillName` alias to `load_skill` tool.
  - [ ] Add `maxResults` alias to `web_search` tool.
  - [ ] Unify and clarify error feedback message when `edit` arguments are missing.
- [ ] **LenientToolDispatcher:**
  - [ ] Ensure `ExtractEdits` gracefully handles both camelCase and snake_case properties within JSON arrays and dictionary objects.
- [ ] **Automated Tests:**
  - [ ] Test `edit` tool with `{ path, oldText, newText }` (camelCase shortcut).
  - [ ] Test `edit` tool with `{ path, old_text, new_text }` (snake_case shortcut).
  - [ ] Test `edit` tool with `{ path, edits: [{ oldText, newText }] }`.
  - [ ] Test `edit` tool with `{ path, edits: [{ old_text, new_text }] }`.
  - [ ] Test `edit` tool missing all edit parameters returns unified, non-confusing error message.
  - [ ] Test `bash` tool with `workingDirectory` camelCase parameter.
  - [ ] Test `load_skill` tool with `skillName` camelCase parameter.
  - [ ] Run full test suite: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
