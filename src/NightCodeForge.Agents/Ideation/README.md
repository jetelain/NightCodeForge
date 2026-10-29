# NightCodeForge.Agents.Ideation

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Interactive Discovery  
> **Role:** Implements the interactive ideation agent for exploring codebases and synthesizing structured backlog tasks.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Ideation` powers the interactive chat experience in the Web UI. It allows developers to converse with an AI agent about architectural ideas, feature requests, or bugs. The agent explores the workspace code structure using read-only tools and synthesizes structured `ForgeTask` proposals complete with descriptions, prerequisites, and dependency links.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IdeationService` | Class | Implements `IIdeationService`, managing interactive multi-turn exploration and task proposal synthesis. |
| `DefaultMaxTurns` | Constant | Standard limit on exploration turns per ideation message. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Web UI (`IdeationModal` / Chat component).
- **Outbound (What this relies on):** `Inference.ILlmChatClientFactory`, `Tools.CanonicalTools`, `Core.Repositories.ITaskRepository`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Read-Only Safety):** Ideation agents must never mutate code files or commit changes; they are restricted to exploration (`read`, `bash` in read-only mode) and proposing tasks.
- **Rule 2 (Structured Proposal Parsing):** Proposed tasks must parse into valid JSON task arrays with titles, descriptions, and optional parent dependency IDs.
- **Rule 3 (Interactive Streaming):** Responses must stream live markdown and activity events back to the UI chat window.

## 5. Usage Example

```csharp
// Starting or continuing an ideation chat session
await foreach (var update in ideationService.SendMessageAsync(
    workspaceId,
    conversationId,
    userPrompt: "Analyze the auth system and propose tasks to add JWT refresh tokens",
    cancellationToken))
{
    Console.Write(update.Text);
}
```
