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
}
