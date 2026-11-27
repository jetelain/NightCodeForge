using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Thread-safe markdown to HTML conversion helper using Markdig with advanced extensions,
/// raw HTML disabled, generic attribute parsing removed, context-aware link rewriting, and unsafe URL schemes stripped.
/// </summary>
public static class MarkdownHelper
{
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string MailtoScheme = "mailto";

    private static readonly MarkdownPipeline ParsePipeline = BuildParsePipeline();

    /// <summary>
    /// Converts markdown text into rendered HTML without workspace link rewriting.
    /// </summary>
    public static string ToHtml(string? markdown) => ToHtml(markdown, context: null);

    /// <summary>
    /// Converts markdown text into rendered HTML using an optional <see cref="MarkdownLinkContext"/>
    /// to rewrite workspace, memory, task, and repository links.
    /// </summary>
    public static string ToHtml(string? markdown, MarkdownLinkContext? context)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, ParsePipeline);
        ProcessDocumentLinks(document, context);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        ParsePipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// Converts markdown text into a Blazor MarkupString for safe HTML rendering in templates.
    /// </summary>
    public static MarkupString ToMarkupString(string? markdown) => new(ToHtml(markdown, context: null));

    /// <summary>
    /// Converts markdown text into a Blazor MarkupString using an optional <see cref="MarkdownLinkContext"/>.
    /// </summary>
    public static MarkupString ToMarkupString(string? markdown, MarkdownLinkContext? context) =>
        new(ToHtml(markdown, context));

    private static MarkdownPipeline BuildParsePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .DisableHtml();

        // Prevent `{onclick=...}` / `{onerror=...}` generic attribute injection into rendered elements.
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        return builder.Build();
    }

    private static void ProcessDocumentLinks(MarkdownDocument document, MarkdownLinkContext? context)
    {
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case LinkInline link:
                    ProcessLinkInline(link, context);
                    break;

                case AutolinkInline autolink when !IsSafeUrl(autolink.Url, isImage: false):
                    autolink.Url = string.Empty;
                    break;
            }
        }
    }

    private static void ProcessLinkInline(LinkInline link, MarkdownLinkContext? context)
    {
        var rawUrl = link.GetDynamicUrl != null ? (link.GetDynamicUrl() ?? link.Url) : link.Url;

        if (link.IsImage || context == null)
        {
            if (!IsSafeUrl(rawUrl, link.IsImage))
            {
                link.Url = string.Empty;
                link.GetDynamicUrl = null;
            }

            return;
        }

        var resolution = MarkdownLinkBuilder.ResolveLink(rawUrl, context);
        switch (resolution.Kind)
        {
            case MarkdownLinkResolutionKind.Neutralized:
                link.Url = string.Empty;
                link.GetDynamicUrl = null;
                break;

            case MarkdownLinkResolutionKind.Rewritten:
                if (IsSafeUrl(resolution.TargetUrl, isImage: false))
                {
                    link.Url = resolution.TargetUrl;
                }
                else
                {
                    link.Url = string.Empty;
                }

                link.GetDynamicUrl = null;
                break;

            case MarkdownLinkResolutionKind.Unchanged:
            default:
                if (!IsSafeUrl(rawUrl, isImage: false))
                {
                    link.Url = string.Empty;
                    link.GetDynamicUrl = null;
                }

                break;
        }
    }

    internal static bool IsSafeUrl(string? url, bool isImage)
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
