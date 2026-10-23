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

INSTRUCTIONS:
1. Inspect each conflicted file with 'read'. Conflict blocks look like:
   <<<<<<< HEAD                 (your feature changes)
   ||||||| <ancestor label>      (original code; label varies)
   =======
   >>>>>>> {0}   (incoming changes from base branch)
2. Preserve the functionality of this task while integrating the incoming changes. Use 'edit' to replace each whole block with clean resolved code. NO conflict markers may remain.
3. Non-textual conflicts (a file deleted on one side and modified on the other, or binary files) have no markers: decide explicitly and resolve with 'git rm <file>' or 'git add <file>'. For textual conflicts you do NOT need to stage anything; the orchestrator stages resolved files.
4. Run '{2}' and '{3}' via 'bash' to verify.
5. DO NOT run 'git commit', 'git merge --abort' or 'git reset'. Finish your turn once the files are resolved and verified; the orchestrator completes the merge.";
}
