using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
/// exposes fast direct database seeding helpers, and manages isolated browser contexts.
/// </summary>
public class PlaywrightWebHostFixture : IAsyncLifetime
{
    private const string DefaultWorkspaceSeed = "1001000";
    private const int StartingPort = 5274;
    private const int PortSearchRange = 100;
    private const int ServerStartupTimeoutSeconds = 45;

    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_uie2e_{Guid.NewGuid():N}.db");
    private readonly string _tempHangfireDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_uie2e_hangfire_{Guid.NewGuid():N}.db");

    private Process? _webServerProcess;
    private StreamWriter? _serverLogWriter;
    private IPlaywright _playwright = default!;
    private IBrowser _browser = default!;

    public string BaseUrl { get; private set; } = string.Empty;
    public string TempDbPath => _tempDbPath;

    public async Task InitializeAsync()
    {
        BaseUrl = DetermineBaseUrl();

        // 1. Ensure NightCodeForge.Web process is running on BaseUrl
        await EnsureWebServerRunningAsync();

        // 2. Initialize Playwright and Chromium
        _playwright = await Playwright.CreateAsync();
        var isHeaded = Environment.GetEnvironmentVariable("HEADED") == "true" || Environment.GetEnvironmentVariable("DEBUG") == "true";

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !isHeaded,
            SlowMo = isHeaded ? 100 : null
        });
    }

    public async Task<IBrowserContext> CreateContextAsync(ViewportSize? viewport = null)
    {
        var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "videos");
        Directory.CreateDirectory(videoDir);

        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport ?? new ViewportSize { Width = 1440, Height = 900 },
            RecordVideoDir = videoDir
        });
    }

    public async Task SeedDatabaseAsync(Func<AppDbContext, Task> seedAction)
    {
        ArgumentNullException.ThrowIfNull(seedAction);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_tempDbPath};")
            .Options;

        await using var context = new AppDbContext(options);
        await seedAction(context);
        await context.SaveChangesAsync();
    }

    public async Task<T> QueryDatabaseAsync<T>(Func<AppDbContext, Task<T>> queryAction)
    {
        ArgumentNullException.ThrowIfNull(queryAction);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_tempDbPath};")
            .Options;

        await using var context = new AppDbContext(options);
        return await queryAction(context);
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            try { await _browser.CloseAsync(); } catch { }
        }

        _playwright?.Dispose();

        if (_webServerProcess != null && !_webServerProcess.HasExited)
        {
            try
            {
                _webServerProcess.Kill(entireProcessTree: true);
                await _webServerProcess.WaitForExitAsync();
            }
            catch { }
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
            catch { }
        }

        TryCleanupTempFiles();
    }

    private void TryCleanupTempFiles()
    {
        try
        {
            var prefix = Path.GetFileNameWithoutExtension(_tempDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{prefix}*"))
            {
                try { File.Delete(f); } catch { }
            }

            var hgPrefix = Path.GetFileNameWithoutExtension(_tempHangfireDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{hgPrefix}*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch
        {
            // Best effort temp db file cleanup
        }
    }

    private async Task EnsureWebServerRunningAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var res = await http.GetAsync(BaseUrl);
            if (res.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch
        {
            // Web server not yet running; spawn new process
        }

        var repoRoot = FindRepoRoot();
        var webProj = Path.Combine(repoRoot, "src", "NightCodeForge.Web", "NightCodeForge.Web.csproj");
        if (!File.Exists(webProj))
        {
            throw new FileNotFoundException($"Could not locate NightCodeForge.Web.csproj at {webProj}");
        }

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{webProj}\" --no-build --urls {BaseUrl}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.Environment[AppDbContext.WorkspaceIdSeedEnvironmentVariable] = DefaultWorkspaceSeed;
        psi.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={_tempDbPath};";
        psi.Environment["ConnectionStrings__HangfireConnection"] = $"Data Source={_tempHangfireDbPath};";

        var logDir = Path.Combine(repoRoot, "artifacts");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "uie2e_webserver.log");
        _serverLogWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        _webServerProcess = new Process { StartInfo = psi };
        _webServerProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _serverLogWriter.WriteLine($"[STDOUT] {e.Data}"); };
        _webServerProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) _serverLogWriter.WriteLine($"[STDERR] {e.Data}"); };

        if (!_webServerProcess.Start())
        {
            throw new InvalidOperationException("Failed to launch NightCodeForge.Web process for UI E2E tests.");
        }

        _webServerProcess.BeginOutputReadLine();
        _webServerProcess.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(ServerStartupTimeoutSeconds))
        {
            await Task.Delay(500);
            try
            {
                var check = await http.GetAsync(BaseUrl);
                if (check.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
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
            catch
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
