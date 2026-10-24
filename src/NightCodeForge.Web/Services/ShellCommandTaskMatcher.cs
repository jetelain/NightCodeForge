namespace NightCodeForge.Web.Services;

/// <summary>
/// Helper to determine whether a shell command's working directory belongs to a specific task.
/// Matches directory path segments exactly (e.g., 'task-1' or 'task_1') across Windows and Unix path separators,
/// preventing accidental substring matches such as 'task-10' through 'task-19' when matching task 1.
/// </summary>
public static class ShellCommandTaskMatcher
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    /// Checks whether the specified working directory belongs to the given task ID.
    /// </summary>
    /// <param name="workingDirectory">The working directory path of the command.</param>
    /// <param name="taskId">The task ID to match against.</param>
    /// <returns>True if any directory segment matches 'task-{taskId}' or 'task_{taskId}', false otherwise.</returns>
    public static bool IsCommandForTask(string? workingDirectory, int taskId)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || taskId <= 0)
        {
            return false;
        }

        var segments = workingDirectory.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        var expectedDash = $"task-{taskId}";
        var expectedUnderscore = $"task_{taskId}";

        foreach (var segment in segments)
        {
            if (string.Equals(segment, expectedDash, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, expectedUnderscore, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
