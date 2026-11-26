using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Commands;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Tests.Datasets;

public sealed class BenchmarkDatasetImporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BenchmarkDatasetLoader _loader = new();

    public BenchmarkDatasetImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-imp-{Guid.NewGuid():N}");
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
    public async Task SweSharpBenchImporter_NormalizesCSharpExportAndRoundTripsJsonl()
    {
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "csharp-test",
          "version": "1.0.0",
          "entries": [
            {
              "instance_id": "Humanizr__Humanizer-1234",
              "source": "swe-sharp-bench",
              "category": "Implementation",
              "language": "csharp",
              "custom_container_image": "mcr.microsoft.com/dotnet/sdk:9.0",
              "custom_test_command": "dotnet test --logger trx"
            }
          ]
        }
        """);

        var inputPath = Path.Combine(_tempDir, "swe_sharp.jsonl");
        await File.WriteAllTextAsync(inputPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"Other__Repo-1","repo":"other/repo","base_commit":"0000","problem_statement":"Skip me"}""",
            """{"instance_id":"Humanizr__Humanizer-1234","repo":"Humanizr/Humanizer","base_commit":"1111aaaa","problem_statement":"Fix ordinalize negative","hints_text":"Check Ordinalizer","patch":"diff --git a/A.cs b/A.cs\n","test_patch":"diff --git a/T.cs b/T.cs\n","FAIL_TO_PASS":"[\"OrdinalizeNegative\"]","PASS_TO_PASS":"[\"OrdinalizePositive\"]"}"""
        ]));

        var manifest = await BenchmarkDatasetManifestLoader.LoadAsync(manifestPath);
        var importer = new SweSharpBenchImporter();

        var imported = await importer.ImportAsync(inputPath, manifest);

        var single = Assert.Single(imported);
        Assert.Equal("Humanizr__Humanizer-1234", single.InstanceId);
        Assert.Equal(BenchmarkProblemCategory.Implementation, single.Category);
        Assert.Equal(BenchmarkLanguages.CSharp, single.Language);
        Assert.Equal("Humanizr/Humanizer", single.Repo);
        Assert.Equal("1111aaaa", single.BaseCommit);
        Assert.Equal("Fix ordinalize negative", single.ProblemStatement);
        Assert.Equal("Check Ordinalizer", single.HintsText);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:9.0", single.CustomContainerImage);
        Assert.Equal("dotnet test --logger trx", single.CustomTestCommand);
        Assert.Equal(TestResultFormat.Trx, single.TestResultFormat);
        Assert.Equal(["OrdinalizeNegative"], single.FailToPass);
        Assert.Equal(["OrdinalizePositive"], single.PassToPass);

        var outPath = Path.Combine(_tempDir, "out", "csharp.jsonl");
        await BenchmarkDatasetJsonlWriter.WriteAsync(outPath, imported);

        var reloaded = await _loader.LoadAsync(outPath);
        var reloadedSingle = Assert.Single(reloaded);
        Assert.Equal(single.InstanceId, reloadedSingle.InstanceId);
        Assert.Equal(single.Category, reloadedSingle.Category);
        Assert.Equal(single.Language, reloadedSingle.Language);
        Assert.Equal(single.BaseCommit, reloadedSingle.BaseCommit);
        Assert.Equal(single.GoldPatch, reloadedSingle.GoldPatch);
        Assert.Equal(single.TestPatch, reloadedSingle.TestPatch);
        Assert.Equal(single.FailToPass, reloadedSingle.FailToPass);
        Assert.Equal(single.PassToPass, reloadedSingle.PassToPass);
        Assert.Equal(TestResultFormat.Trx, reloadedSingle.TestResultFormat);
    }

    [Fact]
    public async Task SweBenchMultilingualImporter_NormalizesTsJsAndRustWithManifestOverrides()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "multi",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "colinhacks__zod-3142",
                    Source = BenchmarkDatasetConstants.SourceSweBenchMultilingual,
                    Language = BenchmarkLanguages.TypeScript,
                    CustomTestCommand = "npx vitest run --reporter=junit",
                    TestResultFormat = TestResultFormat.JUnitXml
                },
                new BenchmarkManifestEntry
                {
                    InstanceId = "markedjs__marked-3105",
                    Source = BenchmarkDatasetConstants.SourceSweBenchMultilingual,
                    Language = BenchmarkLanguages.JavaScript,
                    CustomTestCommand = "npm test",
                    TestResultFormat = TestResultFormat.ExitCode
                },
                new BenchmarkManifestEntry
                {
                    InstanceId = "BurntSushi__ripgrep-2576",
                    Source = BenchmarkDatasetConstants.SourceSweBenchMultilingual,
                    Language = BenchmarkLanguages.Rust,
                    CustomTestCommand = "cargo test --no-fail-fast",
                    TestResultFormat = TestResultFormat.CargoTest
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "multilingual.json");
        await File.WriteAllTextAsync(inputPath, """
        [
          {
            "instance_id": "colinhacks__zod-3142",
            "repo": "colinhacks/zod",
            "base_commit": "aaaa1111",
            "problem_statement": "Reject NaN in finite()",
            "patch": "diff ts",
            "test_patch": "test ts",
            "FAIL_TO_PASS": ["finite rejects NaN"],
            "PASS_TO_PASS": ["finite accepts 42"]
          },
          {
            "instance_id": "markedjs__marked-3105",
            "repo": "markedjs/marked",
            "base_commit": "bbbb2222",
            "problem_statement": "Sanitize href",
            "patch": "diff js",
            "fail_to_pass": ["sanitizes link"]
          },
          {
            "instance_id": "BurntSushi__ripgrep-2576",
            "repo": "BurntSushi/ripgrep",
            "base_commit": "cccc3333",
            "problem_statement": "Ignore empty globs",
            "patch": "diff rs",
            "fail_to_pass": ["tests::empty_glob"]
          }
        ]
        """);

        var importer = new SweBenchMultilingualImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        Assert.Equal(3, imported.Count);
        Assert.Equal(BenchmarkLanguages.TypeScript, imported[0].Language);
        Assert.Equal(TestResultFormat.JUnitXml, imported[0].TestResultFormat);
        Assert.Equal("npx vitest run --reporter=junit", imported[0].CustomTestCommand);

        Assert.Equal(BenchmarkLanguages.JavaScript, imported[1].Language);
        Assert.Equal(TestResultFormat.ExitCode, imported[1].TestResultFormat);
        Assert.Equal("npm test", imported[1].CustomTestCommand);

        Assert.Equal(BenchmarkLanguages.Rust, imported[2].Language);
        Assert.Equal(TestResultFormat.CargoTest, imported[2].TestResultFormat);
        Assert.Equal("cargo test --no-fail-fast", imported[2].CustomTestCommand);
    }

    [Fact]
    public async Task MultiSweBenchImporter_NormalizesNestedMultiSweBenchSchema()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "multiswe",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "BurntSushi__ripgrep-2576",
                    Language = BenchmarkLanguages.Rust
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "multiswe.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"BurntSushi__ripgrep-2576","org":"BurntSushi","repo":"ripgrep","base":{"sha":"deadbeef1234"},"resolved_issues":[{"title":"Empty glob crash","body":"Trim empty override globs."}],"fix_patch":"diff --git a/src/lib.rs b/src/lib.rs\n","test_patch":"diff test","f2p_tests":{"overrides::empty_glob":{"status":"FAIL"}},"p2p_tests":{"overrides::basic":{"status":"PASS"}}}
        """);

        var importer = new MultiSweBenchImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        var single = Assert.Single(imported);
        Assert.Equal("BurntSushi/ripgrep", single.Repo);
        Assert.Equal("deadbeef1234", single.BaseCommit);
        Assert.Equal("Empty glob crash\n\nTrim empty override globs.", single.ProblemStatement);
        Assert.Equal("diff --git a/src/lib.rs b/src/lib.rs\n", single.GoldPatch);
        Assert.Equal(["overrides::empty_glob"], single.FailToPass);
        Assert.Equal(["overrides::basic"], single.PassToPass);
        Assert.Equal(TestResultFormat.CargoTest, single.TestResultFormat);
    }

    [Fact]
    public async Task SweBenchVerifiedImporter_AppliesDefaultPythonJUnitMetadata()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "py-suite",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "psf__requests-2674",
                    Source = BenchmarkDatasetConstants.SourceSweBenchVerified,
                    Language = BenchmarkLanguages.Python
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "py.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"psf__requests-2674","repo":"psf/requests","base_commit":"abc123def","problem_statement":"Wrap urllib3 exceptions","patch":"diff py","test_patch":"test py","FAIL_TO_PASS":["tests/test_requests.py::test_timeout"],"PASS_TO_PASS":["tests/test_requests.py::test_get"]}
        """);

        var importer = new SweBenchVerifiedImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        var single = Assert.Single(imported);
        Assert.Equal(BenchmarkLanguages.Python, single.Language);
        Assert.Equal(TestResultFormat.JUnitXml, single.TestResultFormat);
        Assert.Equal(BenchmarkDatasetConstants.DefaultPythonJUnitTestCommand, single.CustomTestCommand);
    }

    [Fact]
    public async Task Importers_FailFastOnMissingInstanceIdsOrEmptyBaseCommit()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "fail-fast",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "missing__id-999",
                    Language = BenchmarkLanguages.Python
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "input.jsonl");
        await File.WriteAllTextAsync(inputPath, """{"instance_id":"other__id-1","repo":"o/r","base_commit":"abc","problem_statement":"stmt"}""");

        var importer = new SweBenchVerifiedImporter();
        var missingEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => importer.ImportAsync(inputPath, manifest));
        Assert.Contains("missing__id-999", missingEx.Message);

        var emptyCommitManifest = new BenchmarkDatasetManifest
        {
            SuiteName = "empty-commit",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "bad__commit-1",
                    Language = BenchmarkLanguages.Python
                }
            ]
        };
        await File.WriteAllTextAsync(inputPath, """{"instance_id":"bad__commit-1","repo":"o/r","base_commit":"   ","problem_statement":"stmt"}""");

        var commitEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => importer.ImportAsync(inputPath, emptyCommitManifest));
        Assert.Contains("base_commit", commitEx.Message);
    }

    [Fact]
    public async Task ManifestLoader_FailFastOnUnsupportedLanguageOrDuplicateInstanceId()
    {
        var unsupportedLangPath = Path.Combine(_tempDir, "bad-lang.manifest.json");
        await File.WriteAllTextAsync(unsupportedLangPath, """
        {
          "suite_name": "bad-lang",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "x-1", "language": "cobol" }
          ]
        }
        """);

        var langEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => BenchmarkDatasetManifestLoader.LoadAsync(unsupportedLangPath));
        Assert.Contains("Unsupported language", langEx.Message);

        var dupIdPath = Path.Combine(_tempDir, "dup-id.manifest.json");
        await File.WriteAllTextAsync(dupIdPath, """
        {
          "suite_name": "dup",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "x-1", "language": "csharp" },
            { "instance_id": "X-1", "language": "csharp" }
          ]
        }
        """);

        var dupEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => BenchmarkDatasetManifestLoader.LoadAsync(dupIdPath));
        Assert.Contains("Duplicate instance_id", dupEx.Message);
    }

    [Fact]
    public void ImporterRegistry_ResolvesRegisteredSourcesAndThrowsForUnknown()
    {
        var registry = new BenchmarkDatasetImporterRegistry(
        [
            new SweSharpBenchImporter(),
            new SweBenchMultilingualImporter(),
            new MultiSweBenchImporter(),
            new SweBenchVerifiedImporter()
        ]);

        Assert.IsType<SweSharpBenchImporter>(registry.GetImporter(BenchmarkDatasetSource.SweSharpBench));
        Assert.IsType<SweBenchMultilingualImporter>(registry.GetImporter(BenchmarkDatasetSource.SweBenchMultilingual));
        Assert.IsType<MultiSweBenchImporter>(registry.GetImporter(BenchmarkDatasetSource.MultiSweBench));
        Assert.IsType<SweBenchVerifiedImporter>(registry.GetImporter(BenchmarkDatasetSource.SweBenchVerified));

        Assert.Throws<BenchmarkDatasetException>(() => registry.GetImporter((BenchmarkDatasetSource)999));
    }

    [Fact]
    public async Task DatasetImportCommand_ExecuteAsync_ImportsManifestSubsetAndLogsSummary()
    {
        var manifestPath = Path.Combine(_tempDir, "cmd-manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "cmd-import",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "Humanizr__Humanizer-1234", "source": "swe-sharp-bench", "language": "csharp" }
          ]
        }
        """);

        var inputPath = Path.Combine(_tempDir, "cmd-raw.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"Humanizr__Humanizer-1234","repo":"Humanizr/Humanizer","base_commit":"abc123","problem_statement":"Fix bug","patch":"diff"}
        """);

        var outPath = Path.Combine(_tempDir, "cmd-out.jsonl");
        var registry = new BenchmarkDatasetImporterRegistry([new SweSharpBenchImporter()]);
        var command = new DatasetImportCommand(registry);
        var lines = new List<string>();

        var exitCode = await command.ExecuteAsync(
            new DatasetImportCommandOptions
            {
                Source = BenchmarkDatasetSource.SweSharpBench,
                InputPath = inputPath,
                ManifestPath = manifestPath,
                OutputPath = outPath
            },
            lines.Add);

        Assert.Equal(BenchmarkCliConstants.ExitSuccess, exitCode);
        Assert.True(File.Exists(outPath));
        Assert.Contains(lines, l => l.Contains("Imported 1 instance(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestLoader_NormalizesSourceVariants_AndFailsFastOnInvalidSourceCompatibilityOrMixedSources()
    {
        var normalizedPath = Path.Combine(_tempDir, "norm.manifest.json");
        await File.WriteAllTextAsync(normalizedPath, """
        {
          "suite_name": "norm",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "impl-1", "source": "swe_sharp_bench", "category": "Implementation", "language": "csharp" },
            { "instance_id": "cr-1", "source": "crafted_code_review", "category": "CodeReview", "language": "csharp" }
          ]
        }
        """);

        var loaded = await BenchmarkDatasetManifestLoader.LoadAsync(normalizedPath);
        Assert.Equal(BenchmarkDatasetConstants.SourceSweSharpBench, loaded.Entries[0].Source);
        Assert.Equal(BenchmarkDatasetConstants.SourceCraftedCodeReview, loaded.Entries[1].Source);

        var badSourcePath = Path.Combine(_tempDir, "bad-source.manifest.json");
        await File.WriteAllTextAsync(badSourcePath, """
        {
          "suite_name": "bad-src",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "impl-1", "source": "swe-sharp-bnech", "language": "csharp" }
          ]
        }
        """);
        var badEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => BenchmarkDatasetManifestLoader.LoadAsync(badSourcePath));
        Assert.Contains("Unsupported manifest source", badEx.Message);

        var incompatiblePath = Path.Combine(_tempDir, "incompatible.manifest.json");
        await File.WriteAllTextAsync(incompatiblePath, """
        {
          "suite_name": "incompat",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "impl-1", "source": "crafted-code-review", "category": "Implementation", "language": "csharp" }
          ]
        }
        """);
        var incompatEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => BenchmarkDatasetManifestLoader.LoadAsync(incompatiblePath));
        Assert.Contains("incompatible source", incompatEx.Message);

        var mixedImplPath = Path.Combine(_tempDir, "mixed-impl.manifest.json");
        await File.WriteAllTextAsync(mixedImplPath, """
        {
          "suite_name": "mixed",
          "version": "1.0.0",
          "entries": [
            { "instance_id": "impl-1", "source": "swe-sharp-bench", "category": "Implementation", "language": "csharp" },
            { "instance_id": "impl-2", "category": "Implementation", "language": "csharp" }
          ]
        }
        """);
        var mixedEx = await Assert.ThrowsAsync<BenchmarkDatasetException>(() => BenchmarkDatasetManifestLoader.LoadAsync(mixedImplPath));
        Assert.Contains("mixes explicit and omitted", mixedEx.Message);
    }

    [Fact]
    public async Task ExternalImporter_SelectsImplementationEntriesWhenReviewEntriesHaveExplicitSources_AndValidatesManifest()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "mixed-categories",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "ncf-cr-csharp-001",
                    Source = BenchmarkDatasetConstants.SourceCraftedCodeReview,
                    Category = BenchmarkProblemCategory.CodeReview,
                    Language = BenchmarkLanguages.CSharp
                },
                new BenchmarkManifestEntry
                {
                    InstanceId = "serilog__serilog-2083",
                    Category = BenchmarkProblemCategory.Implementation,
                    Language = BenchmarkLanguages.CSharp
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "sharp.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"serilog__serilog-2083","repo":"serilog/serilog","base_commit":"fdf4a4872b4effd02dcd7d788e45b6b8ffdfa2fc","problem_statement":"Fix logger issue","patch":"","gold_patch":" context\n-old\n+new\n","test_patch":" test_ctx\n+t\n","fail_to_pass":[],"f2p_tests":{"MyTest":{"status":"FAIL"}}}
        """);

        var importer = new SweSharpBenchImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        var item = Assert.Single(imported);
        Assert.Equal("serilog__serilog-2083", item.InstanceId);
        Assert.Equal(" context\n-old\n+new\n", item.GoldPatch);
        Assert.Equal(" test_ctx\n+t\n", item.TestPatch);
        Assert.Equal(["MyTest"], item.FailToPass);

        var dupManifest = manifest with
        {
            Entries =
            [
                new BenchmarkManifestEntry { InstanceId = "dup-1", Language = BenchmarkLanguages.CSharp },
                new BenchmarkManifestEntry { InstanceId = "DUP-1", Language = BenchmarkLanguages.CSharp }
            ]
        };
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => importer.ImportAsync(inputPath, dupManifest));

        var badFmtManifest = manifest with
        {
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "serilog__serilog-2083",
                    Language = BenchmarkLanguages.CSharp,
                    TestResultFormat = (TestResultFormat)999
                }
            ]
        };
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() => importer.ImportAsync(inputPath, badFmtManifest));
    }

    [Fact]
    public async Task MultiSweBenchImporter_HandlesEmptyTitlesMultipleIssuesSlashesAndSynthesizedIds()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "multiswe-edge",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry { InstanceId = "orgA__repoA-42", Language = BenchmarkLanguages.Rust },
                new BenchmarkManifestEntry { InstanceId = "orgB__repoB-99", Language = BenchmarkLanguages.TypeScript }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "multiswe-edge.jsonl");
        await File.WriteAllTextAsync(inputPath, string.Join(Environment.NewLine,
        [
            """{"org":"/orgA/","repo":"/repoA/","number":42,"base":{"sha":"aaaa1111"},"resolved_issues":[{"title":"","body":"First body"},{"title":"Second title","body":"Second body"}],"patch":"","fix_patch":" diff_ctx\n+fix\n"}""",
            """{"org":"orgB","repo":"repoB","number":"99","base":{"sha":"bbbb2222"},"resolved_issues":[{"title":"Only title","body":""}],"fix_patch":" diff_ts\n+fix\n"}"""
        ]));

        var importer = new MultiSweBenchImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        Assert.Equal(2, imported.Count);
        Assert.Equal("orgA/repoA", imported[0].Repo);
        Assert.Equal("First body\n\n---\n\nSecond title\n\nSecond body", imported[0].ProblemStatement);
        Assert.Equal(" diff_ctx\n+fix\n", imported[0].GoldPatch);

        Assert.Equal("orgB/repoB", imported[1].Repo);
        Assert.Equal("Only title", imported[1].ProblemStatement);
    }

    [Fact]
    public async Task BenchmarkDatasetJsonlWriter_ValidatesEntriesAndWritesAtomically()
    {
        var outPath = Path.Combine(_tempDir, "atomic.jsonl");
        await File.WriteAllTextAsync(outPath, "ORIGINAL_CONTENT");

        var valid = new BenchmarkInstance
        {
            InstanceId = "id-1",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "o/r",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };

        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            BenchmarkDatasetJsonlWriter.WriteAsync(outPath, [valid, null!]));
        Assert.Equal("ORIGINAL_CONTENT", await File.ReadAllTextAsync(outPath));

        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            BenchmarkDatasetJsonlWriter.WriteAsync(outPath, [valid, valid with { InstanceId = "ID-1" }]));
        Assert.Equal("ORIGINAL_CONTENT", await File.ReadAllTextAsync(outPath));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BenchmarkDatasetJsonlWriter.WriteAsync(outPath, [valid], cts.Token));
        Assert.Equal("ORIGINAL_CONTENT", await File.ReadAllTextAsync(outPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

        await BenchmarkDatasetJsonlWriter.WriteAsync(outPath, [valid]);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        var reloaded = await _loader.LoadAsync(outPath);
        Assert.Single(reloaded);
    }

    [Fact]
    public async Task SweSharpBenchImporter_ParsesPythonSingleQuotedTestListsAndRejectsMalformed()
    {
        var manifest = new BenchmarkDatasetManifest
        {
            SuiteName = "sharp-pylist",
            Version = "1.0.0",
            Entries =
            [
                new BenchmarkManifestEntry
                {
                    InstanceId = "serilog__serilog-2083",
                    Category = BenchmarkProblemCategory.Implementation,
                    Language = BenchmarkLanguages.CSharp
                }
            ]
        };

        var inputPath = Path.Combine(_tempDir, "sharp-pylist.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"serilog__serilog-2083","repo":"serilog/serilog","base_commit":"fdf4a4872b4effd02dcd7d788e45b6b8ffdfa2fc","problem_statement":"Fix logger","patch":"diff","FAIL_TO_PASS":"['Serilog.Tests.A(\"x\")', 'Serilog.Tests.B(\\'y\\')', \"Test('z')\",]","PASS_TO_PASS":"[]"}
        """);

        var importer = new SweSharpBenchImporter();
        var imported = await importer.ImportAsync(inputPath, manifest);

        var item = Assert.Single(imported);
        Assert.Equal(
            ["""Serilog.Tests.A("x")""", """Serilog.Tests.B('y')""", """Test('z')"""],
            item.FailToPass);
        Assert.Empty(item.PassToPass);

        foreach (var badList in new[] { "['unterminated", "[123]", "['a' 'b']", "['bad\\u12']" })
        {
            var badPath = Path.Combine(_tempDir, $"bad-{Guid.NewGuid():N}.jsonl");
            var escapedBad = System.Text.Json.JsonSerializer.Serialize(badList);
            await File.WriteAllTextAsync(badPath,
                "{\"instance_id\":\"serilog__serilog-2083\",\"repo\":\"serilog/serilog\",\"base_commit\":\"fdf\",\"problem_statement\":\"Fix\",\"patch\":\"diff\",\"FAIL_TO_PASS\":" + escapedBad + "}");

            await Assert.ThrowsAsync<BenchmarkDatasetException>(() => importer.ImportAsync(badPath, manifest));
        }
    }

    [Fact]
    public void ImporterRegistry_RejectsNullElementsAndDuplicateSources()
    {
        Assert.Throws<ArgumentException>(() =>
            new BenchmarkDatasetImporterRegistry([new SweSharpBenchImporter(), null!]));

        Assert.Throws<BenchmarkDatasetException>(() =>
            new BenchmarkDatasetImporterRegistry([new SweSharpBenchImporter(), new SweSharpBenchImporter()]));
    }

    [Fact]
    public async Task SweSharpBenchImporter_AppliesManifestFailToPassAndPassToPassOverrides()
    {
        var manifestPath = Path.Combine(_tempDir, "override-manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "override-test",
          "version": "1.0.0",
          "entries": [
            {
              "instance_id": "serilog__serilog-1903",
              "source": "swe-sharp-bench",
              "category": "Implementation",
              "language": "csharp",
              "fail_to_pass": [ "SelfLogTests.TestA", "SelfLogTests.TestB" ],
              "pass_to_pass": [ "GettablePropertyFinderTests.RenamedTest" ]
            }
          ]
        }
        """);

        var inputPath = Path.Combine(_tempDir, "raw-override.jsonl");
        await File.WriteAllTextAsync(inputPath, """
        {"instance_id":"serilog__serilog-1903","repo":"serilog/serilog","base_commit":"33de1c86","problem_statement":"Fix","patch":"diff","fail_to_pass":["SelfLogTests.TestA","GettablePropertyFinderTests.RenamedTest","SelfLogTests.TestB"],"pass_to_pass":[]}
        """);

        var manifest = await BenchmarkDatasetManifestLoader.LoadAsync(manifestPath);
        var importer = new SweSharpBenchImporter();

        var imported = await importer.ImportAsync(inputPath, manifest);

        var single = Assert.Single(imported);
        Assert.Equal(["SelfLogTests.TestA", "SelfLogTests.TestB"], single.FailToPass);
        Assert.Equal(["GettablePropertyFinderTests.RenamedTest"], single.PassToPass);
    }

    [Fact]
    public async Task SweBenchVerifiedImporter_AppliesManifestPipConstraintsOverrideAndPreservesRawWhenOmitted()
    {
        var manifestPath = Path.Combine(_tempDir, "pip-constraints-manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
        {
          "suite_name": "pip-test",
          "version": "1.0.0",
          "entries": [
            {
              "instance_id": "pallets__flask-5014",
              "source": "swebench-verified",
              "category": "Implementation",
              "language": "python",
              "pip_constraints": [ "Werkzeug<3.0" ]
            },
            {
              "instance_id": "psf__requests-5414",
              "source": "swebench-verified",
              "category": "Implementation",
              "language": "python"
            }
          ]
        }
        """);

        var inputPath = Path.Combine(_tempDir, "raw-pip.jsonl");
        await File.WriteAllLinesAsync(inputPath,
        [
            """{"instance_id":"pallets__flask-5014","repo":"pallets/flask","base_commit":"abc123","problem_statement":"Fix","patch":"diff","pip_constraints":["Werkzeug<2.0"]}""",
            """{"instance_id":"psf__requests-5414","repo":"psf/requests","base_commit":"def456","problem_statement":"Fix","patch":"diff","pip_constraints":["MarkupSafe<2.1","Werkzeug<2.1"]}"""
        ]);

        var manifest = await BenchmarkDatasetManifestLoader.LoadAsync(manifestPath);
        var importer = new SweBenchVerifiedImporter();

        var imported = await importer.ImportAsync(inputPath, manifest);

        Assert.Equal(2, imported.Count);
        var flask = Assert.Single(imported, i => i.InstanceId == "pallets__flask-5014");
        Assert.Equal(["Werkzeug<3.0"], flask.PipConstraints);

        var requests = Assert.Single(imported, i => i.InstanceId == "psf__requests-5414");
        Assert.Equal(["MarkupSafe<2.1", "Werkzeug<2.1"], requests.PipConstraints);
    }
}
