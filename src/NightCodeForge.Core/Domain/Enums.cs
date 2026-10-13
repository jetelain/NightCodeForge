namespace NightCodeForge.Core.Domain;

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
/// Overall lifecycle status of an issue.
/// </summary>
public enum IssueOverallStatus
{
    WaitingOnDependencies = 0,
    Pending = 1,
    Running = 2,
    BlockedQuestion = 3,
    Review = 4,
    Completed = 5,
    Cancelled = 6,
    Failed = 7
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
    Skipped = 4
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
/// Known harness identifiers.
/// </summary>
public static class HarnessType
{
    public const string Crucible = "crucible";
    public const string PiRpc = "pi-rpc";
    public const string Acp = "acp";
}
