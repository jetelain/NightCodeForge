namespace NightCodeForge.Core.Domain;

/// <summary>
/// Helper methods and display metadata for task merge modes.
/// </summary>
public static class MergeModeHelper
{
    /// <summary>
    /// All currently supported merge modes.
    /// </summary>
    public static readonly MergeMode[] All = [MergeMode.Automatic, MergeMode.ManualReviewRequired];

    /// <summary>
    /// Returns the human-readable display label for a merge mode.
    /// </summary>
    public static string GetLabel(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "Automatic Merge",
        MergeMode.ManualReviewRequired => "Manual Review Required",
        _ => mode.ToString()
    };

    /// <summary>
    /// Returns the short code or chip label for a merge mode.
    /// </summary>
    public static string GetShortLabel(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "Auto",
        MergeMode.ManualReviewRequired => "Manual",
        _ => mode.ToString()
    };

    /// <summary>
    /// Returns the descriptive explanation for a merge mode.
    /// </summary>
    public static string GetDescription(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "Auto-squashes and merges to base branch once status reaches Ready.",
        MergeMode.ManualReviewRequired => "Pauses at Ready for human maintainer review in the Review Room before merging.",
        _ => string.Empty
    };

    /// <summary>
    /// Returns the CSS badge class for styling this merge mode.
    /// </summary>
    public static string GetBadgeClass(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "badge-teal",
        MergeMode.ManualReviewRequired => "badge-indigo",
        _ => "badge-secondary"
    };

    /// <summary>
    /// Standard theme hex color for merge mode badges.
    /// </summary>
    public static string GetBadgeColor(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "#14b8a6",
        MergeMode.ManualReviewRequired => "#6366f1",
        _ => "#64748b"
    };

    /// <summary>
    /// Returns an icon for the merge mode.
    /// </summary>
    public static string GetIcon(MergeMode mode) => mode switch
    {
        MergeMode.Automatic => "⚡",
        MergeMode.ManualReviewRequired => "🔍",
        _ => "🔀"
    };
}
