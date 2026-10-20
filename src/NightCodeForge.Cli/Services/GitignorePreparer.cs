namespace NightCodeForge.Cli.Services;

/// <summary>
/// CLI forwarding shim to <see cref="NightCodeForge.Core.Services.GitignorePreparer"/>.
/// </summary>
public static class GitignorePreparer
{
    public static string DefaultGitignoreBlock => NightCodeForge.Core.Services.GitignorePreparer.DefaultGitignoreBlock;

    public static string Prepare(string? existingContent)
        => NightCodeForge.Core.Services.GitignorePreparer.Prepare(existingContent);
}
