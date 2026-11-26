using System.Diagnostics;
using System.Text.Json;

namespace NightCodeForge.Benchmark.Cli.Tests.Datasets;

public sealed class ValidateGoldenScriptTests : IDisposable
{
    private readonly string _tempDir;

    public ValidateGoldenScriptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf_validate_golden_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of temp test directory.
        }
    }

    [Fact]
    public async Task ValidateGolden_Resume_PreservesSuiteDatasetManifestAndNoVerifyFromPreviousSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "benchmarks", "validate-golden.ps1");
        var datasetPath = Path.Combine(_tempDir, "custom-smoke.jsonl");
        var outDir = Path.Combine(_tempDir, "resume-out");
        Directory.CreateDirectory(outDir);

        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"custom-py-001","category":"Implementation","language":"python","repo":"pallets/flask","base_commit":"abc123","problem_statement":"Fix bug"}""");

        var summaryJsonPath = Path.Combine(outDir, "summary.json");
        var initialSummary = JsonSerializer.Serialize(new
        {
            suite = "v1-smoke",
            dataset = datasetPath,
            manifest = "",
            verify_with_tests = false,
            configuration = GetBuildConfiguration(),
            started_utc = "2026-11-26T00:00:00Z",
            updated_utc = "2026-11-26T00:01:00Z",
            total_selected = 1,
            passed = 1,
            failed = 0,
            pending = 0,
            resumed_skipped = 0,
            total_duration_ms = 123,
            results = new[]
            {
                new
                {
                    instance_id = "custom-py-001",
                    category = "Implementation",
                    language = "python",
                    repo = "pallets/flask",
                    status = "Passed",
                    duration_ms = 123,
                    resumed = false,
                    detail = "GoldPatch applied (--no-verify)."
                }
            }
        });
        await File.WriteAllTextAsync(summaryJsonPath, initialSummary);

        var (exitCode, stdout, stderr) = await RunPowerShellScriptAsync(
            scriptPath,
            ["-Resume", outDir, "-NoBuild", "-Configuration", GetBuildConfiguration()]);

        Assert.True(exitCode == 0, $"validate-golden.ps1 -Resume failed (exit {exitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryJsonPath));
        var root = doc.RootElement;
        Assert.Equal("v1-smoke", root.GetProperty("suite").GetString());
        Assert.Equal(Path.GetFullPath(datasetPath), root.GetProperty("dataset").GetString());
        Assert.Equal(string.Empty, root.GetProperty("manifest").GetString());
        Assert.False(root.GetProperty("verify_with_tests").GetBoolean());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(1, root.GetProperty("resumed_skipped").GetInt32());

        var summaryMd = await File.ReadAllTextAsync(Path.Combine(outDir, "summary.md"));
        Assert.Contains("Patch & frontmatter only (--no-verify)", summaryMd);
    }

    [Fact]
    public async Task ValidateGolden_CustomDatasetNoVerifyFailure_EmitsAccurateRerunCommandsInSummaryMarkdown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "benchmarks", "validate-golden.ps1");
        var datasetPath = Path.Combine(_tempDir, "failing-custom.jsonl");
        var missingRepoPath = Path.Combine(_tempDir, "nonexistent-repo");
        var outDir = Path.Combine(_tempDir, "fail-out");

        var instanceJson = JsonSerializer.Serialize(new
        {
            instance_id = "custom-fail-001",
            category = "Implementation",
            language = "csharp",
            repo = missingRepoPath,
            base_commit = "abc123",
            problem_statement = "Fix bug",
            patch = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b"
        });
        await File.WriteAllTextAsync(datasetPath, instanceJson);

        var (exitCode, stdout, stderr) = await RunPowerShellScriptAsync(
            scriptPath,
            ["-Dataset", datasetPath, "-NoVerify", "-NoBuild", "-Configuration", GetBuildConfiguration(), "-OutDir", outDir]);

        Assert.True(exitCode == 1, $"Expected exit code 1 on failing instance, got {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "summary.json")));
        Assert.Equal("failing-custom", summaryDoc.RootElement.GetProperty("suite").GetString());

        var summaryMd = await File.ReadAllTextAsync(Path.Combine(outDir, "summary.md"));
        Assert.Contains($"-Dataset \"{Path.GetFullPath(datasetPath)}\"", summaryMd);
        Assert.Contains("-Filter \"custom-fail-001\"", summaryMd);
        Assert.Contains("-NoVerify", summaryMd);
        Assert.DoesNotContain("-CleanContainers", summaryMd);
        Assert.DoesNotContain("-Suite v1-full", summaryMd);
        Assert.Contains($"-Resume \"{Path.GetFullPath(outDir)}\" -NoVerify", summaryMd);
    }

    [Fact]
    public async Task ValidateGolden_MixedCategoryDataset_CategoryFilterDoesNotOverrideOtherCategoriesAndSkipsManifestOnlyEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoDir = Path.Combine(_tempDir, "local-repo");
        Directory.CreateDirectory(repoDir);
        RunGit(repoDir, "init");
        RunGit(repoDir, "config user.email test@example.com");
        RunGit(repoDir, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "a.txt"), "hello\n");
        RunGit(repoDir, "add -A");
        RunGit(repoDir, "commit -m \"Initial\"");
        var baseCommit = RunGit(repoDir, "rev-parse HEAD").Trim();

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "benchmarks", "validate-golden.ps1");
        var datasetPath = Path.Combine(_tempDir, "mixed.jsonl");
        var manifestPath = Path.Combine(_tempDir, "mixed.manifest.json");
        var outDir = Path.Combine(_tempDir, "mixed-out");

        var implLine = JsonSerializer.Serialize(new
        {
            instance_id = "impl-001",
            category = "Implementation",
            language = "csharp",
            repo = repoDir,
            base_commit = baseCommit,
            problem_statement = "Fix bug",
            patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-hello\n+world\n"
        });
        var taskReviewLine = JsonSerializer.Serialize(new
        {
            instance_id = "tr-001",
            category = "TaskReview",
            language = "csharp",
            repo = repoDir,
            base_commit = baseCommit,
            problem_statement = "Review task",
            task_specification = "---\nid: 42\ntitle: \"Sample Task\"\nstatus: Open\ndifficulty: standard\n---\n\n# Overview\n\nClarify edge case.",
            expected_verdict = "Ready",
            expected_difficulty = "standard"
        });
        await File.WriteAllLinesAsync(datasetPath, [implLine, taskReviewLine]);

        var manifestJson = JsonSerializer.Serialize(new
        {
            suite_name = "mixed",
            version = "1.0.0",
            entries = new object[]
            {
                new
                {
                    instance_id = "impl-001",
                    source = "swe-sharp-bench",
                    category = "Implementation",
                    language = "csharp"
                },
                new
                {
                    instance_id = "tr-001",
                    source = "crafted-task-review",
                    category = "TaskReview",
                    language = "csharp"
                },
                new
                {
                    instance_id = "missing-from-jsonl-001",
                    source = "crafted-task-review",
                    category = "TaskReview",
                    language = "csharp"
                }
            }
        });
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        var (exitCode, stdout, stderr) = await RunPowerShellScriptAsync(
            scriptPath,
            ["-Dataset", datasetPath, "-Manifest", manifestPath, "-Category", "task-review", "-NoVerify", "-NoBuild", "-Configuration", GetBuildConfiguration(), "-OutDir", outDir]);

        Assert.True(exitCode == 0, $"Expected exit code 0 for mixed-category TaskReview validation, got {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "summary.json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("total_selected").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        var single = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal("tr-001", single.GetProperty("instance_id").GetString());
        Assert.Equal("Passed", single.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ValidateGolden_ResumeWithFilter_WritesToNewOutDirWithoutClobberingSourceSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "benchmarks", "validate-golden.ps1");
        var datasetPath = Path.Combine(_tempDir, "two-instances.jsonl");
        var sourceOutDir = Path.Combine(_tempDir, "source-resume-out");
        Directory.CreateDirectory(sourceOutDir);

        await File.WriteAllLinesAsync(datasetPath,
        [
            """{"instance_id":"inst-1","category":"Implementation","language":"python","repo":"pallets/flask","base_commit":"abc123","problem_statement":"Fix 1"}""",
            """{"instance_id":"inst-2","category":"Implementation","language":"python","repo":"pallets/flask","base_commit":"def456","problem_statement":"Fix 2"}"""
        ]);

        var sourceSummaryPath = Path.Combine(sourceOutDir, "summary.json");
        var initialSummary = JsonSerializer.Serialize(new
        {
            suite = "two-instances",
            dataset = datasetPath,
            manifest = "",
            verify_with_tests = false,
            configuration = GetBuildConfiguration(),
            started_utc = "2026-11-26T00:00:00Z",
            updated_utc = "2026-11-26T00:01:00Z",
            total_selected = 2,
            passed = 2,
            failed = 0,
            pending = 0,
            resumed_skipped = 0,
            total_duration_ms = 200,
            results = new[]
            {
                new { instance_id = "inst-1", category = "Implementation", language = "python", repo = "pallets/flask", status = "Passed", duration_ms = 100, resumed = false, detail = "ok" },
                new { instance_id = "inst-2", category = "Implementation", language = "python", repo = "pallets/flask", status = "Passed", duration_ms = 100, resumed = false, detail = "ok" }
            }
        });
        await File.WriteAllTextAsync(sourceSummaryPath, initialSummary);

        string? generatedOutDir = null;
        try
        {
            var (exitCode, stdout, stderr) = await RunPowerShellScriptAsync(
                scriptPath,
                ["-Resume", sourceOutDir, "-Filter", "inst-1", "-NoBuild", "-Configuration", GetBuildConfiguration()]);

            Assert.True(exitCode == 0, $"Filtered -Resume failed (exit {exitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            var outDirLine = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("OUT_DIR=", StringComparison.Ordinal));
            Assert.NotNull(outDirLine);
            generatedOutDir = outDirLine["OUT_DIR=".Length..].Trim();
            Assert.False(
                string.Equals(Path.GetFullPath(sourceOutDir), Path.GetFullPath(generatedOutDir), StringComparison.OrdinalIgnoreCase),
                $"Filtered -Resume must not overwrite source directory '{sourceOutDir}'.");

            using var sourceDoc = JsonDocument.Parse(await File.ReadAllTextAsync(sourceSummaryPath));
            Assert.Equal(2, sourceDoc.RootElement.GetProperty("total_selected").GetInt32());
            Assert.Equal(2, sourceDoc.RootElement.GetProperty("passed").GetInt32());

            using var filteredDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(generatedOutDir, "summary.json")));
            Assert.Equal(1, filteredDoc.RootElement.GetProperty("total_selected").GetInt32());
            Assert.Equal(1, filteredDoc.RootElement.GetProperty("passed").GetInt32());
            Assert.Equal(1, filteredDoc.RootElement.GetProperty("resumed_skipped").GetInt32());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(generatedOutDir) && Directory.Exists(generatedOutDir))
            {
                try
                {
                    Directory.Delete(generatedOutDir, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of generated validation output folder.
                }
            }
        }
    }

    [Fact]
    public async Task ValidateGolden_ConcurrencyTwoNoVerify_ValidatesBatchAndRecordsConcurrencyInSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoDir = Path.Combine(_tempDir, "local-repo-conc");
        Directory.CreateDirectory(repoDir);
        RunGit(repoDir, "init");
        RunGit(repoDir, "config user.email test@example.com");
        RunGit(repoDir, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "a.txt"), "hello\n");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "b.txt"), "foo\n");
        RunGit(repoDir, "add -A");
        RunGit(repoDir, "commit -m \"Initial\"");
        var baseCommit = RunGit(repoDir, "rev-parse HEAD").Trim();

        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "benchmarks", "validate-golden.ps1");
        var datasetPath = Path.Combine(_tempDir, "conc.jsonl");
        var outDir = Path.Combine(_tempDir, "conc-out");

        var line1 = JsonSerializer.Serialize(new
        {
            instance_id = "conc-001",
            category = "Implementation",
            language = "csharp",
            repo = repoDir,
            base_commit = baseCommit,
            problem_statement = "Fix a",
            patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-hello\n+world\n"
        });
        var line2 = JsonSerializer.Serialize(new
        {
            instance_id = "conc-002",
            category = "Implementation",
            language = "csharp",
            repo = repoDir,
            base_commit = baseCommit,
            problem_statement = "Fix b",
            patch = "diff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-foo\n+bar\n"
        });
        await File.WriteAllLinesAsync(datasetPath, [line1, line2]);

        var (exitCode, stdout, stderr) = await RunPowerShellScriptAsync(
            scriptPath,
            ["-Dataset", datasetPath, "-Concurrency", "2", "-NoVerify", "-NoBuild", "-Configuration", GetBuildConfiguration(), "-OutDir", outDir]);

        Assert.True(exitCode == 0, $"validate-golden.ps1 -Concurrency 2 failed (exit {exitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "summary.json")));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("concurrency").GetInt32());
        Assert.Equal(2, root.GetProperty("total_selected").GetInt32());
        Assert.Equal(2, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());

        var summaryMd = await File.ReadAllTextAsync(Path.Combine(outDir, "summary.md"));
        Assert.Contains("- **Concurrency:** 2", summaryMd);
    }

    private static string GetBuildConfiguration() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellScriptAsync(
        string scriptPath,
        IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string RunGit(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed ({proc.ExitCode}): {stderr}");
        }

        return stdout;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "benchmarks", "validate-golden.ps1")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing benchmarks/validate-golden.ps1.");
    }
}
