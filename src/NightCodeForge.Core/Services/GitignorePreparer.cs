namespace NightCodeForge.Core.Services;

using NightCodeForge.Core.Domain;

/// <summary>
/// Prepares and synchronizes repository .gitignore with Domain-mandated entries
/// (TaskSyncConstants.RequiredGitIgnoreEntries), preserving user rules outside the managed block.
/// </summary>
public static class GitignorePreparer
{
    public static string DefaultGitignoreBlock => string.Join("\n", TaskSyncConstants.RequiredGitIgnoreEntries);

    public static string Prepare(string? existingContent)
    {
        return ManagedBlockHelper.ApplyManagedBlock(existingContent, DefaultGitignoreBlock);
    }
}
