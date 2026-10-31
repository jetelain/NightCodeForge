# 🏛️ Step 9.2: Database Integrity & Hard Errors (Zero Synthetic Fallbacks)

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Enforce hard errors whenever an entity that is expected to exist in the database is not found. Stop creating synthetic in-memory values or silently falling back to defaults on the fly. When a requested record (such as a specific `WorkflowDefinition` or `AgentProfile`) is missing, throw an explicit domain exception so that callers, background workers, and the web UI receive clear, actionable error feedback.

---

## 2. Technical Scope & Specifications

### 2.1 Exceptions

1. **`WorkflowNotFoundException` (`NightCodeForge.Core.Exceptions`):**
   ```csharp
   namespace NightCodeForge.Core.Exceptions;

   public class WorkflowNotFoundException : InvalidOperationException
   {
       public int? WorkflowId { get; }
       public string? WorkflowCode { get; }

       public WorkflowNotFoundException(int workflowId)
           : base($"Workflow with ID {workflowId} was not found in the database.")
       {
           WorkflowId = workflowId;
       }

       public WorkflowNotFoundException(string workflowCode)
           : base($"Workflow with code '{workflowCode}' was not found in the database.")
       {
           WorkflowCode = workflowCode;
       }
   }
   ```

2. **`AgentProfileNotFoundException` (`NightCodeForge.Core.Exceptions`):**
   ```csharp
   namespace NightCodeForge.Core.Exceptions;

   public class AgentProfileNotFoundException : InvalidOperationException
   {
       public string ProfileId { get; }
       public int? WorkspaceId { get; }

       public AgentProfileNotFoundException(string profileId, int? workspaceId = null)
           : base($"Agent profile '{profileId}' was not found in the database for workspace #{workspaceId?.ToString() ?? "global"}.")
       {
           ProfileId = profileId;
           WorkspaceId = workspaceId;
       }
   }
   ```

---

## 3. Remediation Targets

### 3.1 Strict Pipeline Resolution in `DatabaseWorkflowPipelineProvider`
* **File:** `src/NightCodeForge.Core/Workflow/DatabaseWorkflowPipelineProvider.cs`
* **Current Violation:**
  If `task.WorkflowId` has a value but `GetByIdWithStagesAsync` returns null, the provider silently executes `GetDefaultWorkflowWithStagesAsync`, obscuring invalid task configurations.
* **Remediation:**
  ```csharp
  if (task.WorkflowId.HasValue && task.WorkflowId.Value > 0)
  {
      workflow = await _workflowRepo.GetByIdWithStagesAsync(task.WorkflowId.Value, cancellationToken);
      if (workflow == null)
      {
          throw new WorkflowNotFoundException(task.WorkflowId.Value);
      }
  }
  else
  {
      workflow = await _workflowRepo.GetDefaultWorkflowWithStagesAsync(cancellationToken);
  }

  if (workflow == null || workflow.Stages.Count == 0)
  {
      throw new WorkflowNotFoundException("Default workflow is not seeded or contains no stages.");
  }
  ```

### 3.2 Elimination of Synthetic Profiles in `TaskOrchestrator`
* **File:** `src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs`
* **Current Violation:**
  If an agent profile is not found in the database, `TaskOrchestrator` generates a synthetic profile on the fly (`Profile = profile ?? new AgentProfile { Id = "default", HarnessId = harnessId, ModelName = string.Empty }`), causing downstream runs with blank models or unexpected behavior.
* **Remediation:**
  If `profile == null`, throw an `AgentProfileNotFoundException(stage.StageId, workspace.Id)` or return `StageExecutionResult.Failure(...)` with an explicit diagnostic message.

### 3.3 Elimination of Synthetic Profiles in `IdeationService`
* **File:** `src/NightCodeForge.Agents/Ideation/IdeationService.cs`
* **Current Violation:**
  `ResolveIdeationProfile` falls back to `AgentProfile.CreateDefaultIdeationProfile(workspace?.Id)` in memory if missing from the workspace.
* **Remediation:**
  Retrieve the configured ideation profile from `IWorkspaceRepository`. If no matching profile exists in the database, throw an `AgentProfileNotFoundException("ideation", workspace?.Id)` prompting the user to seed or configure a profile.

### 3.4 Elimination of Synthetic Profiles in `TriageService`
* **File:** `src/NightCodeForge.Agents/Triage/TriageService.cs`
* **Current Violation:**
  If `GetEffectiveAgentProfileAsync(workspace.Id, TriageStage.DefaultStageId)` returns null, a synthetic `AgentProfile` is constructed on the fly with `ModelName = workspace.AgentProfiles?.FirstOrDefault()?.ModelName ?? "default"`.
* **Remediation:**
  Ensure the database seeder inserts the canonical triage profile, and throw `AgentProfileNotFoundException(TriageStage.DefaultStageId, workspace.Id)` if absent.

---

## 4. Verification & Testing

* Add unit tests in `NightCodeForge.Core.Tests`:
  * Assert that `DatabaseWorkflowPipelineProvider.GetPipelineAsync` throws `WorkflowNotFoundException` when given a non-existent `WorkflowId`.
  * Assert that `TaskOrchestrator.ProcessTaskAsync` fails fast when no valid profile is configured in the database.
* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

---

## 5. Implementation Status

- [x] **Status:** COMPLETED
- [x] Created `WorkflowNotFoundException` and `AgentProfileNotFoundException` in `NightCodeForge.Core.Exceptions`.
- [x] Removed synthetic default fallbacks in `DatabaseWorkflowPipelineProvider` (throws `WorkflowNotFoundException` on missing IDs or unseeded default workflows).
- [x] Removed synthetic profile fallbacks in `TaskOrchestrator` (throws `AgentProfileNotFoundException` and fails fast on missing agent profile).
- [x] Injected `IWorkspaceRepository` and removed fallback default profile generation in `IdeationService` and `Ideation.razor`.
- [x] Removed synthetic profile construction in `TriageService` (throws `AgentProfileNotFoundException` if profile missing from database).
- [x] Updated repository implementations (`InMemoryWorkflowRepository` and `EfCoreWorkflowRepository`) to return null when default workflow is missing, delegating hard error throwing to pipeline resolution.
- [x] All 897 tests passing (100%), including non-E2E tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) and end-to-end acceptance suites (`dotnet test`).

