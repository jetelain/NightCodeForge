# 🧱 Step 8.1: Domain Entities & Database Schema Evolution

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Define and establish the core database domain entities for extensible workflows and stages, update existing entities (`ForgeTask`, `Workspace`, `StageConversation`), configure Entity Framework Core mappings with SQLite Write-Ahead Logging (WAL) pragmas, and provide non-destructive schema migrations.

---

## 2. Detailed File Modifications & Additions

### 2.1 `src/NightCodeForge.Core/Domain/WorkflowDefinition.cs` (New)
- Primary Key: `int Id`
- Display Name: `string Name`
- Unique Code: `string Code` (e.g. `"standard-3-agent"`)
- UI Glyph / Emoji Icon: `string Icon` (e.g. `"⚡"`, `"🔄"`)
- Description: `string Description`
- Global Default Flag: `bool IsDefault`
- Scoping: `int? WorkspaceId` (null = global template)
- Timestamps: `DateTime CreatedAt`, `DateTime UpdatedAt`
- Navigation Properties:
  - `Workspace? Workspace`
  - `ICollection<StageDefinition> Stages`
  - `ICollection<ForgeTask> Tasks`

### 2.2 `src/NightCodeForge.Core/Domain/StageDefinition.cs` (New)
- Primary Key: `int Id`
- Foreign Key: `int WorkflowId`
- Stage Identifier: `string StageId` (e.g. `"task-review"`, `"deterministic-build"`)
- Display Name: `string DisplayName`
- UI Glyph / Emoji Icon: `string Icon` (e.g. `"🔍"`, `"⚙️"`, `"💻"`, `"🧪"`, `"🧐"`, `"🛡️"`)
- Sequence Order: `int SequenceOrder` (1, 2, 3...)
- Stage Kind: `WorkflowStageKind Kind` (`Agentic` or `Deterministic`)
- C# Implementation Type: `string CsharpImplementationType`
- Configuration Payload: `string ConfigurationJson`
- Timestamp: `DateTime CreatedAt`
- Navigation Property: `WorkflowDefinition? Workflow`

### 2.3 `src/NightCodeForge.Core/Workflow/Configuration/` (New)
- **`AgenticStageConfig.cs`**:
  ```csharp
  public class AgenticStageConfig
  {
      public string? AgentProfileId { get; set; }
      public string? StageDirective { get; set; }
      public AgentMode RequiredMode { get; set; } = AgentMode.Full;
      public string? LoopBackStageId { get; set; }
      public int MaxRetries { get; set; } = 3;
      public int? TimeoutMinutes { get; set; }
  }
  ```
- **`DeterministicStageConfig.cs`**:
  ```csharp
  public class DeterministicStageConfig
  {
      public string Action { get; set; } = "Build";
      public string? Command { get; set; }
      public string LoopBackStageId { get; set; } = "implementation";
      public bool FailOnError { get; set; } = true;
      public int TimeoutSeconds { get; set; } = 300;
      public Dictionary<string, string> ExtraParameters { get; set; } = new();
  }
  ```

### 2.4 Updates to Existing Domain Entities
- **`ForgeTask.cs`**:
  - Add: `public int WorkflowId { get; set; }`
  - Add: `public WorkflowDefinition? Workflow { get; set; }`
- **`Workspace.cs`**:
  - Add: `public int? DefaultWorkflowId { get; set; }`
  - Add: `public ICollection<WorkflowDefinition> Workflows { get; set; } = new List<WorkflowDefinition>();`
- **`StageConversation.cs`**:
  - Change: `public int? TaskId { get; set; }` (make nullable)
  - Add: `public int? WorkspaceId { get; set; }`
  - Add: `public ConversationType ConversationType { get; set; } = ConversationType.StageRun;`
  - Add: `public string? Title { get; set; }`
  - Add: `public Workspace? Workspace { get; set; }`

### 2.5 `src/NightCodeForge.Infrastructure/Data/AppDbContext.cs`
- Add `DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();`
- Add `DbSet<StageDefinition> StageDefinitions => Set<StageDefinition>();`
- Configure Fluent API:
  - `WorkflowDefinition`: Key, index on `Code` (unique), foreign key to `Workspace` (`OnDelete(DeleteBehavior.SetNull)`).
  - `StageDefinition`: Key, index on `{ WorkflowId, StageId }` (unique), index on `{ WorkflowId, SequenceOrder }`, foreign key to `WorkflowDefinition` (`OnDelete(DeleteBehavior.Cascade)`).
  - `ForgeTask`: Foreign key to `WorkflowDefinition` (`OnDelete(DeleteBehavior.Restrict)`).
  - `StageConversation`: Foreign key to `Workspace` (`OnDelete(DeleteBehavior.Cascade)`), optional foreign key to `ForgeTask` (`OnDelete(DeleteBehavior.Cascade)`).
- Update `EnsureDatabaseCreatedAndWalAsync`:
  - Non-destructive SQLite table creations for `WorkflowDefinitions` and `StageDefinitions`.
  - Add column `WorkflowId` to `Tasks`.
  - Add columns `WorkspaceId`, `ConversationType`, `Title` to `StageConversations`.

---

## 3. Verification Criteria
1. `dotnet build` compiles cleanly with no compiler warnings or errors.
2. In-memory and SQLite test instances initialize tables without foreign key constraint violations.
3. Unit tests verify model serialization, default values, and nullable constraints.
