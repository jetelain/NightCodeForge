using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Unified contract for AI agent execution harnesses (Crucible, Pi JSON-RPC, ACP).
/// </summary>
public interface IAgentHarness
{
    /// <summary>
    /// Unique identifier of the harness (e.g. "crucible", "pi", "acp").
    /// </summary>
    string HarnessId { get; }

    /// <summary>
    /// Executes a pipeline stage session with the provided context.
    /// </summary>
    Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry managing registered agent harnesses.
/// </summary>
public interface IHarnessRegistry
{
    IAgentHarness GetHarness(string harnessId);
    void RegisterHarness(IAgentHarness harness);
    bool TryGetHarness(string harnessId, out IAgentHarness? harness);
    IEnumerable<IAgentHarness> GetAllHarnesses();
}
