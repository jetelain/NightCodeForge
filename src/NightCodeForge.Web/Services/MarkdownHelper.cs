using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Thread-safe markdown to HTML conversion helper using Markdig with advanced extensions,
/// raw HTML disabled, generic attribute parsing removed, and unsafe URL schemes stripped.
/// </summary>
public static class MarkdownHelper
{
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string MailtoScheme = "mailto";

    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

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

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .DisableHtml();

        // Prevent `{onclick=...}` / `{onerror=...}` generic attribute injection into rendered elements.
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        builder.DocumentProcessed += SanitizeDocument;
        return builder.Build();
    }

    private static void SanitizeDocument(MarkdownDocument document)
    {
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case LinkInline link:
                    var linkUrl = link.GetDynamicUrl != null ? (link.GetDynamicUrl() ?? link.Url) : link.Url;
                    if (!IsSafeUrl(linkUrl, link.IsImage))
                    {
                        link.Url = string.Empty;
                        link.GetDynamicUrl = null;
                    }
                    break;

                case AutolinkInline autolink when !IsSafeUrl(autolink.Url, isImage: false):
                    autolink.Url = string.Empty;
                    break;
            }
        }
    }

    private static bool IsSafeUrl(string? url, bool isImage)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Decode residual HTML entities and strip control/whitespace characters to defeat `java\tscript:` obfuscation.
        var decoded = WebUtility.HtmlDecode(url);
        var sb = new StringBuilder(decoded.Length);
        foreach (var ch in decoded)
        {
            if (ch > 0x20 && ch != 0x7F)
            {
                sb.Append(ch);
            }
        }

        var candidate = sb.ToString();
        if (candidate.Length == 0)
        {
            return true;
        }

        var colonIndex = candidate.IndexOf(':');
        if (colonIndex < 0)
        {
            return true;
        }

        var slashIndex = candidate.IndexOf('/');
        var queryIndex = candidate.IndexOf('?');
        var fragmentIndex = candidate.IndexOf('#');
        if ((slashIndex >= 0 && slashIndex < colonIndex) ||
            (queryIndex >= 0 && queryIndex < colonIndex) ||
            (fragmentIndex >= 0 && fragmentIndex < colonIndex))
        {
            return true;
        }

        var scheme = candidate[..colonIndex];
        return string.Equals(scheme, HttpScheme, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase) ||
               (!isImage && string.Equals(scheme, MailtoScheme, StringComparison.OrdinalIgnoreCase));
    }
}
