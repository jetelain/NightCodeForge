namespace NightCodeForge.Core.Services;

using NightCodeForge.Core.Domain;

/// <summary>
/// Prepares and synchronizes repository .editorconfig with the Domain baseline configuration,
/// preserving user rules outside the managed block.
/// </summary>
public static class EditorConfigPreparer
{
    public const string DefaultEditorConfigBlock = EditorConfigConstants.BaselineEditorConfigContent;

    public static string Prepare(string? existingContent)
    {
        return ManagedBlockHelper.ApplyManagedBlock(existingContent, DefaultEditorConfigBlock);
    }
}
