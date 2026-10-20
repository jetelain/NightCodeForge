using System.Text.Json;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Represents field-level mutation badge data between two task revisions.
/// </summary>
public record TaskFieldChange(
    string FieldName,
    string OldValue,
    string NewValue,
    string BadgeClass = "badge-indigo");

/// <summary>
/// Represents a single line in a character/word-level DiffPlex diff comparison.
/// </summary>
public record TaskHistoryDiffLine(
    int? OldLineNumber,
    int? NewLineNumber,
    ChangeType Type,
    string Text)
{
    public string Prefix => Type switch
    {
        ChangeType.Inserted => "+",
        ChangeType.Deleted => "-",
        ChangeType.Modified => "~",
        _ => " "
    };

    public string BackgroundStyle => Type switch
    {
        ChangeType.Inserted => "rgba(16, 185, 129, 0.14)",
        ChangeType.Deleted => "rgba(239, 68, 68, 0.14)",
        ChangeType.Modified => "rgba(245, 158, 11, 0.12)",
        _ => "transparent"
    };

    public string TextColor => Type switch
    {
        ChangeType.Inserted => "#6ee7b7",
        ChangeType.Deleted => "#fca5a5",
        ChangeType.Modified => "#fde68a",
        _ => "var(--text-secondary, #cbd5e1)"
    };
}

/// <summary>
/// View model representing an enriched point-in-time task history snapshot with diffs.
/// </summary>
public class TaskHistoryRevisionModel
{
    public int Version { get; set; }
    public int PreviousVersion { get; set; }
    public DateTime ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string? ChangeReason { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskOverallStatus Status { get; set; }
    public int Priority { get; set; }
    public MergeMode MergeMode { get; set; }
    public string BaseBranch { get; set; } = string.Empty;
    public string? FeatureBranch { get; set; }
    public string? CurrentStageId { get; set; }
    public IReadOnlyList<int> Dependencies { get; set; } = Array.Empty<int>();

    public string TimeAgo { get; set; } = string.Empty;
    public string ActorBadgeClass { get; set; } = "badge-gray";
    public string ActorDisplay { get; set; } = string.Empty;

    public List<TaskFieldChange> FieldChanges { get; set; } = new();

    public bool HasDescriptionDiff { get; set; }
    public int DescriptionLinesAdded { get; set; }
    public int DescriptionLinesDeleted { get; set; }
    public List<TaskHistoryDiffLine> DiffLines { get; set; } = new();
}

/// <summary>
/// Service interface for computing temporal differences and timeline models between task revisions.
/// </summary>
public interface ITaskHistoryDiffService
{
    /// <summary>
    /// Processes task history snapshots into an ordered revision timeline with field-level and text diffs.
    /// Returns revisions ordered descending by version (newest first).
    /// </summary>
    IReadOnlyList<TaskHistoryRevisionModel> BuildTimeline(IReadOnlyList<ForgeTaskHistory> histories, DateTime? utcNow = null);
}

/// <summary>
/// Computes field-level and DiffPlex markdown description diffs across task history snapshots.
/// </summary>
public class TaskHistoryDiffService : ITaskHistoryDiffService
{
    public IReadOnlyList<TaskHistoryRevisionModel> BuildTimeline(IReadOnlyList<ForgeTaskHistory> histories, DateTime? utcNow = null)
    {
        if (histories == null || histories.Count == 0)
        {
            return Array.Empty<TaskHistoryRevisionModel>();
        }

        var sorted = histories.OrderBy(h => h.Version).ToList();
        var timeline = new List<TaskHistoryRevisionModel>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            var curr = sorted[i];
            var currDeps = ParseDependencies(curr.SerializedDependencies);

            var actor = string.IsNullOrWhiteSpace(curr.ChangedBy) ? AuditActors.System : curr.ChangedBy;
            var model = new TaskHistoryRevisionModel
            {
                Version = curr.Version,
                ChangedAt = curr.ChangedAt,
                ChangedBy = actor,
                ChangeReason = curr.ChangeReason,
                Title = curr.Title,
                Description = curr.Description,
                Status = curr.Status,
                Priority = curr.Priority,
                MergeMode = curr.MergeMode,
                BaseBranch = curr.BaseBranch,
                FeatureBranch = curr.FeatureBranch,
                CurrentStageId = curr.CurrentStageId,
                Dependencies = currDeps,
                TimeAgo = FormatRelativeTime(curr.ChangedAt, utcNow),
                ActorBadgeClass = GetActorBadgeClass(actor),
                ActorDisplay = GetActorDisplay(actor)
            };

            if (i > 0)
            {
                var prev = sorted[i - 1];
                model.PreviousVersion = prev.Version;

                if (!string.Equals(prev.Title, curr.Title, StringComparison.Ordinal))
                {
                    model.FieldChanges.Add(new TaskFieldChange("Title", prev.Title, curr.Title, "badge-indigo"));
                }

                if (prev.Status != curr.Status)
                {
                    model.FieldChanges.Add(new TaskFieldChange("Status", prev.Status.ToString(), curr.Status.ToString(), "badge-teal"));
                }

                if (prev.Priority != curr.Priority)
                {
                    model.FieldChanges.Add(new TaskFieldChange("Priority", $"P{prev.Priority}", $"P{curr.Priority}", "badge-amber"));
                }

                if (prev.MergeMode != curr.MergeMode)
                {
                    model.FieldChanges.Add(new TaskFieldChange("MergeMode", prev.MergeMode.ToString(), curr.MergeMode.ToString(), "badge-purple"));
                }

                if (!string.Equals(prev.BaseBranch, curr.BaseBranch, StringComparison.Ordinal))
                {
                    model.FieldChanges.Add(new TaskFieldChange("BaseBranch", prev.BaseBranch, curr.BaseBranch, "badge-indigo"));
                }

                if (!string.Equals(prev.FeatureBranch, curr.FeatureBranch, StringComparison.Ordinal))
                {
                    model.FieldChanges.Add(new TaskFieldChange("FeatureBranch", prev.FeatureBranch ?? "None", curr.FeatureBranch ?? "None", "badge-indigo"));
                }

                if (!string.Equals(prev.CurrentStageId, curr.CurrentStageId, StringComparison.OrdinalIgnoreCase))
                {
                    model.FieldChanges.Add(new TaskFieldChange("Stage", prev.CurrentStageId ?? "None", curr.CurrentStageId ?? "None", "badge-blue"));
                }

                var prevDeps = ParseDependencies(prev.SerializedDependencies);
                if (!prevDeps.OrderBy(x => x).SequenceEqual(currDeps.OrderBy(x => x)))
                {
                    var oldText = prevDeps.Count == 0 ? "None" : $"[{string.Join(", ", prevDeps.OrderBy(x => x).Select(d => $"#{d}"))}]";
                    var newText = currDeps.Count == 0 ? "None" : $"[{string.Join(", ", currDeps.OrderBy(x => x).Select(d => $"#{d}"))}]";
                    model.FieldChanges.Add(new TaskFieldChange("Dependencies", oldText, newText, "badge-teal"));
                }

                var oldDesc = (prev.Description ?? string.Empty).Replace("\r\n", "\n");
                var newDesc = (curr.Description ?? string.Empty).Replace("\r\n", "\n");
                if (!string.Equals(oldDesc, newDesc, StringComparison.Ordinal))
                {
                    var differ = new Differ();
                    var inlineBuilder = new InlineDiffBuilder(differ);
                    var diffResult = inlineBuilder.BuildDiffModel(oldDesc, newDesc);

                    int oldCounter = 1;
                    int newCounter = 1;
                    int added = 0;
                    int deleted = 0;
                    var diffLines = new List<TaskHistoryDiffLine>();

                    foreach (var piece in diffResult.Lines)
                    {
                        int? oldNum = null;
                        int? newNum = null;

                        switch (piece.Type)
                        {
                            case ChangeType.Unchanged:
                                oldNum = oldCounter++;
                                newNum = newCounter++;
                                break;
                            case ChangeType.Deleted:
                                oldNum = oldCounter++;
                                deleted++;
                                break;
                            case ChangeType.Inserted:
                                newNum = newCounter++;
                                added++;
                                break;
                            case ChangeType.Modified:
                                oldNum = oldCounter++;
                                newNum = newCounter++;
                                added++;
                                deleted++;
                                break;
                        }

                        diffLines.Add(new TaskHistoryDiffLine(oldNum, newNum, piece.Type, piece.Text ?? string.Empty));
                    }

                    model.HasDescriptionDiff = true;
                    model.DescriptionLinesAdded = added;
                    model.DescriptionLinesDeleted = deleted;
                    model.DiffLines = diffLines;
                }
            }

            timeline.Add(model);
        }

        return timeline.OrderByDescending(t => t.Version).ToList();
    }

    public static string FormatRelativeTime(DateTime changedAt, DateTime? utcNow = null)
    {
        var utcChangedAt = changedAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(changedAt, DateTimeKind.Utc)
            : changedAt.ToUniversalTime();

        var now = utcNow ?? DateTime.UtcNow;
        var diff = now - utcChangedAt;

        if (diff.TotalSeconds < 0 || diff.TotalSeconds < 60)
        {
            return "just now";
        }

        if (diff.TotalMinutes < 60)
        {
            var mins = (int)diff.TotalMinutes;
            return mins <= 1 ? "1 minute ago" : $"{mins} minutes ago";
        }

        if (diff.TotalHours < 24)
        {
            var hours = (int)diff.TotalHours;
            return hours <= 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        if (diff.TotalDays < 30)
        {
            var days = (int)diff.TotalDays;
            return days <= 1 ? "yesterday" : $"{days} days ago";
        }

        return utcChangedAt.ToLocalTime().ToString("MMM d, yyyy");
    }

    public static string GetActorBadgeClass(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return "badge-gray";
        var lower = actor.ToLowerInvariant();
        if (lower == "user") return "badge-indigo";
        if (lower.Contains("orchestrator")) return "badge-teal";
        if (lower.Contains("ideation")) return "badge-purple";
        if (lower.StartsWith("agent:")) return "badge-amber";
        if (lower.Contains("file-watcher") || lower.Contains("filewatcher")) return "badge-blue";
        return "badge-gray";
    }

    public static string GetActorDisplay(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return "⚙️ system";
        var lower = actor.ToLowerInvariant();
        if (lower == "user") return "👤 User";
        if (lower.Contains("orchestrator")) return "🤖 TaskOrchestrator";
        if (lower.Contains("ideation")) return "💡 IdeationAgent";
        if (lower.StartsWith("agent:")) return $"🤖 {actor}";
        if (lower.Contains("file-watcher") || lower.Contains("filewatcher")) return "📁 file-watcher";
        return $"⚙️ {actor}";
    }

    private static List<int> ParseDependencies(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return new List<int>();
        }

        var trimmed = serialized.Trim();
        if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
        {
            return new List<int>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(trimmed) ?? new List<int>();
        }
        catch (JsonException)
        {
            return new List<int>();
        }
    }
}
