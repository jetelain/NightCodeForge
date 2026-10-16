# 🌱 Step 8.2: Fresh Database Seeding & Default Standard Workflow

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Implement the startup seeder `EnsureDefaultWorkflowsAsync` in `AppDbContext` to create the standard 6-stage autonomous workflow on a fresh empty database, complete with UI glyph icons for both the workflow and each of its constituent stages. (Note: Legacy database migration/backfill is **not** required; setup will initialize on a clean database).

---

## 2. Seed Data Specification: Standard 3-Agent Workflow

### 2.1 Workflow Definition Record
- `Name`: `"Standard 3-Agent Pipeline"`
- `Code`: `"standard-3-agent"`
- `Icon`: `"⚡"` (Emoji/Glyph for badges, dropdowns, and cards)
- `Description`: `"Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification -> Auto-Merge."`
- `IsDefault`: `true`
- `WorkspaceId`: `null` (Global template)

### 2.2 Stage Definitions Configuration Matrix

| Order | Icon | StageId | DisplayName | Kind | C# Implementation Type | Configuration Payload |
| :---: | :---: | :--- | :--- | :---: | :--- | :--- |
| **1** | 🔍 | `task-review` | ForgeTask Review | Agentic | `agentic-review` | `{"AgentProfileId": null, "RequiredMode": 1, "MaxRetries": 3, "TimeoutMinutes": 30}` |
| **2** | ⚙️ | `deterministic-pre-impl` | Pre-Implementation Baseline | Deterministic | `deterministic-composite` | `{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}` |
| **3** | 💻 | `implementation` | Implementation | Agentic | `agentic-impl` | `{"AgentProfileId": null, "RequiredMode": 1, "MaxRetries": 3, "TimeoutMinutes": 60}` |
| **4** | 🧪 | `deterministic-post-impl` | Post-Implementation Gates | Deterministic | `deterministic-composite` | `{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "build,test,coverage,sast"}}` |
| **5** | 🧐 | `code-review-fix` | Code Review & Fix | Agentic | `agentic-review-fix` | `{"AgentProfileId": null, "RequiredMode": 2, "LoopBackStageId": "implementation", "MaxRetries": 3, "TimeoutMinutes": 30}` |
| **6** | 🛡️ | `deterministic-post-review` | Post-Review Verification | Deterministic | `deterministic-composite` | `{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}` |

---

## 3. Implementation Details

### 3.1 `EnsureDefaultWorkflowsAsync` in `AppDbContext.cs`
```csharp
public async Task<WorkflowDefinition> EnsureDefaultWorkflowsAsync(CancellationToken cancellationToken = default)
{
    var standardWorkflow = await WorkflowDefinitions
        .Include(w => w.Stages)
        .FirstOrDefaultAsync(w => w.Code == "standard-3-agent", cancellationToken);

    if (standardWorkflow == null)
    {
        standardWorkflow = new WorkflowDefinition
        {
            Name = "Standard 3-Agent Pipeline",
            Code = "standard-3-agent",
            Icon = "⚡",
            Description = "Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification.",
            IsDefault = true,
            WorkspaceId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Stages = new List<StageDefinition>
            {
                new StageDefinition
                {
                    StageId = "task-review",
                    DisplayName = "ForgeTask Review",
                    Icon = "🔍",
                    SequenceOrder = 1,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic-review",
                    ConfigurationJson = """{"AgentProfileId": null, "RequiredMode": 1, "MaxRetries": 3, "TimeoutMinutes": 30}"""
                },
                new StageDefinition
                {
                    StageId = "deterministic-pre-impl",
                    DisplayName = "Pre-Implementation Baseline",
                    Icon = "⚙️",
                    SequenceOrder = 2,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                },
                new StageDefinition
                {
                    StageId = "implementation",
                    DisplayName = "Implementation",
                    Icon = "💻",
                    SequenceOrder = 3,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic-impl",
                    ConfigurationJson = """{"AgentProfileId": null, "RequiredMode": 1, "MaxRetries": 3, "TimeoutMinutes": 60}"""
                },
                new StageDefinition
                {
                    StageId = "deterministic-post-impl",
                    DisplayName = "Post-Implementation Gates",
                    Icon = "🧪",
                    SequenceOrder = 4,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "build,test,coverage,sast"}}"""
                },
                new StageDefinition
                {
                    StageId = "code-review-fix",
                    DisplayName = "Code Review & Fix",
                    Icon = "🧐",
                    SequenceOrder = 5,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic-review-fix",
                    ConfigurationJson = """{"AgentProfileId": null, "RequiredMode": 2, "LoopBackStageId": "implementation", "MaxRetries": 3, "TimeoutMinutes": 30}"""
                },
                new StageDefinition
                {
                    StageId = "deterministic-post-review",
                    DisplayName = "Post-Review Verification",
                    Icon = "🛡️",
                    SequenceOrder = 6,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                }
            }
        };

        WorkflowDefinitions.Add(standardWorkflow);
        await SaveChangesAsync(cancellationToken);
    }

    return standardWorkflow;
}
```

### 3.2 Startup Hook in `src/NightCodeForge.Web/Program.cs`
Executed when initializing a clean database:
```csharp
await dbContext.EnsureDatabaseCreatedAndWalAsync();
await dbContext.EnsureDefaultGlobalAgentProfilesAsync(defaultModel);
await dbContext.EnsureDefaultWorkflowsAsync(); // <-- Seed standard workflow & stages
```

---

## 4. Verification Criteria
1. When starting an empty database, `WorkflowDefinitions` contains the standard workflow with 6 ordered stages, each with its designated `Icon`.
2. Re-running the application does not duplicate the workflow or stages.
3. Seeding completes in < 50ms during startup.
