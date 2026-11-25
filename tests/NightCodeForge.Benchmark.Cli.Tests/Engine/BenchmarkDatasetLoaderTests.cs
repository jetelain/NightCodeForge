using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkDatasetLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BenchmarkDatasetLoader _loader = new();

    public BenchmarkDatasetLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-ds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_JsonlImplementation_ParsesJsonArraysAndEncodedStringArrays()
    {
        var path = Path.Combine(_tempDir, "impl.jsonl");
        var content = string.Join(Environment.NewLine,
        [
            """{"instance_id":"repo__1","language":"csharp","repo":"owner/repo","base_commit":"abc123","problem_statement":"Fix bug 1","hints_text":"Hint","patch":"diff1","test_patch":"testdiff1","fail_to_pass":"[\"TestA\",\"TestB\"]","pass_to_pass":["TestC"],"custom_container_image":"img:v1","custom_test_command":"dotnet test","test_result_format":"trx"}""",
            "",
            """{"instance_id":"repo__2","language":"python","repo":"owner/pyrepo","base_commit":"def456","problem_statement":"Fix py bug","fail_to_pass":["test_x"],"pass_to_pass":"[\"test_y\"]"}"""
        ]);
        await File.WriteAllTextAsync(path, content);

        var items = await _loader.LoadAsync(path);

        Assert.Equal(2, items.Count);
        Assert.Equal("repo__1", items[0].InstanceId);
        Assert.Equal(BenchmarkLanguages.CSharp, items[0].Language);
        Assert.Equal(["TestA", "TestB"], items[0].FailToPass);
        Assert.Equal(["TestC"], items[0].PassToPass);
        Assert.Equal("diff1", items[0].GoldPatch);
        Assert.Equal("testdiff1", items[0].TestPatch);
        Assert.Equal("img:v1", items[0].CustomContainerImage);
        Assert.Equal("dotnet test", items[0].CustomTestCommand);
        Assert.Equal(TestResultFormat.Trx, items[0].TestResultFormat);

        Assert.Equal(BenchmarkLanguages.Python, items[1].Language);
        Assert.Equal(["test_x"], items[1].FailToPass);
        Assert.Equal(["test_y"], items[1].PassToPass);
        Assert.Equal(TestResultFormat.JUnitXml, items[1].ResolveEffectiveTestResultFormat());
    }

    [Fact]
    public async Task LoadAsync_JsonCodeReviewAndTaskReview_ParsesAllCategoryFieldsAndFindings()
    {
        var path = Path.Combine(_tempDir, "reviews.json");
        var json = """
        [
          {
            "instance_id": "cr-1",
            "category": "code_review",
            "language": "typescript",
            "repo": "org/ts-app",
            "base_commit": "111aaa",
            "problem_statement": "Review PR",
            "candidate_patch": "diff --git a/a.ts b/a.ts\n",
            "expected_verdict": "RequestChanges",
            "expected_findings": [
              {
                "file_path": "src/auth.ts",
                "symbol_or_line_hint": "verifyToken",
                "severity": "high",
                "summary": "Missing signature check",
                "keywords": ["signature", "verify"]
              }
            ]
          },
          {
            "instance_id": "tr-1",
            "category": "task_review",
            "language": "rust",
            "repo": "org/rust-app",
            "base_commit": "222bbb",
            "problem_statement": "Spec review",
            "task_specification": "# Add retry logic",
            "expected_verdict": "Ready",
            "expected_difficulty": "difficult",
            "expected_findings": [
              {
                "file_path": "src/client.rs",
                "keywords": ["backoff"]
              }
            ]
          }
        ]
        """;
        await File.WriteAllTextAsync(path, json);

        var items = await _loader.LoadAsync(path);

        Assert.Equal(2, items.Count);
        Assert.Equal(BenchmarkProblemCategory.CodeReview, items[0].Category);
        Assert.Equal(BenchmarkLanguages.TypeScript, items[0].Language);
        Assert.Single(items[0].ExpectedFindings);
        Assert.Equal("src/auth.ts", items[0].ExpectedFindings[0].FilePath);
        Assert.Equal(["signature", "verify"], items[0].ExpectedFindings[0].Keywords);

        Assert.Equal(BenchmarkProblemCategory.TaskReview, items[1].Category);
        Assert.Equal(BenchmarkLanguages.Rust, items[1].Language);
        Assert.Equal("difficult", items[1].ExpectedDifficulty);
        Assert.Equal(TestResultFormat.CargoTest, items[1].ResolveEffectiveTestResultFormat());
    }

    [Fact]
    public async Task LoadAsync_FailsFastOnMissingFileEmptyDatasetDuplicateIdsOrInvalidSchema()
    {
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            _loader.LoadAsync(Path.Combine(_tempDir, "does-not-exist.jsonl")));

        var emptyFile = Path.Combine(_tempDir, "empty.jsonl");
        await File.WriteAllTextAsync(emptyFile, "\n   \n");
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(emptyFile));

        var dupFile = Path.Combine(_tempDir, "dup.jsonl");
        await File.WriteAllTextAsync(dupFile,
            """{"instance_id":"dup-1","repo":"a/b","base_commit":"c1","problem_statement":"p1"}""" + Environment.NewLine +
            """{"instance_id":"dup-1","repo":"a/b","base_commit":"c2","problem_statement":"p2"}""");
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(dupFile));

        var badCrFile = Path.Combine(_tempDir, "bad-cr.jsonl");
        await File.WriteAllTextAsync(badCrFile,
            """{"instance_id":"cr-bad","category":"code_review","repo":"a/b","base_commit":"c1","problem_statement":"p1","candidate_patch":"diff","expected_verdict":"UnknownVerdict"}""");
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(badCrFile));

        var badArrayString = Path.Combine(_tempDir, "bad-arr.jsonl");
        await File.WriteAllTextAsync(badArrayString,
            """{"instance_id":"impl-bad","repo":"a/b","base_commit":"c1","problem_statement":"p1","fail_to_pass":"not-json-array"}""");
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(badArrayString));

        var emptyFindingFile = Path.Combine(_tempDir, "empty-finding.jsonl");
        await File.WriteAllTextAsync(emptyFindingFile,
            """{"instance_id":"cr-empty-finding","category":"code_review","repo":"a/b","base_commit":"c1","problem_statement":"p1","candidate_patch":"diff","expected_verdict":"RequestChanges","expected_findings":[{}]}""");
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(emptyFindingFile));

        var nonStringPropFile = Path.Combine(_tempDir, "non-string-prop.jsonl");
        await File.WriteAllTextAsync(nonStringPropFile,
            """{"instance_id":"inst-num","repo":"a/b","base_commit":"c1","problem_statement":"p1","hints_text":123}""");
        var ex = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(nonStringPropFile));
        Assert.Equal(nonStringPropFile, ex.DatasetPath);
        Assert.Equal("inst-num", ex.InstanceId);
    }

    [Fact]
    public async Task LoadAsync_NormalizesUppercaseSweBenchKeys_AndTrimsIdentifierFields()
    {
        var path = Path.Combine(_tempDir, "swebench-upper.jsonl");
        await File.WriteAllTextAsync(path,
            """{"instance_id":"  repo__trim  ","category":"code_review","repo":"  owner/repo ","base_commit":" abc123 ","problem_statement":"Review","candidate_patch":"diff","expected_verdict":" MinorFix ","expected_difficulty":"  difficult ","FAIL_TO_PASS":["T1"],"PASS_TO_PASS":"[\"T2\"]"}""");

        var items = await _loader.LoadAsync(path);

        var item = Assert.Single(items);
        Assert.Equal("repo__trim", item.InstanceId);
        Assert.Equal("owner/repo", item.Repo);
        Assert.Equal("abc123", item.BaseCommit);
        Assert.Equal("MinorFix", item.ExpectedVerdict);
        Assert.Equal("difficult", item.ExpectedDifficulty);
        Assert.Equal(["T1"], item.FailToPass);
        Assert.Equal(["T2"], item.PassToPass);
    }

    [Fact]
    public async Task LoadAsync_WithExplicitOrDiscoveredManifest_OverlaysMetadataAndSiblingCraftedDatasetsAndEmitsDiagnostics()
    {
        var manifestDir = Path.Combine(_tempDir, "repo-datasets");
        Directory.CreateDirectory(manifestDir);

        var manifestPath = Path.Combine(manifestDir, "v1-smoke.manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "v1-smoke",
          "version": "1.0.1",
          "entries": [
            {
              "instance_id": "axios__axios-5892",
              "source": "swebench-multilingual",
              "category": "Implementation",
              "language": "javascript",
              "repo": "axios/axios",
              "test_result_format": "JUnitXml",
              "custom_test_command": "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter"
            },
            {
              "instance_id": "psf__requests-1921",
              "source": "swebench-verified",
              "category": "Implementation",
              "language": "python",
              "repo": "psf/requests",
              "custom_container_image": "python:3.9",
              "test_result_format": "JUnitXml",
              "custom_test_command": "pytest --junitxml=/workspace/.ncf-bench-results/junit.xml"
            },
            {
              "instance_id": "ncf-tr-ts-002",
              "source": "crafted-task-review",
              "category": "TaskReview",
              "language": "typescript",
              "repo": "vuejs/core",
              "test_result_format": "JUnitXml"
            }
          ]
        }
        """);

        await File.WriteAllTextAsync(Path.Combine(manifestDir, "v1-task-review.jsonl"),
            """{"instance_id":"ncf-tr-ts-002","category":"TaskReview","language":"typescript","repo":"vuejs/core","base_commit":"3653bc0f45d6fedf84e29b64ca52584359c383c0","problem_statement":"Updated problem","task_specification":"---\nid: 202\ntitle: \"Updated spec\"\nstatus: Open\ndifficulty: standard\n---\n\nUpdated body","expected_verdict":"Ready","expected_difficulty":"difficult","expected_findings":[{"file_path":"packages/reactivity/src/effect.ts","keywords":["endBatch","batchDepth"]}]}""");

        var localMaterializedDir = Path.Combine(_tempDir, "local-cache");
        Directory.CreateDirectory(localMaterializedDir);
        var materializedDataset = Path.Combine(localMaterializedDir, "v1-smoke.jsonl");
        await File.WriteAllTextAsync(materializedDataset, string.Join(Environment.NewLine,
        [
            """{"instance_id":"axios__axios-5892","category":"Implementation","language":"javascript","repo":"axios/axios","base_commit":"aaa","problem_statement":"Fix","test_result_format":"ExitCode","custom_test_command":"npm run test:mocha"}""",
            """{"instance_id":"psf__requests-1921","category":"Implementation","language":"python","repo":"psf/requests","base_commit":"bbb","problem_statement":"Fix","custom_container_image":"","test_result_format":"JUnitXml"}""",
            """{"instance_id":"ncf-tr-ts-002","category":"TaskReview","language":"typescript","repo":"vuejs/core","base_commit":"3653bc0f45d6fedf84e29b64ca52584359c383c0","problem_statement":"Stale problem","task_specification":"---\nid: 202\ntitle: \"Stale\"\nstatus: Open\ndifficulty: standard\n---\n\nStale body","expected_verdict":"Ready","expected_difficulty":"expert"}"""
        ]));

        var diagnostics = new List<string>();
        var items = await _loader.LoadAsync(materializedDataset, categoryOverride: null, manifestPath, diagnostics.Add);

        Assert.Equal(3, items.Count);
        var axios = Assert.Single(items, i => i.InstanceId == "axios__axios-5892");
        Assert.Equal(TestResultFormat.JUnitXml, axios.TestResultFormat);
        Assert.Contains("mocha-junit-reporter", axios.CustomTestCommand);

        var requests = Assert.Single(items, i => i.InstanceId == "psf__requests-1921");
        Assert.Equal("python:3.9", requests.CustomContainerImage);

        var tr = Assert.Single(items, i => i.InstanceId == "ncf-tr-ts-002");
        Assert.Equal("Updated problem", tr.ProblemStatement);
        Assert.Equal("difficult", tr.ExpectedDifficulty);
        Assert.Single(tr.ExpectedFindings);

        Assert.Contains(diagnostics, d => d.Contains("axios__axios-5892") && d.Contains("test_result_format"));
        Assert.Contains(diagnostics, d => d.Contains("psf__requests-1921") && d.Contains("custom_container_image"));
        Assert.Contains(diagnostics, d => d.Contains("ncf-tr-ts-002") && d.Contains("expected_difficulty"));
        Assert.Contains(diagnostics, d => d.Contains("overlaid 3/3"));

        // Adjacent auto-discovery without explicit --manifest
        File.Copy(manifestPath, Path.Combine(localMaterializedDir, "v1-smoke.manifest.json"));
        var autoItems = await _loader.LoadAsync(materializedDataset);
        Assert.Equal("python:3.9", Assert.Single(autoItems, i => i.InstanceId == "psf__requests-1921").CustomContainerImage);

        // Missing explicit manifest fails fast
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            _loader.LoadAsync(materializedDataset, null, Path.Combine(_tempDir, "missing.manifest.json")));
    }

    [Fact]
    public async Task LoadAsync_WithManifestAndSiblingCraftedDatasets_DoesNotEmitFalseDiffsWhenAlreadySynced_AndLogsNetFinalDiff()
    {
        var manifestDir = Path.Combine(_tempDir, "sync-repo-datasets");
        Directory.CreateDirectory(manifestDir);

        var manifestPath = Path.Combine(manifestDir, "v1-smoke.manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "v1-smoke",
          "version": "1.0.1",
          "entries": [
            {
              "instance_id": "ncf-cr-csharp-001",
              "source": "crafted-code-review",
              "category": "CodeReview",
              "language": "csharp",
              "repo": "serilog/serilog",
              "test_result_format": "Trx"
            },
            {
              "instance_id": "ncf-cr-py-001",
              "source": "crafted-code-review",
              "category": "CodeReview",
              "language": "python",
              "repo": "psf/requests",
              "custom_container_image": "python:3.9",
              "test_result_format": "JUnitXml"
            }
          ]
        }
        """);

        // Crafted code review omits test_result_format on 001 and sets python:3.10 on py-001 (manifest pins python:3.9)
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "v1-code-review.jsonl"), string.Join(Environment.NewLine,
        [
            """{"instance_id":"ncf-cr-csharp-001","category":"CodeReview","language":"csharp","repo":"serilog/serilog","base_commit":"aaa","problem_statement":"Review","candidate_patch":"diff","expected_verdict":"Approve"}""",
            """{"instance_id":"ncf-cr-py-001","category":"CodeReview","language":"python","repo":"psf/requests","base_commit":"bbb","problem_statement":"Review","custom_container_image":"python:3.10","candidate_patch":"diff","expected_verdict":"Approve"}"""
        ]));

        var localMaterializedDir = Path.Combine(_tempDir, "sync-local-cache");
        Directory.CreateDirectory(localMaterializedDir);
        var materializedDataset = Path.Combine(localMaterializedDir, "v1-smoke.jsonl");
        await File.WriteAllTextAsync(materializedDataset, string.Join(Environment.NewLine,
        [
            // Already matches final effective instance (crafted + manifest Trx) -> must NOT trigger overlaid diff log
            """{"instance_id":"ncf-cr-csharp-001","category":"CodeReview","language":"csharp","repo":"serilog/serilog","base_commit":"aaa","problem_statement":"Review","test_result_format":"Trx","candidate_patch":"diff","expected_verdict":"Approve"}""",
            // Stale empty custom_container_image -> final value from manifest is python:3.9 (not crafted python:3.10)
            """{"instance_id":"ncf-cr-py-001","category":"CodeReview","language":"python","repo":"psf/requests","base_commit":"bbb","problem_statement":"Review","test_result_format":"JUnitXml","candidate_patch":"diff","expected_verdict":"Approve"}"""
        ]));

        var diagnostics = new List<string>();
        var items = await _loader.LoadAsync(materializedDataset, categoryOverride: null, manifestPath, diagnostics.Add);

        Assert.Equal(2, items.Count);
        Assert.Equal("python:3.9", Assert.Single(items, i => i.InstanceId == "ncf-cr-py-001").CustomContainerImage);

        Assert.DoesNotContain(diagnostics, d => d.Contains("ncf-cr-csharp-001", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d =>
            d.Contains("ncf-cr-py-001", StringComparison.Ordinal) &&
            d.Contains("custom_container_image '<null>' -> 'python:3.9'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Contains("python:3.10", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("overlaid 1/2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_TaskReview_RejectsNumericExpectedDifficulty()
    {
        var path = Path.Combine(_tempDir, "numeric-difficulty.jsonl");
        await File.WriteAllTextAsync(path,
            """{"instance_id":"tr-num","category":"TaskReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Fix","task_specification":"---\nid: 1\nstatus: Open\n---\nBody","expected_verdict":"Ready","expected_difficulty":"2"}""");

        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => _loader.LoadAsync(path));
    }
}
