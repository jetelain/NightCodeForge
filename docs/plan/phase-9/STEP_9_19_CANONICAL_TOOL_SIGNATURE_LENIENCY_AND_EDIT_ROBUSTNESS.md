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

### 3.1 Consistent Naming Scheme with Dynamic Leniency
1. **Single, Clean Canonical Signatures:**
   Canonical tool delegates maintain a single, clean, consistent `snake_case` naming scheme (e.g. `path`, `edits`, `old_text`, `new_text`, `working_directory`, `skill_name`, `max_results`). Delegate parameters are NOT duplicated to avoid bloating the tool schema exposed to the LLM.
2. **Simplified `EditChunk` Model:**
   Instead of polluting the domain model with duplicate properties (`old_text`, `new_text`, `oldText`, `newText`) and complex deserialization constructors, `EditChunk` remains an idiomatic, clean C# positional record:
   ```csharp
   public record EditChunk(string OldText, string NewText);
   ```
   No serialization aliases or synthetic attributes reside on the record itself, keeping the reflection schema minimal and clean.
3. **Centralized Leniency via LenientToolDispatcher:**
   All leniency, alias mapping, and payload shape conversions are centralized exclusively in [LenientToolDispatcher](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs). When an LLM invokes tools using camelCase (`oldText`, `newText`, `workingDirectory`, `skillName`, `maxResults`), legacy names (`search`, `replace`, `filepath`, `code`), alternative line slicing (`start_line`, `end_line`), or JSON array string payloads, the dispatcher normalizes arguments before execution.
4. **`EditChunk` Ingestion Strategy in Dispatcher:**
   `LenientToolDispatcher.ExtractEdits` handles all payload representations:
   - Typed collections: `IEnumerable<EditChunk>`
   - JSON array strings: `"[{\"oldText\": \"...\", \"newText\": \"...\"}]"`
   - Dictionaries and `JsonObject` instances: mapped via `ExtractChunkFromDictionary` recognizing `oldText`, `old_text`, `targetContent`, `search`, etc.
   - `JsonElement` objects: parsed via `ExtractChunkFromJsonElement`
   - Blank chunk fallback: if `edits` contains empty items (`[{}]`), the dispatcher transparently falls back to single-edit parameters (`old_text` / `new_text`).
5. **Deterministic Precedence:**
   - If `edits` contains one or more non-empty items, execute chunk edits via `tools.EditAsync(path, edits, ct)`.
   - Else if effective old text and effective new text are non-null, execute single edit via `tools.EditAsync(path, effectiveOld, effectiveNew, ct)`.
   - Else, return a clear, unambiguous error string stating:
     `"Error: provide 'edits' (array of { oldText, newText }) or single-edit replacement ('oldText'/'newText' or 'old_text'/'new_text')."`

### 3.2 Canonical Tools Naming Reference

| Tool | Canonical Parameters | Leniently Accepted Aliases (via Dispatcher) |
| :--- | :--- | :--- |
| `read` | `path`, `offset`, `limit` | `file_path`, `filePath`, `start_line`, `startLine`, `from_line`, `end_line`, `endLine`, `to_line` |
| `write` | `path`, `content` | `file_path`, `filePath`, `text`, `code` |
| `edit` | `path`, `edits`, `old_text`, `new_text` | `oldText`, `newText`, `targetContent`, `replacementContent`, `search`, `replace` |
| `bash` | `command`, `timeout`, `working_directory` | `workingDirectory`, `cwd`, `workdir` |
| `load_skill` | `skill_name` | `skillName`, `skill`, `name` |
| `ask` | `questions` | `question_items`, `items` |
| `propose_tasks` | `tasks`, `title`, `description`, `priority` | `task`, single-task parameters |
| `request_changes` | `feedback`, `reason` | `critique`, `comment` |
| `web_search` | `query`, `max_results` | `maxResults`, `q`, `limit`, `count` |

---

## 4. Implementation Checklist & Deliverables

- [x] **Clean Model & Consistent Signatures:**
  - [x] Preserve clean `public record EditChunk(string OldText, string NewText);` without property duplication or custom serialization attributes.
  - [x] Establish consistent `snake_case` naming on all canonical tool delegates without parameter duplication.
  - [x] Implement `LenientAIFunction` wrapper in [CanonicalAIFunctionFactory](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) to delegate call normalization to [LenientToolDispatcher](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs).
  - [x] Wire `LenientToolDispatcher` in [CrucibleAgentHarness](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs).
  - [x] Unify and clarify error feedback message when `edit` arguments are missing.
- [x] **Centralized LenientToolDispatcher Normalization:**
  - [x] Ingest `EditChunk` from `IEnumerable<EditChunk>`, JSON strings, `IDictionary`, `JsonObject`, and `JsonElement` via `ExtractChunkFromDictionary` and `ExtractChunkFromJsonElement`.
  - [x] Support blank chunk fallback (`edits: [{}]`) with single-edit shortcut parameters.
  - [x] Normalize `offset` / `limit` including computing line count from `end_line` - `start_line` + 1.
  - [x] Forward single-task arguments (`title`, `description`, `priority`) directly to `ProposeTasks`.
  - [x] Normalize aliases for all tools (`code`, `cwd`, `skill`, `q`, `count`, `critique`, `comment`, `question_items`, `items`).
- [x] **Automated Tests:**
  - [x] Test `edit` tool with `{ path, oldText, newText }` (camelCase shortcut).
  - [x] Test `edit` tool with `{ path, old_text, new_text }` (snake_case shortcut).
  - [x] Test `edit` tool with `{ path, edits: [{ oldText, newText }] }`.
  - [x] Test `edit` tool with `{ path, edits: [{ old_text, new_text }] }`.
  - [x] Test `edit` tool with string JSON `edits`.
  - [x] Test `edit` tool missing all edit parameters returns unified, non-confusing error message.
  - [x] Verify `edit` tool JSON Schema exposes clean, non-duplicated properties.
  - [x] Test `bash` tool with `workingDirectory` and `cwd` aliases.
  - [x] Test `load_skill` tool with `skillName` and `skill` aliases.
  - [x] Test `web_search` tool with `maxResults`, `q`, and `count` aliases.
  - [x] Test `read` tool with `start_line` and `end_line` slice parameters.
  - [x] Test `write` tool with `filePath`, `text`, and `code` aliases.
  - [x] Test `ask` tool with `question_items` and `items` aliases.
  - [x] Test `request_changes` tool with `critique` and `comment` aliases.
  - [x] Run full test suite: `dotnet test`.

