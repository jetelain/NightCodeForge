namespace NightCodeForge.Web.Services;

/// <summary>
/// Provides lightweight, deterministic lexical syntax highlighting for diff lines.
/// Wraps tokens in safe HTML spans (tok-keyword, tok-string, tok-comment, tok-type, tok-number, tok-punct)
/// with full HTML entity encoding for security.
/// </summary>
public interface ISyntaxHighlightingService
{
    /// <summary>
    /// Highlights a single line of source code based on the file path or extension.
    /// Returns sanitized HTML markup safe for rendering in Razor components.
    /// </summary>
    string HighlightLine(string? line, string? filePathOrExtension);
}
