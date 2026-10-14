using Markdig;
using Microsoft.AspNetCore.Components;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Thread-safe markdown to HTML conversion helper using Markdig with advanced extensions.
/// </summary>
public static class MarkdownHelper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>
    /// Converts markdown text into rendered HTML.
    /// </summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        return Markdown.ToHtml(markdown, Pipeline);
    }

    /// <summary>
    /// Converts markdown text into a Blazor MarkupString for safe HTML rendering in templates.
    /// </summary>
    public static MarkupString ToMarkupString(string? markdown)
    {
        return new MarkupString(ToHtml(markdown));
    }
}
