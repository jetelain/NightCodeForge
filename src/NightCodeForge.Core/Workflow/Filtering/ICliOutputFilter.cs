namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Transforms verbose CLI output into compact, high-signal text for LLM context.
/// </summary>
public interface ICliOutputFilter
{
    /// <summary>
    /// Checks whether this filter applies to the executed command line.
    /// </summary>
    bool CanHandle(string command);

    /// <summary>
    /// Transforms raw CLI stdout/stderr into compact output for the LLM.
    /// </summary>
    string FilterOutput(string rawOutput, string workspaceRoot, CommandFilterOptions options);
}
