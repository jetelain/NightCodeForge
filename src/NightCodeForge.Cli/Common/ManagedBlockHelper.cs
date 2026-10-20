namespace NightCodeForge.Cli.Common;

/// <summary>
/// CLI forwarding shim to <see cref="NightCodeForge.Core.Services.ManagedBlockHelper"/>.
/// </summary>
public static class ManagedBlockHelper
{
    public static string ApplyManagedBlock(string? existingContent, string blockContent)
        => NightCodeForge.Core.Services.ManagedBlockHelper.ApplyManagedBlock(existingContent, blockContent);

    public static string? ExtractManagedBlock(string? content)
        => NightCodeForge.Core.Services.ManagedBlockHelper.ExtractManagedBlock(content);
}
