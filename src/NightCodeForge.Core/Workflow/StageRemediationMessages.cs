namespace NightCodeForge.Core.Workflow;

/// <summary>
/// System notices injected into a stage conversation when a post-stage artifact gate rejects a premature completion.
/// </summary>
public static class StageRemediationMessages
{
    public const string MissingSpecificationPrompt =
"""
[System Notice: Mandatory Task Specification Document Update Required]
Your investigation turn concluded without modifying the task specification document on disk in docs/tasks/.

As a Staff Software Architect, you are required to produce an updated task specification before this stage can complete.
1. EDIT THE SPECIFICATION: Use the 'edit' or 'write' tool to update the specification document with your approved technical architecture, affected files and classes, implementation plan, and testable acceptance criteria.
2. PRESERVE FRONTMATTER: Keep the top YAML frontmatter block intact (do not modify id or status fields).
3. CLARIFICATION QUESTIONS: If you have blocking questions or product ambiguities for the maintainer, you MUST invoke the 'ask' tool with your structured questions. Plain text questions in conversational chat are not received by the maintainer and cannot be answered.

Please update the task specification document on disk or invoke 'ask' now to proceed.
""";

    public const string EmptyImplementationPrompt =
"""
[System Notice: Mandatory Code Implementation Required]
Your implementation turn concluded without modifying or adding any source or test files in the repository (git diff against the base branch is empty, excluding .scratch/ and docs/tasks/).

You must implement the required code and test changes in the workspace and verify them before this stage can complete. If you are blocked by a genuine product ambiguity, invoke the 'ask' tool.

Please proceed with implementing the changes now.
""";
}

