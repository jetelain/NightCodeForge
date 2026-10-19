# NightCodeForge.Agents.Skills

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Modular Skills  
> **Role:** Implements progressive disclosure skill loading and prompt enhancement via `Microsoft.Agents.AI.Skills`.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Skills` provides dynamic skill loading for AI agents using progressive disclosure. Rather than bloating system prompts with every available workflow cheatsheet, it injects a lightweight skill catalog into system instructions and exposes a `load_skill` tool, allowing agents to load detailed instructions only when needed.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `SkillsService` | Class | Discovers skills across workspace and global directories, builds catalog prompts, and creates `load_skill` tools. |
| `SkillDescriptor` | Record | Metadata model capturing skill name, summary description, directory path, and `SKILL.md` path. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `DefaultBootstrapPromptBuilder`.
- **Outbound (What this relies on):** `Microsoft.Agents.AI.Skills`, `Microsoft.Extensions.AI`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Hierarchical Discovery):** Workspace skills (`.nightcodeforge/skills`, `.agents/skills`, `skills`) override global skills (`~/.nightcodeforge/skills`).
- **Rule 2 (Progressive Disclosure):** Only high-level skill names and descriptions are injected into prompts; full markdown instructions are loaded on demand via `load_skill`.
- **Rule 3 (Fail-Safe Loading):** Malformed skill markdown files must not crash prompt assembly; invalid skills are logged and excluded.

## 5. Usage Example

```csharp
// Discovering available skills and generating catalog prompt
var skills = await skillsService.DiscoverSkillsAsync(workspace.RootPath, cancellationToken);
var catalogPrompt = skillsService.BuildCatalogPrompt(skills);
systemPromptBuilder.AppendSection("Available Skills", catalogPrompt);
```
