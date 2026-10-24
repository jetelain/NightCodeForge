namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Domain constants for Git conflict detection, resolution budget, audit attribution, and marker parsing.
/// </summary>
public static class ConflictResolutionConstants
{
    /// <summary>
    /// Maximum consecutive failed resolution verification attempts before escalating to a human maintainer.
    /// </summary>
    public const int MaxConflictResolutionAttempts = 3;

    /// <summary>
    /// Maximum length in characters for compiler error diagnostics injected into prompts.
    /// </summary>
    public const int MaxCompilerErrorsLength = 4000;

    /// <summary>
    /// Cumulative maximum conflict resolution attempts across all conflicts on a single task.
    /// </summary>
    public const int MaxTotalConflictResolutionAttempts = 6;

    /// <summary>
    /// Maximum distinct merge conflicts against advancing base branch before escalating to human maintainer.
    /// </summary>
    public const int MaxLifetimeConflictDetections = 3;

    /// <summary>
    /// Maximum retry attempts when host push of merged base branch is rejected due to non-fast-forward race.
    /// </summary>
    public const int MaxPushRetries = 1;

    /// <summary>
    /// Minimum Git CLI version required for non-destructive structured mergeability preview (git merge-tree --write-tree).
    /// </summary>
    public static readonly Version MinimumRequiredGitVersion = new(2, 38, 0);

    // Git Conflict Markers
    public const string MarkerStart = "<<<<<<< ";
    public const string MarkerAncestor = "||||||| ";
    public const string MarkerEnd = ">>>>>>> ";
    public const string MarkerSeparator = "=======";

    // Git index stages of unmerged entries (git ls-files -u)
    public const int IndexStageAncestor = 1;
    public const int IndexStageOurs = 2;
    public const int IndexStageTheirs = 3;

    // Audit and History Log Messages
    public const string AuditApprovalDiscardedFormat = "Maintainer approval discarded due to merge conflict with {0}.";
    public const string AuditConflictResolutionRoutedFormat = "Merge conflict detected with {0}. Routed to Implementation Agent for resolution.";
    public const string AuditConflictResolvedByAgentFormat = "Implementation Agent resolved merge conflicts in {0} file(s) ({1}).";
    public const string AuditPostReviewVerificationPassedReopened = "Post-Review Verification passed. Re-opened for maintainer review.";
    public const string AuditConflictResolutionBudgetExhausted = "Conflict resolution budget exhausted. Escalated to maintainer.";
    public const string AuditBaselineSyncSuccessFormat = "Synchronized feature branch with latest '{0}' prior to baseline verification.";
    public const string MergeCommitMessageFormat = "Resolve merge conflicts with {0}";

    // UI Alert & Toast Texts
    public const string ReReviewBannerTitle = "Autonomous Conflict Resolution Completed";
    public const string ReReviewBannerMessageFormat = "This task encountered merge conflicts with '{0}'. The implementation agent resolved all conflicts and successfully verified the deterministic build and test gates. Please review the updated diff before approving.";
    public const string ConflictDispatchedToastTitle = "Merge Conflict Detected";
    public const string ConflictDispatchedToastMessageFormat = "Merge conflict detected with {0}. An implementation agent has been dispatched to resolve the conflict and will notify you when ready for re-review.";

    // Conflict Directive Template
    public const string ConflictDirectiveHeader = "[CRITICAL: Autonomous Merge Conflict Resolution Required]";
    public const string ConflictDirectiveFormat = @"
[CRITICAL: Autonomous Merge Conflict Resolution Required]
Your feature branch has conflicts with target branch '{0}'.
A merge is in progress in your worktree. The following files are conflicted:
{1}
{4}
INSTRUCTIONS:
1. Inspect each conflicted file with 'read'. Conflict blocks look like:
   <<<<<<< HEAD                 (your feature changes)
   ||||||| <ancestor label>      (original code; label varies)
   =======
   >>>>>>> {0}   (incoming changes from base branch)
2. Preserve the functionality of this task while integrating incoming changes. Use 'edit' to reconcile code and remove ALL four conflict markers (<<<<<<<, |||||||, =======, >>>>>>>). NO markers may remain.
3. Signature Reconciliation: Thoroughly reconcile constructors, interfaces, and method signatures across all affected callers and callees (including tests).
4. Inspect Staged & Untracked Files: The verifier automatically stages resolved conflicted files and newly created files via 'git add' during verification. Therefore, 'git diff' alone hides staged modifications. Use 'git diff --cached' and 'git status' to inspect staged changes and unstaged working tree state.
5. Non-textual conflicts (a file deleted on one side and modified on the other, or binary files) have no markers: decide explicitly and resolve with 'git rm <file>' or 'git add <file>'.
6. MANDATORY VERIFICATION: You MUST execute '{2}' and '{3}' via 'bash'. Any claims that compilation succeeded without running the actual commands will fail deterministic verification. Fix all compiler errors and broken tests.
7. DO NOT run 'git commit', 'git merge --abort', or 'git reset'. Finish your turn once files are resolved and verified; the orchestrator deterministically verifies compilation and completes the merge.";
}
