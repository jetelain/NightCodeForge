using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class RoslynSymbolIndexTests
{
    [Fact]
    public async Task IndexWorkspaceAsync_DiscoversClassesInterfacesAndMethods()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var code = @"
namespace DemoApp;

public interface IOrderService
{
    void ProcessOrder(int id);
}

public class OrderService : IOrderService
{
    public void ProcessOrder(int id)
    {
    }
}
";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "OrderService.cs"), code);

            var indexer = new RoslynSymbolIndexProvider();
            var symbols = await indexer.IndexWorkspaceAsync(tempDir);

            Assert.NotEmpty(symbols);
            Assert.Contains(symbols, s => s.Kind == "Interface" && s.Name == "IOrderService");
            Assert.Contains(symbols, s => s.Kind == "Class" && s.Name == "OrderService");
            Assert.Contains(symbols, s => s.Kind == "Method" && s.Name == "OrderService.ProcessOrder");

            var classSymbol = symbols.First(s => s.Name == "OrderService" && s.Kind == "Class");
            Assert.True(classSymbol.StartLine > 0);
            Assert.True(classSymbol.EndLine >= classSymbol.StartLine);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task GetMatchingSymbolsPromptAsync_GeneratesXmlTagWithMatchingSymbols()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var code = @"
namespace DemoApp;

public class PaymentGateway
{
    public void Charge() {}
}
";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "PaymentGateway.cs"), code);

            var indexer = new RoslynSymbolIndexProvider();
            var prompt = await indexer.GetMatchingSymbolsPromptAsync(tempDir, "Please fix bug in PaymentGateway charge method", new LocalOptimizationFlags());

            Assert.Contains("<discovered_code_symbols>", prompt);
            Assert.Contains("Class PaymentGateway", prompt);
            Assert.Contains("PaymentGateway.cs", prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Step1164_GetMatchingSymbolsPromptAsync_RanksProductionSourcesAheadOfBenchSamplesAndTests_AndMatchesDotSegments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-autofac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create 18 matching benchmark symbols that previously crowded out all src/ symbols in filesystem order.
            var benchDir = Path.Combine(tempDir, "bench", "Autofac.Benchmarks");
            var samplesDir = Path.Combine(tempDir, "samples", "Autofac.Samples");
            var testDir = Path.Combine(tempDir, "test", "Autofac.Test");
            var srcDir = Path.Combine(tempDir, "src", "Autofac", "Core", "Resolving");
            Directory.CreateDirectory(benchDir);
            Directory.CreateDirectory(samplesDir);
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(srcDir);

            var benchSb = new System.Text.StringBuilder("namespace Autofac.Benchmarks;\n");
            for (int i = 1; i <= 18; i++)
            {
                benchSb.AppendLine($"public class ResolvePipelineBenchmark{i:D2} {{ public void Execute() {{}} }}");
            }
            await File.WriteAllTextAsync(Path.Combine(benchDir, "ResolveBenchmarks.cs"), benchSb.ToString());

            await File.WriteAllTextAsync(
                Path.Combine(samplesDir, "SampleService.cs"),
                "namespace Autofac.Samples;\npublic class ResolvePipelineSample { public void ResolveComponent() {} }\n");

            await File.WriteAllTextAsync(
                Path.Combine(testDir, "ResolveTests.cs"),
                "namespace Autofac.Test;\npublic class ResolvePipelineTests { public void ResolveComponentTest() {} }\n");

            await File.WriteAllTextAsync(
                Path.Combine(srcDir, "ResolveRequest.cs"),
                """
                namespace Autofac.Core.Resolving;

                public class ResolvePipeline
                {
                    public void ResolveComponent() {}
                }
                """);

            var indexer = new RoslynSymbolIndexProvider();
            // Prompt includes common stop-words ("issue", "class", "method", "should", "public") plus real identifiers.
            var prompt = await indexer.GetMatchingSymbolsPromptAsync(
                tempDir,
                "Issue in class ResolvePipeline method ResolveComponent should resolve component cleanly.",
                new LocalOptimizationFlags());

            Assert.Contains("<discovered_code_symbols>", prompt);
            Assert.Contains("src/Autofac/Core/Resolving/ResolveRequest.cs", prompt);
            Assert.Contains("Class ResolvePipeline: src/Autofac/Core/Resolving/ResolveRequest.cs", prompt);
            Assert.Contains("Method ResolvePipeline.ResolveComponent: src/Autofac/Core/Resolving/ResolveRequest.cs", prompt);

            // Production src/ symbols must appear before test/ and bench/ symbols.
            int srcIdx = prompt.IndexOf("src/Autofac/Core/Resolving/ResolveRequest.cs", StringComparison.Ordinal);
            int testIdx = prompt.IndexOf("test/Autofac.Test/ResolveTests.cs", StringComparison.Ordinal);
            Assert.True(srcIdx >= 0);
            if (testIdx >= 0)
            {
                Assert.True(srcIdx < testIdx, "Expected production src/ symbol to rank ahead of test/ symbol.");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Step1164_ScoreSymbolRelevance_RequiresWholeQualifiedBoundaries_AndSkipsStopWordReverseSubstrings()
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FooBar",
            "RunAsync",
            "ValueTask",
            "TypeConverter"
        };

        var barRun = new CodeSymbol("Method", "Bar.Run", "src/Bar.cs", 1, 5);
        Assert.Equal(0, RoslynSymbolIndexProvider.ScoreSymbolRelevance(barRun, keywords, "Calls FooBar.RunAsync()"));
        Assert.True(RoslynSymbolIndexProvider.ScoreSymbolRelevance(barRun, keywords, "Calls Bar.Run()") >= 120);

        var stopWordMember = new CodeSymbol("Method", "NoiseClass.Value", "src/NoiseClass.cs", 1, 5);
        var stopWordTypeMember = new CodeSymbol("Method", "NoiseClass.Type", "src/NoiseClass.cs", 6, 10);
        Assert.Equal(0, RoslynSymbolIndexProvider.ScoreSymbolRelevance(stopWordMember, keywords, "Investigate ValueTask and TypeConverter"));
        Assert.Equal(0, RoslynSymbolIndexProvider.ScoreSymbolRelevance(stopWordTypeMember, keywords, "Investigate ValueTask and TypeConverter"));
    }

    [Fact]
    public async Task Step1166_BalancedCompositeRanking_And_SourceRootSandboxClassification()
    {
        // Source-rooted feature folder named "Sandbox" stays Production, while standalone "sandbox/" is Auxiliary
        Assert.Equal((RoslynSymbolIndexProvider.SymbolPathCategory.Production, true),
            RoslynSymbolIndexProvider.ClassifyPathPriority("src/MyApp/Sandbox/SandboxPolicy.cs"));
        Assert.Equal((RoslynSymbolIndexProvider.SymbolPathCategory.Auxiliary, false),
            RoslynSymbolIndexProvider.ClassifyPathPriority("sandbox/Demo/WidgetRunner.cs"));

        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-1166-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var srcDir = Path.Combine(tempDir, "src", "Unrelated");
            var sandboxDir = Path.Combine(tempDir, "sandbox", "Target");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(sandboxDir);

            // Create 18 weak substring hits in src/ matching "Widget" in "TargetWidgetExecutor"
            var srcSb = new System.Text.StringBuilder("namespace DemoApp.Unrelated;\n");
            for (int i = 1; i <= 18; i++)
            {
                srcSb.AppendLine($"public class LegacyWidgetAdapter{i:D2} {{ public void Run() {{}} }}");
            }
            await File.WriteAllTextAsync(Path.Combine(srcDir, "LegacyWidgets.cs"), srcSb.ToString());

            await File.WriteAllTextAsync(
                Path.Combine(sandboxDir, "TargetWidgetExecutor.cs"),
                """
                namespace DemoApp.Sandbox;

                public class TargetWidgetExecutor
                {
                    public void ExecuteSpecial() {}
                }
                """);

            var indexer = new RoslynSymbolIndexProvider();
            var prompt = await indexer.GetMatchingSymbolsPromptAsync(
                tempDir,
                "Fix TargetWidgetExecutor.ExecuteSpecial bug",
                new LocalOptimizationFlags());

            Assert.Contains("TargetWidgetExecutor.ExecuteSpecial", prompt);
            Assert.Contains("sandbox/Target/TargetWidgetExecutor.cs", prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
