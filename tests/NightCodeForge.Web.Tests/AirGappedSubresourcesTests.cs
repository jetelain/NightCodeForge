using System.Text.RegularExpressions;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class AirGappedSubresourcesTests
{
    private static readonly Regex HtmlSubresourcePattern = new(
        @"<(?:link|script|img|audio|video|embed|object|iframe|source|track)\s+[^>]*\b(?:href|src|data)\s*=\s*[""']https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssSubresourcePattern = new(
        @"(?:@import\s+(?:url\()?[""']?https?://|url\(\s*[""']?https?://)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsSubresourcePattern = new(
        @"(?:import\s+(?:[^;]*from\s+)?[""']https?://|import\(\s*[""']https?://)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlCommentsPattern = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CssCommentsPattern = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex JsCommentsPattern = new(
        @"/\*.*?\*/|//.*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void WebApp_ShouldNotReferenceExternalHttpResources_InTemplatesOrStylesheets()
    {
        var webRoot = FindWebProjectRoot();
        var files = Directory.GetFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".razor" or ".html" or ".css" or ".js")) return false;
                var normalized = f.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/") || normalized.Contains("/wwwroot/lib/"))
                    return false;
                return true;
            })
            .ToList();

        Assert.NotEmpty(files);

        var violations = new List<string>();

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            switch (ext)
            {
                case ".razor":
                case ".html":
                {
                    var markup = ext == ".razor" ? StripRazorCodeBlock(content) : content;
                    markup = HtmlCommentsPattern.Replace(markup, string.Empty);

                    var matches = HtmlSubresourcePattern.Matches(markup);
                    foreach (Match m in matches)
                    {
                        violations.Add($"{file}: HTML subresource '{m.Value}'");
                    }

                    var cssMatches = CssSubresourcePattern.Matches(markup);
                    foreach (Match m in cssMatches)
                    {
                        violations.Add($"{file}: Inline CSS subresource '{m.Value}'");
                    }
                    break;
                }
                case ".css":
                {
                    var cleanCss = CssCommentsPattern.Replace(content, string.Empty);
                    var matches = CssSubresourcePattern.Matches(cleanCss);
                    foreach (Match m in matches)
                    {
                        violations.Add($"{file}: CSS external reference '{m.Value}'");
                    }
                    break;
                }
                case ".js":
                {
                    var cleanJs = JsCommentsPattern.Replace(content, string.Empty);
                    var matches = JsSubresourcePattern.Matches(cleanJs);
                    foreach (Match m in matches)
                    {
                        violations.Add($"{file}: JS external import '{m.Value}'");
                    }
                    break;
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} external subresource references in air-gapped web assets:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void BundledFonts_ShouldExistInWwwrootFontsDirectory()
    {
        var webRoot = FindWebProjectRoot();
        var fontsDir = Path.Combine(webRoot, "wwwroot", "fonts");

        Assert.True(Directory.Exists(fontsDir), $"Fonts directory does not exist: {fontsDir}");

        var interFont = Path.Combine(fontsDir, "inter-latin-variable.woff2");
        Assert.True(File.Exists(interFont), $"Missing Inter variable font: {interFont}");
        var interLen = new FileInfo(interFont).Length;
        Assert.True(interLen > 20000, $"Inter font file is suspiciously small: {interLen} bytes");

        var jetbrainsFont = Path.Combine(fontsDir, "jetbrains-mono-latin-variable.woff2");
        Assert.True(File.Exists(jetbrainsFont), $"Missing JetBrains Mono variable font: {jetbrainsFont}");
        var jetbrainsLen = new FileInfo(jetbrainsFont).Length;
        Assert.True(jetbrainsLen > 20000, $"JetBrains Mono font file is suspiciously small: {jetbrainsLen} bytes");
    }

    [Fact]
    public void FontsCss_ShouldDeclareLocalFontFaceDefinitions_WithFontDisplaySwap()
    {
        var webRoot = FindWebProjectRoot();
        var fontsCssPath = Path.Combine(webRoot, "wwwroot", "css", "fonts.css");

        Assert.True(File.Exists(fontsCssPath), $"Missing fonts.css at {fontsCssPath}");

        var css = File.ReadAllText(fontsCssPath);
        Assert.Contains("@font-face", css);
        Assert.Contains("font-display: swap;", css);
        Assert.Contains("inter-latin-variable.woff2", css);
        Assert.Contains("jetbrains-mono-latin-variable.woff2", css);
        Assert.Contains("'Inter'", css);
        Assert.Contains("'JetBrains Mono'", css);
    }

    [Fact]
    public void ThemeCss_ShouldDefineRobustNativeSystemFallbacks()
    {
        var webRoot = FindWebProjectRoot();
        var themeCssPath = Path.Combine(webRoot, "wwwroot", "css", "theme.css");

        Assert.True(File.Exists(themeCssPath), $"Missing theme.css at {themeCssPath}");

        var css = File.ReadAllText(themeCssPath);
        Assert.Contains("--font-sans:", css);
        Assert.Contains("--font-mono:", css);
        Assert.Contains("system-ui", css);
        Assert.Contains("ui-monospace", css);
    }

    [Fact]
    public void AppRazor_ShouldNotReferenceGoogleFonts_AndShouldIncludeFontsCss()
    {
        var webRoot = FindWebProjectRoot();
        var appRazorPath = Path.Combine(webRoot, "Components", "App.razor");

        Assert.True(File.Exists(appRazorPath), $"Missing App.razor at {appRazorPath}");

        var content = File.ReadAllText(appRazorPath);
        Assert.DoesNotContain("fonts.googleapis.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.gstatic.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("css/fonts.css", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2\" />", true)]
    [InlineData("<script src=\"http://cdn.example.com/bundle.js\"></script>", true)]
    [InlineData("<img src=\"https://evil.com/tracker.png\" />", true)]
    [InlineData("<iframe src=\"https://external.com/embed\"></iframe>", true)]
    [InlineData("<a href=\"https://github.com/project\">Source Code</a>", false)]
    [InlineData("<input placeholder=\"https://api.openai.com/v1\" />", false)]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", false)]
    public void Scanner_ShouldAccuratelyDiscriminateExternalSubresources(string htmlSnippet, bool shouldFlag)
    {
        var match = HtmlSubresourcePattern.IsMatch(htmlSnippet);
        Assert.Equal(shouldFlag, match);
    }

    private static string FindWebProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var directWebProj = Path.Combine(current.FullName, "NightCodeForge.Web.csproj");
            if (File.Exists(directWebProj))
            {
                return current.FullName;
            }

            var srcWebProj = Path.Combine(current.FullName, "src", "NightCodeForge.Web", "NightCodeForge.Web.csproj");
            if (File.Exists(srcWebProj))
            {
                return Path.Combine(current.FullName, "src", "NightCodeForge.Web");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate NightCodeForge.Web project directory from " + AppContext.BaseDirectory);
    }

    private static string StripRazorCodeBlock(string content)
    {
        var idx = content.IndexOf("@code", StringComparison.Ordinal);
        while (idx >= 0)
        {
            var braceStart = content.IndexOf('{', idx);
            if (braceStart < 0) break;

            var braceCount = 1;
            var cur = braceStart + 1;
            while (cur < content.Length && braceCount > 0)
            {
                if (content[cur] == '{') braceCount++;
                else if (content[cur] == '}') braceCount--;
                cur++;
            }

            content = content.Remove(idx, cur - idx);
            idx = content.IndexOf("@code", idx, StringComparison.Ordinal);
        }

        return content;
    }
}

