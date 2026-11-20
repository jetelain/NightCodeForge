# NightCodeForge.Agents.Ideation

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Interactive Discovery  
> **Role:** Implements the interactive ideation agent for exploring codebases and synthesizing structured backlog tasks.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Ideation` powers the interactive chat experience in the Web UI. It allows developers to converse with an AI agent about architectural ideas, feature requests, or bugs. The agent explores the workspace code structure using read-only tools and synthesizes structured `ForgeTask` proposals complete with descriptions, prerequisites, and dependency links.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IdeationConversationCoordinator` | Class | Singleton implementing `IIdeationConversationCoordinator`, managing warm execution contexts, 48k JIT preload, structured turn persistence, and circuit-independent turns. |
| `WarmIdeationSession` | Class | Holds per-conversation warm `HarnessAgent`, `CanonicalToolsetSession`, authoritative `ChatHistory`, and in-flight turn state. |
| `ScopedConversationTurnQueue` | Class | Serialized scope-hygienic persistence queue writing `ConversationTurn` records through fresh DI scopes. |
| `IdeationSystemPromptBuilder` | Class | Builds stabilized system prompts, `<workspace_context_update>` deltas, date enrichment, and fallback proposal parsing. |
| `DefaultMaxTurns` | Constant | Standard limit on exploration turns per ideation message. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Web UI (`IdeationSessionState` / `Ideation.razor`).
- **Outbound (What this relies on):** `IHarnessAgentFactory`, `ICanonicalToolsetFactory`, `IAgentWorkspaceContextService`, `ILlmModelPreloadService`, `ILlmGatewayService`, `Core.Repositories.ITaskRepository`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Read-Only Safety):** Ideation agents must never mutate code files or commit changes; they are restricted to exploration (`read`, `bash` in read-only mode), proposing tasks, and scoped backlog mutations.
- **Rule 2 (Structured Proposal Parsing):** Proposed tasks must parse into valid JSON task arrays with titles, descriptions, and optional parent dependency IDs.
- **Rule 3 (Interactive Streaming):** Responses stream live thinking/text deltas, tool calls, `ask` questions, and activity events independently of individual Blazor circuit lifetimes.

## 5. Usage Example

```csharp
// Warm up on page open, then stream circuit-independent turns via subscription
var handle = await coordinator.GetOrCreateActiveConversationAsync(workspaceId, cancellationToken: ct);
using var subscription = coordinator.Subscribe(handle.Conversation.Id, update => HandleUpdate(update));
await coordinator.StartTurnAsync(new IdeationSendRequest(workspaceId, handle.Conversation.Id, "Analyze the auth system"), ct);
```
