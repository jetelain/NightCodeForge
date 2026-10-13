using System.Text;

namespace NightCodeForge.Agents.Tools.EditorConfig;

public enum EndOfLine { Lf, CrLf, Cr }
public enum IndentStyle { Space, Tab }

public record EditorConfigSettings(
    EndOfLine? EndOfLine = null,
    IndentStyle? IndentStyle = null,
    int? IndentSize = null,
    int? TabWidth = null,
    bool? InsertFinalNewline = null,
    bool? TrimTrailingWhitespace = null,
    Encoding? Charset = null);

public interface IEditorConfigService
{
    Task<EditorConfigSettings> GetSettingsForPathAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IEditorConfigFileApplier
{
    string ApplyFormatting(string content, EditorConfigSettings settings, string? existingContent = null);
    byte[] ApplyFormattingAndEncode(string content, EditorConfigSettings settings, string? existingContent = null);
}
