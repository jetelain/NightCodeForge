namespace NightCodeForge.Cli.Services;

/// <summary>
/// CLI forwarding shim to <see cref="NightCodeForge.Core.Services.EditorConfigPreparer"/>.
/// </summary>
public static class EditorConfigPreparer
{
    public const string DefaultEditorConfigBlock = NightCodeForge.Core.Services.EditorConfigPreparer.DefaultEditorConfigBlock;

    public static string Prepare(string? existingContent)
        => NightCodeForge.Core.Services.EditorConfigPreparer.Prepare(existingContent);
}
