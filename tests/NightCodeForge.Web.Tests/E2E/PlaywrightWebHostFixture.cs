using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

[CollectionDefinition("PlaywrightUiE2E")]
public class PlaywrightUiE2ECollection : ICollectionFixture<PlaywrightWebHostFixture>
{
}

/// <summary>
/// Reusable, LLM-free Playwright Web Host Fixture.
/// Boots NightCodeForge.Web against an isolated temporary SQLite database and synthetic environment,
/// exposes fast direct database seeding helpers, and manages isolated browser contexts and temp directories.
/// </summary>
public class PlaywrightWebHostFixture : IAsyncLifetime
{
    private const string DefaultWorkspaceSeed = "1001000";
    private const int StartingPort = 5274;
    private const int PortSearchRange = 100;
    private const int ServerStartupTimeoutSeconds = 45;

    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_uie2e_{Guid.NewGuid():N}.db");
    private readonly string _tempHangfireDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_uie2e_hangfire_{Guid.NewGuid():N}.db");
    private readonly ConcurrentBag<string> _tempDirectories = new();

    private Process? _webServerProcess;
    private TextWriter? _serverLogWriter;
    private IPlaywright _playwright = default!;
    private IBrowser _browser = default!;

    public string BaseUrl { get; private set; } = string.Empty;
    public string TempDbPath => _tempDbPath;

    public async Task InitializeAsync()
    {
        BaseUrl = DetermineBaseUrl();

        // 1. Start isolated NightCodeForge.Web process on BaseUrl
        await StartWebServerAsync();

        // 2. Initialize Playwright and Chromium
        _playwright = await Playwright.CreateAsync();
        var isHeaded = Environment.GetEnvironmentVariable("HEADED") == "true" || Environment.GetEnvironmentVariable("DEBUG") == "true";

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !isHeaded,
            SlowMo = isHeaded ? 100 : null
        });
    }

    public string CreateTempWorkspaceDirectory(string prefix = "synthetic_ws")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    public async Task<IBrowserContext> CreateContextAsync(
        ViewportSize? viewport = null,
        IEnumerable<KeyValuePair<string, string>>? extraHttpHeaders = null)
    {
        var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "videos");
        Directory.CreateDirectory(videoDir);

        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport ?? new ViewportSize { Width = 1440, Height = 900 },
            RecordVideoDir = videoDir,
            ExtraHTTPHeaders = extraHttpHeaders
        });
    }

    public async Task SeedDatabaseAsync(Func<AppDbContext, Task> seedAction)
    {
        ArgumentNullException.ThrowIfNull(seedAction);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_tempDbPath};Pooling=False;Default Timeout=30;")
            .Options;

        await using var context = new AppDbContext(options);
        await seedAction(context);
        await context.SaveChangesAsync();
    }

    public async Task<T> QueryDatabaseAsync<T>(Func<AppDbContext, Task<T>> queryAction)
    {
        ArgumentNullException.ThrowIfNull(queryAction);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_tempDbPath};Pooling=False;Default Timeout=30;")
            .Options;

        await using var context = new AppDbContext(options);
        return await queryAction(context);
    }

    public static async Task ClickUntilVisibleAsync(ILocator button, ILocator target, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await target.CountAsync() > 0 && await target.First.IsVisibleAsync())
            {
                return;
            }

            try
            {
                if (await button.CountAsync() > 0 && await button.First.IsVisibleAsync())
                {
                    await button.First.ClickAsync();
                }
            }
            catch (PlaywrightException)
            {
                // Transient Blazor circuit re-render while clicking
            }

            await Task.Delay(500);
        }

        await target.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
    }

    public static async Task<T?> WaitForConditionAsync<T>(Func<Task<T?>> condition, TimeSpan timeout) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var result = await condition();
            if (result != null)
            {
                return result;
            }
            await Task.Delay(300);
        }
        return await condition();
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch (Exception)
            {
                // Best-effort browser shutdown during fixture disposal
            }
        }

        _playwright?.Dispose();

        if (_webServerProcess != null)
        {
            try
            {
                if (!_webServerProcess.HasExited)
                {
                    _webServerProcess.Kill(entireProcessTree: true);
                    await _webServerProcess.WaitForExitAsync();
                }
            }
            catch (Exception)
            {
                // Best-effort process termination during fixture disposal
            }
            finally
            {
                _webServerProcess.Dispose();
            }
        }

        if (_serverLogWriter != null)
        {
            try
            {
                await _serverLogWriter.FlushAsync();
                await _serverLogWriter.DisposeAsync();
            }
            catch (Exception)
            {
                // Best-effort log stream flush on teardown
            }
        }

        TryCleanupTempFiles();
    }

    private void TryCleanupTempFiles()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            var prefix = Path.GetFileNameWithoutExtension(_tempDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{prefix}*"))
            {
                TryDeleteFileWithRetry(f);
            }

            var hgPrefix = Path.GetFileNameWithoutExtension(_tempHangfireDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{hgPrefix}*"))
            {
                TryDeleteFileWithRetry(f);
            }

            foreach (var dir in _tempDirectories)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception)
                    {
                        // Best-effort temporary workspace directory cleanup
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort temp file enumeration cleanup
        }
    }

    private static void TryDeleteFileWithRetry(string filePath)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                // Ignore if locked by OS handle release delay
                return;
            }
        }
    }

    private async Task StartWebServerAsync()
    {
        var repoRoot = FindRepoRoot();
        var webProjDir = Path.Combine(repoRoot, "src", "NightCodeForge.Web");
        var webDllPath = typeof(NightCodeForge.Web.Components.App).Assembly.Location;
        if (!File.Exists(webDllPath))
        {
            throw new FileNotFoundException($"Could not locate compiled NightCodeForge.Web.dll at {webDllPath}");
        }

        var psi = new ProcessStartInfo("dotnet", $"\"{webDllPath}\" --urls {BaseUrl} --contentRoot \"{webProjDir}\"")
        {
            WorkingDirectory = webProjDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment[AppDbContext.WorkspaceIdSeedEnvironmentVariable] = DefaultWorkspaceSeed;
        psi.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={_tempDbPath};";
        psi.Environment["ConnectionStrings__HangfireConnection"] = $"Data Source={_tempHangfireDbPath};";

        var logDir = Path.Combine(repoRoot, "artifacts");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "uie2e_webserver.log");
        var rawWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _serverLogWriter = TextWriter.Synchronized(rawWriter);

        _webServerProcess = new Process { StartInfo = psi };
        _webServerProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                try { _serverLogWriter?.WriteLine($"[STDOUT] {e.Data}"); } catch (ObjectDisposedException) { }
            }
        };
        _webServerProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                try { _serverLogWriter?.WriteLine($"[STDERR] {e.Data}"); } catch (ObjectDisposedException) { }
            }
        };

        if (!_webServerProcess.Start())
        {
            throw new InvalidOperationException("Failed to launch NightCodeForge.Web process for UI E2E tests.");
        }

        _webServerProcess.BeginOutputReadLine();
        _webServerProcess.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(ServerStartupTimeoutSeconds))
        {
            await Task.Delay(300);
            try
            {
                var check = await http.GetAsync(BaseUrl);
                if (check.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception)
            {
                if (_webServerProcess.HasExited)
                {
                    throw new InvalidOperationException($"NightCodeForge.Web exited prematurely with code {_webServerProcess.ExitCode}. Check {logPath}");
                }
            }
        }

        throw new TimeoutException($"Timed out waiting for NightCodeForge.Web at {BaseUrl} to start.");
    }

    private static string DetermineBaseUrl()
    {
        var envUrl = Environment.GetEnvironmentVariable("NCF_UIE2E_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl.TrimEnd('/');
        }

        var port = GetAvailablePort(StartingPort);
        return $"http://127.0.0.1:{port}";
    }

    private static int GetAvailablePort(int startingPort)
    {
        for (var port = startingPort; port < startingPort + PortSearchRange; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Port occupied, try next
            }
        }

        using var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        var dynamicPort = ((IPEndPoint)fallback.LocalEndpoint).Port;
        fallback.Stop();
        return dynamicPort;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NightCodeForge.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "NightCodeForge.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
