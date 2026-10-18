namespace NightCodeForge.Core.Domain;

/// <summary>
/// Operational mode defining tool capabilities for an agent or requirements for a stage.
/// </summary>
public enum AgentMode
{
    ReadOnly = 0, // Read-only tools (inspection, triage, discovery)
    Full = 1,     // Full capabilities (editing, file writing, shell execution)
    Review = 2    // Review & audit capabilities (diff inspection, requesting changes, targeted fixes)
}

/// <summary>
/// Execution environment mode for tasks within a workspace.
/// </summary>
public enum ExecutionMode
{
    BareHost = 0,          // Direct host CLI (Opt-in for simple setups or bare Linux)
    WslDirect = 1,         // Direct execution inside WSL2 distro (wsl.exe -d <distro>)
    ContainerSandbox = 2   // Ephemeral Docker/Podman container running on ext4 (Default)
}

/// <summary>
/// Thinking/reasoning depth mode for thinking models (e.g. DeepSeek-R1).
/// </summary>
public enum ThinkingEffort
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    CustomBudget = 4
}

/// <summary>
/// Security confinement tier for sandbox execution.
/// </summary>
public enum SandboxSecurityTier
{
    Standard = 0,          // Normal execution on feature branch; network permitted for restores
    RestrictedReadOnly = 1 // Untrusted external intake on main/master: :ro mount, --network none, --read-only, --cap-drop=ALL
}

/// <summary>
/// Comprehensive lifecycle status of a task in NightCodeForge.
/// </summary>
public enum TaskOverallStatus
{
    Suggested = 0,         // Newly created (via triage agent, ideation chat, or docs/tasks/*.md)
    Rejected = 1,          // Manually rejected by maintainer
    Open = 2,              // Approved by maintainer; eligible for processing once dependencies are Closed
    Reviewed = 3,          // Stage 1 (ForgeTask Review) completed successfully
    Implemented = 4,       // Stage 3 (Implementation) completed successfully
    Ready = 5,             // Stage 6 (Deterministic Post-Review Verification) completed; awaiting merge
    Closed = 6,            // Fully merged into base branch; terminal state
    Cancelled = 7,         // Manually cancelled during processing
    Failed = 8             // Execution failed with fatal unrecoverable error
}

/// <summary>
/// Determines how an approved and verified task is merged into the base branch.
/// </summary>
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1   // Maintainer manually reviews diff/tests before triggering merge in UI
}

/// <summary>
/// Type of clarification question asked to a human.
/// </summary>
public enum QuestionType
{
    SingleChoice = 0,
    MultipleChoice = 1,
    FreeText = 2
}

/// <summary>
/// Status of a human clarification question.
/// </summary>
public enum QuestionStatus
{
    Pending = 0,
    Answered = 1,
    Skipped = 2
}

/// <summary>
/// Execution status of a specific workflow stage.
/// </summary>
public enum StageExecutionStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Skipped = 4,
    WaitingForHuman = 5,
    Cancelled = 6
}

/// <summary>
/// Categorization of a workflow stage: agentic (LLM-driven) vs deterministic (CPU-native tool execution).
/// </summary>
public enum WorkflowStageKind
{
    Agentic = 0,
    Deterministic = 1
}

/// <summary>
/// Role of a message or activity turn within a stage conversation.
/// </summary>
public enum StageTurnRole
{
    User = 0,
    Assistant = 1,
    Thinking = 2,
    Tool = 3,
    ToolResult = 4,
    Optimization = 5,
    System = 6
}

/// <summary>
/// Type or scope of a conversation.
/// </summary>
public enum ConversationType
{
    StageRun = 0,    // Autonomous pipeline stage execution (tied to a task)
    Ideation = 1,    // Interactive discovery & task formulation chat (workspace-level)
    AdHoc = 2        // Standalone developer consultation
}
