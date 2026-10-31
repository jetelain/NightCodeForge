using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// End-to-end acceptance test validating the full autonomous feature lifecycle in NightCodeForge.
/// 1. Create Workspace via UI Provisioning Wizard
/// 2. Use Ideation to Plan a Task
/// 3. Validate the Task (Auto-Merge Configuration)
/// 4. Let the Process Flow (Autonomous Pipeline Execution)
/// 5. Ensure Requested Feature Added in Dev Repo
/// </summary>
[Collection("E2E_EnvironmentSeed")]
public class AutonomousFeatureLifecycleTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IPlaywright _playwright = default!;
    private IBrowser _browser = default!;
    private IBrowserContext _context = default!;
    private IPage _page = default!;
    private SyntheticRepoFixture _repo = default!;
    private Process? _webServerProcess;
    private int? _provisionedWorkspaceId;
    private readonly string _baseUrl = Environment.GetEnvironmentVariable("NCF_BASE_URL") ?? "http://127.0.0.1:5272";
    private readonly string _workspaceSeed = Environment.GetEnvironmentVariable(NightCodeForge.Infrastructure.Data.AppDbContext.WorkspaceIdSeedEnvironmentVariable) 
        ?? NightCodeForge.Infrastructure.Data.AppDbContext.DefaultE2EWorkspaceIdSeed.ToString();
    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_e2e_{Guid.NewGuid():N}.db");
    private readonly string _tempHangfireDbPath = Path.Combine(Path.GetTempPath(), $"nightcodeforge_e2e_hangfire_{Guid.NewGuid():N}.db");

    public AutonomousFeatureLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Ensure NCF_WORKSPACE_ID_SEED is set in the test runner environment
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NightCodeForge.Infrastructure.Data.AppDbContext.WorkspaceIdSeedEnvironmentVariable)))
        {
            Environment.SetEnvironmentVariable(NightCodeForge.Infrastructure.Data.AppDbContext.WorkspaceIdSeedEnvironmentVariable, _workspaceSeed);
        }

        // 1. Initialize synthetic repository fixture
        _repo = new SyntheticRepoFixture();
        await _repo.InitializeAsync();

        // 2. Ensure NightCodeForge.Web server is responsive
        await EnsureWebServerRunningAsync();

        // 3. Initialize Playwright & Chromium
        _playwright = await Playwright.CreateAsync();
        var isHeaded = Environment.GetEnvironmentVariable("HEADED") == "true" || Environment.GetEnvironmentVariable("DEBUG") == "true";

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !isHeaded,
            SlowMo = isHeaded ? 100 : null
        });

        var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "videos");
        Directory.CreateDirectory(videoDir);

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            RecordVideoDir = videoDir
        });

        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(60000); // 60s default timeout
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    [Trait("Category", "E2E")]
    public async Task FullAutonomousFeatureLifecycle_CreatesWorkspace_PlansTask_AutoMerges_UpdatesDevRepo()
    {
        // =============================================================
        // Step 1: Create the Workspace via UI Provisioning Wizard
        // =============================================================
        await _page.GotoAsync(_baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".forge-layout");

        // Wait for Blazor interactive circuit to establish
        await _page.WaitForTimeoutAsync(1500);

        // Trigger wizard modal via top bar '+' button or empty state hero with retry until opened
        var wizardModal = _page.Locator(".forge-modal-card.wizard-card");
        var openSw = Stopwatch.StartNew();
        while (openSw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await wizardModal.CountAsync() > 0 && await wizardModal.First.IsVisibleAsync())
            {
                break;
            }

            var heroBtn = _page.Locator(".hero-launch-btn, button:has-text('Launch Workspace Creation Wizard')");
            if (await heroBtn.CountAsync() > 0 && await heroBtn.First.IsVisibleAsync())
            {
                await heroBtn.First.ClickAsync();
            }
            else
            {
                var openWizardBtn = _page.Locator("button.forge-btn-icon-addon[title*='Create']");
                if (await openWizardBtn.CountAsync() > 0 && await openWizardBtn.First.IsVisibleAsync())
                {
                    await openWizardBtn.First.ClickAsync();
                }
            }

            await Task.Delay(1000);
        }

        await wizardModal.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // Step 1.1: Probe synthetic directory
        await _page.FillAsync("#repo-path-input", _repo.RepositoryPath);
        await _page.ClickAsync("button:has-text('Probe Directory')");
        await _page.WaitForSelectorAsync(".probe-feedback-card.probe-success", new PageWaitForSelectorOptions { Timeout = 15000 });

        // Step 1.2: Set workspace name and proceed
        await _page.FillAsync("#workspace-name-input", _repo.WorkspaceName);
        await _page.ClickAsync("button:has-text('Continue →')");

        // Step 1.3: Stack & Branch verification
        await _page.WaitForSelectorAsync("#base-branch-select", new PageWaitForSelectorOptions { Timeout = 10000 });
        await _page.ClickAsync("button:has-text('Continue →')");

        // Step 1.4: Execution Environment (Container Sandbox - Recommended Default)
        await _page.WaitForSelectorAsync(".execution-modes-grid", new PageWaitForSelectorOptions { Timeout = 10000 });
        await _page.ClickAsync(".mode-card:has-text('Container Sandbox')");
        await _page.ClickAsync("button:has-text('Provision Workspace')");

        // Step 1.5: Finish provisioning & activate workspace
        var finishBtn = _page.Locator("button:has-text('Open Workspace in Forge ➔')");
        await finishBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await finishBtn.ClickAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Verify seeded workspace ID to ensure no collision with real workspaces
        var wsDropdown = _page.Locator("select.workspace-dropdown");
        if (await wsDropdown.CountAsync() > 0)
        {
            var activeWsVal = await wsDropdown.First.InputValueAsync();
            if (int.TryParse(activeWsVal, out var activeWsId) && int.TryParse(_workspaceSeed, out var expectedMinId))
            {
                _provisionedWorkspaceId = activeWsId;
                _output.WriteLine($"[E2E] Provisioned workspace ID: {activeWsId} (minimum expected seed: {expectedMinId})");
                Assert.True(activeWsId >= expectedMinId, $"Workspace ID {activeWsId} should be >= seed {expectedMinId}");
            }
        }

        // =============================================================
        // Step 2: Use Ideation to Plan a Task
        // =============================================================
        await _page.ClickAsync("a[href$='ideation']");
        await _page.WaitForSelectorAsync("textarea.chat-textarea", new PageWaitForSelectorOptions { Timeout = 15000 });

        const string prompt = "Add a Multiply(int a, int b) method to Calculator with full XML documentation and corresponding unit tests in CalculatorTests.cs.";
        await _page.FillAsync("textarea.chat-textarea", prompt);
        await _page.ClickAsync("button.send-btn");

        // Wait for proposal card to appear in right panel
        var proposalCard = _page.Locator(".proposal-card:has-text('Multiply'), .ideation-proposal-card:has-text('Multiply')");
        await proposalCard.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 90000 });

        // Ingest proposal into backlog
        var addBtn = proposalCard.First.Locator("button:has-text('Add to Backlog'), button.add-backlog-btn");
        await addBtn.ClickAsync();
        await _page.WaitForSelectorAsync(".toast-item.toast-success:has-text('Added to Backlog')", new PageWaitForSelectorOptions { Timeout = 45000 });

        // =============================================================
        // Step 3: Validate the Task (Auto-Merge)
        // =============================================================
        await _page.ClickAsync("a[href$='backlog']");
        await _page.WaitForSelectorAsync(".kanban-board-container, .kanban-board", new PageWaitForSelectorOptions { Timeout = 15000 });

        // Locate suggested task card & trigger promotion modal
        var taskCard = _page.Locator(".kanban-column:has-text('Suggested') .kanban-card:has-text('Multiply'), .kanban-column:has-text('Suggested') .task-card:has-text('Multiply')");
        await taskCard.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        await taskCard.First.Locator("button.promote-btn, button:has-text('Triage')").ClickAsync();

        // Modal configuration: Priority = 1, MergeMode = Automatic
        var modal = _page.Locator(".maintainer-promotion-modal");
        await modal.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await modal.Locator("#priority-select").SelectOptionAsync("1");
        await modal.Locator("#merge-mode-select").SelectOptionAsync("Automatic");
        await modal.Locator("button:has-text('Promote to Open & Enqueue')").ClickAsync();

        await _page.WaitForSelectorAsync(".toast-item.toast-success:has-text('ForgeTask Promoted')", new PageWaitForSelectorOptions { Timeout = 15000 });

        // =============================================================
        // Step 4: Let the Process Flow (Autonomous Pipeline Execution)
        // =============================================================
        // Wait for task to move through pipeline stages and reach Closed column in Kanban.
        // We poll periodically to provide user feedback, and fail fast if the task card shows 'Failed'.
        var terminalTaskCard = _page.Locator(".kanban-column:has-text('Closed') .kanban-card:has-text('Multiply'), .kanban-column:has-text('Closed') .task-card:has-text('Multiply')");
        var anyMultiplyCard = _page.Locator(".kanban-card:has-text('Multiply'), .task-card:has-text('Multiply')");

        var pipelineTimeout = TimeSpan.FromMinutes(25);
        var pipelineSw = Stopwatch.StartNew();
        var lastFeedbackSw = Stopwatch.StartNew();
        bool isClosed = false;

        _output.WriteLine($"[{pipelineSw.Elapsed:mm\\:ss}] Task promoted to Open & enqueued. Beginning autonomous pipeline monitoring...");
        Console.WriteLine($"[{pipelineSw.Elapsed:mm\\:ss}] Task promoted to Open & enqueued. Beginning autonomous pipeline monitoring...");

        while (pipelineSw.Elapsed < pipelineTimeout)
        {
            // Fail fast if any card on board shows Failed
            if (await anyMultiplyCard.CountAsync() > 0 && await anyMultiplyCard.First.IsVisibleAsync())
            {
                var failedBadge = anyMultiplyCard.First.Locator(".badge-crimson:has-text('Failed'), .forge-badge:has-text('Failed')");
                if (await failedBadge.CountAsync() > 0 && await failedBadge.First.IsVisibleAsync())
                {
                    Assert.Fail("Task entered Failed state during autonomous pipeline execution. Inspect artifacts/webserver.log for details.");
                }
            }

            if (await terminalTaskCard.CountAsync() > 0 && await terminalTaskCard.First.IsVisibleAsync())
            {
                var closedBadge = terminalTaskCard.First.Locator(".badge-emerald:has-text('Closed'), .forge-badge:has-text('Closed')");
                if (await closedBadge.CountAsync() > 0 && await closedBadge.First.IsVisibleAsync())
                {
                    isClosed = true;
                    var msg = $"[{pipelineSw.Elapsed:mm\\:ss}] ForgeTask successfully completed all pipeline stages and reached Closed column!";
                    _output.WriteLine(msg);
                    Console.WriteLine(msg);
                    break;
                }
            }

            // Periodically refresh backlog and emit progress updates every ~15 seconds
            if (lastFeedbackSw.Elapsed >= TimeSpan.FromSeconds(15))
            {
                lastFeedbackSw.Restart();
                var refreshBtn = _page.Locator("button:has-text('Refresh')");
                if (await refreshBtn.CountAsync() > 0 && await refreshBtn.First.IsVisibleAsync())
                {
                    try
                    {
                        await refreshBtn.First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                    }
                    catch
                    {
                        // Ignore collision if Blazor circuit is reconnecting
                    }
                }

                // Periodic page reload to ensure fresh circuit and clear any dropped connections
                if (pipelineSw.Elapsed > TimeSpan.FromSeconds(30) && (int)pipelineSw.Elapsed.TotalSeconds % 30 < 15)
                {
                    try
                    {
                        await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                        await _page.WaitForTimeoutAsync(1500);
                    }
                    catch
                    {
                        // Ignore transient connection reset if Blazor circuit is re-establishing
                    }
                }

                string cardStatus = "Pending";
                if (await anyMultiplyCard.CountAsync() > 0 && await anyMultiplyCard.First.IsVisibleAsync())
                {
                    var badges = anyMultiplyCard.First.Locator(".card-status-row .forge-badge, .card-status-row span");
                    if (await badges.CountAsync() > 0)
                    {
                        var texts = await badges.AllInnerTextsAsync();
                        cardStatus = string.Join(" | ", texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
                    }
                }
                var progressMsg = $"[{pipelineSw.Elapsed:mm\\:ss}] Autonomous pipeline progress: {cardStatus}";
                _output.WriteLine(progressMsg);
                Console.WriteLine(progressMsg);

                // If agent requested human clarification via 'ask' tool, answer it via Question Inbox (/inbox)
                if (cardStatus.Contains("Waiting for Human", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = $"[{pipelineSw.Elapsed:mm\\:ss}] Agent paused awaiting human clarification. Navigating to /inbox to submit guidance...";
                    _output.WriteLine(msg);
                    Console.WriteLine(msg);

                    var wsPrefix = _provisionedWorkspaceId.HasValue ? $"/w/{_provisionedWorkspaceId.Value}" : "";
                    await _page.GotoAsync($"{_baseUrl}{wsPrefix}/inbox", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await _page.WaitForSelectorAsync(".question-card, #inbox-empty-state", new PageWaitForSelectorOptions { Timeout = 15000 });

                    var questionCards = _page.Locator(".question-card");
                    if (await questionCards.CountAsync() > 0)
                    {
                        var textareas = _page.Locator("textarea.question-textarea");
                        var textareaCount = await textareas.CountAsync();
                        for (int i = 0; i < textareaCount; i++)
                        {
                            await textareas.Nth(i).FillAsync("Approved. Please proceed with standard Multiply(int a, int b) implementation, XML docs, and unit tests.");
                        }

                        var submitBtn = _page.Locator("button.submit-answer-btn:not([disabled])");
                        if (await submitBtn.CountAsync() > 0)
                        {
                            await submitBtn.First.ClickAsync();
                            var subMsg = $"[{pipelineSw.Elapsed:mm\\:ss}] Clarification submitted! Returning to /backlog to resume monitoring...";
                            _output.WriteLine(subMsg);
                            Console.WriteLine(subMsg);
                            await _page.WaitForTimeoutAsync(2000);
                        }
                    }

                    await _page.GotoAsync($"{_baseUrl}{wsPrefix}/backlog", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await _page.WaitForSelectorAsync(".kanban-board-container, .kanban-board", new PageWaitForSelectorOptions { Timeout = 15000 });
                }
            }

            await Task.Delay(3000);
        }

        Assert.True(isClosed, $"Task did not reach Closed status within {pipelineTimeout.TotalMinutes} minutes.");

        // =============================================================
        // Step 5: Ensure Requested Feature Added in Dev Repo
        // =============================================================
        // 1. Verify Git working tree is clean on main branch
        var status = await _repo.RunGitAsync("status --short");
        Assert.True(string.IsNullOrWhiteSpace(status), $"Working tree is dirty: {status}");

        var branch = await _repo.RunGitAsync("branch --show-current");
        Assert.Equal("main", branch);

        // 2. Verify Squash commit in log
        var log = await _repo.RunGitAsync("log -n 5 --oneline");
        Assert.True(log.Contains("Multiply", StringComparison.OrdinalIgnoreCase) || log.Contains("Auto-merge task #1", StringComparison.OrdinalIgnoreCase), $"Expected commit log to reference task: {log}");

        // 3. Verify Calculator.cs contains Multiply method & XML docs
        var calcCode = await File.ReadAllTextAsync(Path.Combine(_repo.RepositoryPath, "src", "SyntheticLib", "Calculator.cs"));
        Assert.Contains("Multiply(int a, int b)", calcCode);
        Assert.Contains("/// <summary>", calcCode);

        // 4. Verify CalculatorTests.cs contains Multiply unit test
        var testCode = await File.ReadAllTextAsync(Path.Combine(_repo.RepositoryPath, "tests", "SyntheticLib.Tests", "CalculatorTests.cs"));
        Assert.Contains("Multiply", testCode);

        // 5. Run live dotnet test CLI on host repo
        var testExitCode = await _repo.RunDotnetTestAsync();
        Assert.Equal(0, testExitCode);
    }

    private StreamWriter? _serverLogWriter;

    private async Task EnsureWebServerRunningAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var res = await http.GetAsync(_baseUrl);
            if (res.IsSuccessStatusCode)
            {
                return; // Already running
            }
        }
        catch
        {
            // Not running, need to start web host process
        }

        var webProj = FindWebProjectPath();

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{webProj}\" --no-build --urls {_baseUrl}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment[NightCodeForge.Infrastructure.Data.AppDbContext.WorkspaceIdSeedEnvironmentVariable] = _workspaceSeed;
        psi.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={_tempDbPath};";
        psi.Environment["ConnectionStrings__HangfireConnection"] = $"Data Source={_tempHangfireDbPath};";

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var logDir = Path.Combine(repoRoot, "artifacts");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "webserver.log");
        _serverLogWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        _webServerProcess = new Process { StartInfo = psi };
        _webServerProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _serverLogWriter.WriteLine($"[STDOUT] {e.Data}"); };
        _webServerProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) _serverLogWriter.WriteLine($"[STDERR] {e.Data}"); };

        if (!_webServerProcess.Start())
        {
            throw new InvalidOperationException("Failed to launch NightCodeForge.Web process.");
        }

        _webServerProcess.BeginOutputReadLine();
        _webServerProcess.BeginErrorReadLine();

        // Wait up to 45 seconds for web server to become ready
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(500);
            try
            {
                var check = await http.GetAsync(_baseUrl);
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

        throw new TimeoutException($"Timed out waiting for NightCodeForge.Web at {_baseUrl} to start.");
    }

    private static string FindWebProjectPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/NightCodeForge.Web/NightCodeForge.Web.csproj")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/NightCodeForge.Web/NightCodeForge.Web.csproj")),
            Path.GetFullPath("src/NightCodeForge.Web/NightCodeForge.Web.csproj")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        throw new FileNotFoundException("Could not locate NightCodeForge.Web.csproj from current directory.");
    }

    public async Task DisposeAsync()
    {
        if (_page != null)
        {
            try { await _page.CloseAsync(); } catch { }
        }
        if (_context != null)
        {
            try { await _context.CloseAsync(); } catch { }
        }
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

        if (_repo != null)
        {
            await _repo.DisposeAsync();
        }

        if (_provisionedWorkspaceId.HasValue)
        {
            await CleanupProvisionedWorkspaceContainersAsync(_provisionedWorkspaceId.Value);
        }

        try
        {
            var prefix = Path.GetFileNameWithoutExtension(_tempDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{prefix}*"))
            {
                File.Delete(f);
            }
            var hgPrefix = Path.GetFileNameWithoutExtension(_tempHangfireDbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{hgPrefix}*"))
            {
                File.Delete(f);
            }
        }
        catch { }
    }

    private static async Task CleanupProvisionedWorkspaceContainersAsync(int workspaceId)
    {
        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var prefix = $"ncf-sandbox-{workspaceId}";
            var cmd = $"docker ps -a --filter \"name={prefix}\" -q | xargs -r docker rm -f && rm -rf ~/.nightcodeforge/wks/{workspaceId}";
            var psi = isWindows
                ? new ProcessStartInfo("wsl.exe", $"-- bash -c \"{cmd}\"")
                : new ProcessStartInfo("bash", $"-c \"{cmd}\"");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await proc.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
            // Best-effort cleanup of E2E sandbox containers and ext4 mirror
        }
    }
}
