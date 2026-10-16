# 💬 Step 8.5: Ideation Chat Persistence via Conversation

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Completed

---

## 1. Objective

Reuse and generalize `Conversation` and `ConversationTurn` (formerly `StageConversation` and `StageConversationTurn`) to store interactive **Ideation & Discovery Chat** sessions in SQLite, ensuring that conversational history, DeepSeek thinking traces, tool execution records, human answers, and discovered task proposals survive page navigation, application restarts, and multi-tab sessions.

---

## 2. Technical Design & Schema Generalization

### 2.1 Domain Model Updates
In `Conversation`:
- `public int? TaskId { get; set; }` (Nullable: null for Ideation)
- `public int? WorkspaceId { get; set; }` (Required for Ideation)
- `public ConversationType ConversationType { get; set; }` (`StageRun = 0`, `Ideation = 1`)
- `public string? Title { get; set; }` (e.g. "Architecture Discovery: Microservices Decomposition")

### 2.2 Repository Extensions (`ITaskRepository` / `IConversationRepository`)
Add dedicated Ideation conversation lifecycle methods:
```csharp
Task<Conversation> StartIdeationConversationAsync(
    int workspaceId,
    string? title = null,
    string? modelName = null,
    string? agentProfileId = null,
    CancellationToken cancellationToken = default);

Task<IReadOnlyList<Conversation>> GetIdeationConversationsAsync(
    int workspaceId,
    CancellationToken cancellationToken = default);

Task<Conversation?> GetActiveIdeationConversationAsync(
    int workspaceId,
    CancellationToken cancellationToken = default);
```

### 2.3 Turn Mapping Matrix: Ideation to `ConversationTurn`

| Ideation Element | `ConversationTurn` Field | Value / Format |
| :--- | :--- | :--- |
| **User Prompt** | `Role` = `StageTurnRole.User`, `Content` | Raw user question or requirement description |
| **Architect Reply** | `Role` = `StageTurnRole.Assistant`, `Content` | Markdown text emitted by architect agent |
| **Thinking Trace** | `ThinkingTrace` | Extracted `<think>` delta or reasoning tokens |
| **Tool Invocations** | `ToolName`, `ToolArguments`, `ToolResult` | `read`, `bash`, `propose_tasks`, `ask` tool calls |
| **Execution Metrics**| `DurationMs`, `IsError` | Measured tool duration and failure flag |
| **Discovered Proposals**| `MetadataJson` | Serialized JSON array of `IdeationProposal` objects |

### 2.4 Service & UI Synchronization (`IdeationSessionState` & `Ideation.razor`)
1. **On Workspace Switch / Page Load**:
   - `Ideation.razor` calls `GetActiveIdeationConversationAsync(workspace.Id)`.
   - If an active conversation exists, turns and proposals are loaded directly into `Session.Messages` and `Session.Proposals`.
   - If no conversation exists, a new conversation record is initialized.
2. **On Message Send**:
   - Save user prompt immediately via `AddConversationTurnAsync`.
   - Stream model output and tool calls.
   - Upon completion, save assistant turn with thinking trace, tool executions, and discovered proposal metadata.
3. **Session Management UI**:
   - Add session dropdown in header to switch between previous ideation chats.
   - "🔄 Clear Chat" button completes current conversation (`Status = Completed`) and spawns a fresh `Conversation`.

---

## 3. Verification Criteria
1. Sending messages in `/ideation` creates a `Conversation` with `ConversationType = Ideation` and `TaskId = null`.
2. Refreshing the browser or switching between `/ideation` and `/backlog` preserves all messages, thinking traces, and proposal cards.
3. Ingesting a proposal creates a `ForgeTask` linked to the active workspace and standard workflow.
4. Unit and integration tests verify persistence, turn ordering, and retrieval.
