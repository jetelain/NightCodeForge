using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class StructuredDiagnosticExtractorTests
{
    private readonly StructuredDiagnosticExtractor _extractor = new();

    [Fact]
    public void ExtractDiagnostics_ParsesRoslynCompilerErrors()
    {
        var rawBuildOutput = @"
MSBuild version 17.8.3+195e4a5a3 for .NET
  Determining projects to restore...
  All projects are up-to-date for restore.
C:\Repos\App\src\Services\OrderService.cs(42,15): error CS0103: The name 'CalculateTax' does not exist in the current context [C:\Repos\App\src\App.csproj]
C:\Repos\App\src\Services\OrderService.cs(55,20): error CS0246: The type or namespace name 'TaxResult' could not be found [C:\Repos\App\src\App.csproj]

Build FAILED.
";

        var summary = _extractor.ExtractDiagnostics(rawBuildOutput, new LocalOptimizationFlags());

        Assert.Equal(2, summary.CompilerErrors.Count);
        Assert.Equal("CS0103", summary.CompilerErrors[0].Code);
        Assert.Equal(42, summary.CompilerErrors[0].Line);
        Assert.Equal("The name 'CalculateTax' does not exist in the current context", summary.CompilerErrors[0].Message);

        Assert.Equal("CS0246", summary.CompilerErrors[1].Code);
        Assert.Equal(55, summary.CompilerErrors[1].Line);

        Assert.Contains("❌ Build Failed (2 Fatal Errors):", summary.FormattedMarkdown);
    }

    [Fact]
    public void ExtractDiagnostics_WhenOptedOut_ReturnsRawOutput()
    {
        var raw = "Raw MSBuild log...";
        var flags = new LocalOptimizationFlags { StructuredDiagnosticExtraction = false };
        var summary = _extractor.ExtractDiagnostics(raw, flags);

        Assert.Equal(raw, summary.FormattedMarkdown);
    }

    [Fact]
    public void CheckRegression_DetectsDroppingTestCounts()
    {
        var regressionNotice = _extractor.CheckRegression(
            baselineTestCount: 50,
            currentTestCount: 45,
            baselineCoverage: 85.0,
            currentCoverage: 82.5);

        Assert.Contains("Total test count dropped from 50 to 45 (-5)", regressionNotice);
        Assert.Contains("Code coverage dropped from 85.0% to 82.5% (-2.5%)", regressionNotice);
    }

    [Fact]
    public void ExtractDiagnostics_ParsesTypeScriptAndRustErrors()
    {
        var tsOutput = "src/index.ts:14:5 - error TS2322: Type 'string' is not assignable to type 'number'.";
        var tsSummary = _extractor.ExtractDiagnostics(tsOutput, new LocalOptimizationFlags());

        Assert.Single(tsSummary.CompilerErrors);
        Assert.Equal("TS2322", tsSummary.CompilerErrors[0].Code);
        Assert.Equal(14, tsSummary.CompilerErrors[0].Line);
        Assert.Equal("src/index.ts", tsSummary.CompilerErrors[0].File);

        var rustOutput = "error[E0308]: mismatched types\n  --> src/main.rs:22:9\n   | expected i32, found &str";
        var rustSummary = _extractor.ExtractDiagnostics(rustOutput, new LocalOptimizationFlags());

        Assert.Single(rustSummary.CompilerErrors);
        Assert.Equal("E0308", rustSummary.CompilerErrors[0].Code);
        Assert.Equal(22, rustSummary.CompilerErrors[0].Line);
        Assert.Equal("src/main.rs", rustSummary.CompilerErrors[0].File);
    }

    [Fact]
    public void ExtractDiagnostics_ParsesTestFailures_WhenQualified()
    {
        var output = @"
  Failed OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetNamedTarget [9 ms]
  Error Message:
   System.InvalidOperationException : Sequence contains more than one matching element
  Stack Trace:
     at OllamaRouter.Tests.Services.TerminalMonitorRendererTests.cs:line 529
";
        var summary = _extractor.ExtractDiagnostics(output, new LocalOptimizationFlags());

        Assert.Single(summary.TestFailures);
        Assert.Equal("OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetNamedTarget", summary.TestFailures[0].TestName);
        Assert.Contains("Sequence contains more than one matching element", summary.TestFailures[0].ErrorMessage);
        Assert.Contains("line 529", summary.TestFailures[0].StackTraceLine);
    }

    [Fact]
    public void ExtractDiagnostics_IgnoresFailedToFalsePositives()
    {
        var output = @"
Scenario failed: Failed to connect to server.
Failed to start process.
";
        var summary = _extractor.ExtractDiagnostics(output, new LocalOptimizationFlags());

        Assert.Empty(summary.TestFailures);
    }

    [Fact]
    public void ExtractDiagnostics_ParsesParameterizedTheoryTestFailures()
    {
        var output = @"
  Failed NightCodeForge.Agents.Tests.CompactionTests.MyTheory(foo: ""bar"", count: 42) [12 ms]
  Error Message:
   System.ArgumentException : Invalid parameter
  Stack Trace:
     at NightCodeForge.Agents.Tests.CompactionTests.cs:line 120
";
        var summary = _extractor.ExtractDiagnostics(output, new LocalOptimizationFlags());

        Assert.Single(summary.TestFailures);
        Assert.Equal(@"NightCodeForge.Agents.Tests.CompactionTests.MyTheory(foo: ""bar"", count: 42)", summary.TestFailures[0].TestName);
        Assert.Equal("System.ArgumentException : Invalid parameter", summary.TestFailures[0].ErrorMessage);
        Assert.Equal("at NightCodeForge.Agents.Tests.CompactionTests.cs:line 120", summary.TestFailures[0].StackTraceLine);
    }

    [Fact]
    public void ExtractDiagnostics_ParsesSubMillisecondDurationTestFailures()
    {
        var output = @"
  Failed NightCodeForge.Core.Tests.FastTest [< 1 ms]
  Error Message:
   Assert.True() Failure
  Stack Trace:
     at NightCodeForge.Core.Tests.FastTest.cs:line 45
";
        var summary = _extractor.ExtractDiagnostics(output, new LocalOptimizationFlags());

        Assert.Single(summary.TestFailures);
        Assert.Equal("NightCodeForge.Core.Tests.FastTest", summary.TestFailures[0].TestName);
        Assert.Equal("Assert.True() Failure", summary.TestFailures[0].ErrorMessage);
        Assert.Equal("at NightCodeForge.Core.Tests.FastTest.cs:line 45", summary.TestFailures[0].StackTraceLine);
    }
}
