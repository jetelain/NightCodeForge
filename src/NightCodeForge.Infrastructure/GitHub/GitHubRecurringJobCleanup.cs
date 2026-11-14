using System.Globalization;
using Hangfire;
using Hangfire.Storage;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Shared helper to remove stale Hangfire recurring GitHub synchronization jobs belonging to deleted workspaces.
/// </summary>
internal static class GitHubRecurringJobCleanup
{
    public static void RemoveOrphanedWorkspaceRecurringJobs(
        IRecurringJobManager recurringJobManager,
        string jobIdPrefix,
        IReadOnlySet<int> activeWorkspaceIds)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobIdPrefix);
        ArgumentNullException.ThrowIfNull(activeWorkspaceIds);

        List<RecurringJobDto> existingJobs;
        try
        {
            using var connection = JobStorage.Current.GetConnection();
            existingJobs = connection.GetRecurringJobs();
        }
        catch (InvalidOperationException)
        {
            // Hangfire static JobStorage.Current is not initialized in isolated unit tests.
            return;
        }

        foreach (var job in existingJobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id) ||
                !job.Id.StartsWith(jobIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = job.Id[jobIdPrefix.Length..];
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workspaceId) &&
                !activeWorkspaceIds.Contains(workspaceId))
            {
                recurringJobManager.RemoveIfExists(job.Id);
            }
        }
    }
}
