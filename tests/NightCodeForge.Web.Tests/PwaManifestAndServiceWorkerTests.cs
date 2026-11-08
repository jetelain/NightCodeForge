using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Security;
using NightCodeForge.Web.Middleware;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class PwaManifestAndServiceWorkerTests
{
    private static string FindWebRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "NightCodeForge.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate src/NightCodeForge.Web/wwwroot from " + AppContext.BaseDirectory);
    }

    private static string FindWebProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "NightCodeForge.Web");
            if (File.Exists(Path.Combine(candidate, "NightCodeForge.Web.csproj")))
            {
                return candidate;
            }

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate src/NightCodeForge.Web from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void FileExtensionContentTypeProvider_ShouldMapWebmanifestAndServiceWorkerMimeTypes()
    {
        var provider = new FileExtensionContentTypeProvider();

        var hasWebmanifest = provider.TryGetContentType("manifest.webmanifest", out var manifestContentType);
        Assert.True(hasWebmanifest, "Default FileExtensionContentTypeProvider should recognize .webmanifest");
        Assert.Equal("application/manifest+json", manifestContentType);

        var hasJs = provider.TryGetContentType("service-worker.js", out var jsContentType);
        Assert.True(hasJs, "Default FileExtensionContentTypeProvider should recognize .js");
        Assert.True(jsContentType is "text/javascript" or "application/javascript");
    }

    [Fact]
    public void ManifestFile_ShouldExistAndContainValidPwaProperties()
    {
        var wwwroot = FindWebRoot();
        var manifestPath = Path.Combine(wwwroot, "manifest.webmanifest");
        Assert.True(File.Exists(manifestPath), "manifest.webmanifest must exist in wwwroot");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("NightCodeForge Autonomous Dev", root.GetProperty("name").GetString());
        Assert.Equal("NightCodeForge", root.GetProperty("short_name").GetString());
        Assert.Equal("Autonomous AI Software Engineering Dashboard", root.GetProperty("description").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("any", root.GetProperty("orientation").GetString());
        Assert.Equal("#4f46e5", root.GetProperty("theme_color").GetString());
        Assert.Equal("#090d16", root.GetProperty("background_color").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.True(icons.Count >= 3, "Manifest should define at least 3 icons (192, 512, svg)");

        // Validate 192 icon entry
        var icon192 = icons.FirstOrDefault(i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.True(icon192.ValueKind != JsonValueKind.Undefined, "Icon 192x192 must be declared in manifest");
        Assert.Equal("icon-192.png", icon192.GetProperty("src").GetString());
        Assert.Equal("image/png", icon192.GetProperty("type").GetString());
        Assert.Contains("maskable", icon192.GetProperty("purpose").GetString() ?? string.Empty);

        // Validate 512 icon entry
        var icon512 = icons.FirstOrDefault(i => i.GetProperty("sizes").GetString() == "512x512");
        Assert.True(icon512.ValueKind != JsonValueKind.Undefined, "Icon 512x512 must be declared in manifest");
        Assert.Equal("icon-512.png", icon512.GetProperty("src").GetString());
        Assert.Equal("image/png", icon512.GetProperty("type").GetString());
        Assert.Contains("maskable", icon512.GetProperty("purpose").GetString() ?? string.Empty);

        // Validate svg entry
        var iconSvg = icons.FirstOrDefault(i => i.GetProperty("src").GetString() == "favicon.svg");
        Assert.True(iconSvg.ValueKind != JsonValueKind.Undefined, "favicon.svg must be declared in manifest");
        Assert.Equal("image/svg+xml", iconSvg.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("icon-192.png", 192, 192)]
    [InlineData("icon-512.png", 512, 512)]
    public void RasterIcons_ShouldBeValidPngFilesWithCorrectDimensions(string fileName, int expectedWidth, int expectedHeight)
    {
        var wwwroot = FindWebRoot();
        var filePath = Path.Combine(wwwroot, fileName);
        Assert.True(File.Exists(filePath), $"File {fileName} must exist in wwwroot");

        var bytes = File.ReadAllBytes(filePath);
        Assert.True(bytes.Length > 24, "PNG file must contain at least standard header and IHDR chunk");

        // Validate PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A
        ReadOnlySpan<byte> pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(pngMagic), $"{fileName} must have PNG magic bytes");

        // Validate IHDR chunk (offset 12 is 'IHDR', width at offset 16, height at offset 20)
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ServiceWorker_ShouldPrecacheOnlyExistingFiles()
    {
        var wwwroot = FindWebRoot();
        var swPath = Path.Combine(wwwroot, "service-worker.js");
        Assert.True(File.Exists(swPath), "service-worker.js must exist in wwwroot");

        var swContent = File.ReadAllText(swPath);

        // Verify key service worker logic
        Assert.Contains("caches.open", swContent);
        Assert.Contains("PRECACHE_URLS", swContent);
        Assert.Contains("self.skipWaiting()", swContent);
        Assert.Contains("self.clients.claim()", swContent);
        Assert.Contains("/_blazor", swContent);
        Assert.Contains("/hubs/", swContent);
        Assert.Contains("/hangfire", swContent);
        Assert.Contains("event.request.mode === 'navigate'", swContent);
        Assert.Contains("caches.match('/')", swContent);
        Assert.Contains("Response.error()", swContent);

        // Dynamically parse PRECACHE_URLS array from service-worker.js and ensure each static file (excluding '/') physically exists in wwwroot
        var match = Regex.Match(swContent, @"const\s+PRECACHE_URLS\s*=\s*\[(?<urls>[^\]]+)\]", RegexOptions.Singleline);
        Assert.True(match.Success, "Could not parse PRECACHE_URLS array from service-worker.js");

        var precacheList = Regex.Matches(match.Groups["urls"].Value, @"'([^']+)'|""([^""]+)""")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();

        Assert.Contains("/", precacheList);
        var staticUrls = precacheList.Where(u => u != "/").ToList();
        Assert.Equal(10, staticUrls.Count);

        foreach (var relativeUrl in staticUrls)
        {
            var relativePath = relativeUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(wwwroot, relativePath);
            Assert.True(File.Exists(fullPath), $"Precached asset '{relativeUrl}' must physically exist at '{fullPath}' to prevent 404 cache failures.");
        }
    }

    [Fact]
    public void AppRazorAndAppJs_ShouldContainManifestAndMobileMetaTagsAndServiceWorkerRegistration()
    {
        var webRoot = FindWebProjectRoot();
        var appRazorPath = Path.Combine(webRoot, "Components", "App.razor");
        Assert.True(File.Exists(appRazorPath), "App.razor must exist");

        var appRazorContent = File.ReadAllText(appRazorPath);

        Assert.Contains("<link rel=\"manifest\" href=\"manifest.webmanifest\" />", appRazorContent);
        Assert.Contains("<meta name=\"theme-color\" content=\"#4f46e5\" />", appRazorContent);
        Assert.Contains("<meta name=\"mobile-web-app-capable\" content=\"yes\" />", appRazorContent);
        Assert.Contains("<meta name=\"apple-mobile-web-app-capable\" content=\"yes\" />", appRazorContent);
        Assert.Contains("<meta name=\"apple-mobile-web-app-status-bar-style\" content=\"black-translucent\" />", appRazorContent);
        Assert.Contains("<meta name=\"apple-mobile-web-app-title\" content=\"NightCodeForge\" />", appRazorContent);
        Assert.Contains("<link rel=\"apple-touch-icon\" href=\"icon-192.png\" />", appRazorContent);
        Assert.Contains("<script src=\"js/app.js\"></script>", appRazorContent);

        var appJsPath = Path.Combine(webRoot, "wwwroot", "js", "app.js");
        Assert.True(File.Exists(appJsPath), "app.js must exist");
        var appJsContent = File.ReadAllText(appJsPath);

        Assert.Contains("navigator.serviceWorker.register('/service-worker.js')", appJsContent);
        Assert.Contains("window.isSecureContext", appJsContent);
        Assert.Contains("location.hostname === 'localhost'", appJsContent);
        Assert.Contains("location.hostname === '127.0.0.1'", appJsContent);
        Assert.Contains("location.hostname === '[::1]'", appJsContent);
    }

    [Fact]
    public async Task ContentSecurityPolicyMiddleware_ShouldIncludeManifestSrcSelf()
    {
        var context = new DefaultHttpContext();
        var options = Options.Create(new SecurityOptions { EnableContentSecurityPolicy = true });

        var middleware = new ContentSecurityPolicyMiddleware(
            next: ctx => Task.CompletedTask,
            securityOptions: options);

        await middleware.InvokeAsync(context);

        Assert.True(context.Response.Headers.ContainsKey(ContentSecurityPolicyMiddleware.HeaderName));
        var csp = context.Response.Headers[ContentSecurityPolicyMiddleware.HeaderName].ToString();
        Assert.Contains("manifest-src 'self';", csp);
        Assert.Contains("worker-src 'self' blob:;", csp);
    }
}
