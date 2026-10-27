using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TestDiagnosticsParserTests
{
    [Fact]
    public void ParseTestFailuresFromOutput_StandardVsTest_ExtractsTestNameAndDiagnostics()
    {
        var output = """
  Failed NightCodeForge.Agents.Tests.SampleTest [15 ms]
  Error Message:
   Assert.Equal() Failure
   Expected: 1
   Actual:   2
  Stack Trace:
     at NightCodeForge.Agents.Tests.SampleTest() in C:\repo\SampleTest.cs:line 42
     at System.RuntimeMethodHandle.InvokeMethod()
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Failed NightCodeForge.Agents.Tests.SampleTest", failures[0]);
        Assert.Contains("Error Message:", failures[0]);
        Assert.Contains("Expected: 1", failures[0]);
        Assert.Contains("Actual:   2", failures[0]);
        Assert.Contains("Stack Trace:", failures[0]);
        Assert.Contains("at NightCodeForge.Agents.Tests.SampleTest() in C:\\repo\\SampleTest.cs:line 42", failures[0]);
        Assert.DoesNotContain("System.RuntimeMethodHandle", failures[0]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_ParameterizedTestWithBrackets_PreservesParameterList()
    {
        var output = """
  Failed NightCodeForge.Agents.Tests.ArrayTest(items: [1, 2], label: "[test]") [< 1 ms]
  Error Message:
   System.InvalidOperationException: Invalid state
  Stack Trace:
     at NightCodeForge.Agents.Tests.ArrayTest() in C:\repo\ArrayTest.cs:line 99
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Failed NightCodeForge.Agents.Tests.ArrayTest(items: [1, 2], label: \"[test]\")", failures[0]);
        Assert.DoesNotContain("[< 1 ms]", failures[0]);
    }

    [Theory]
    [InlineData("Failed to connect to Docker daemon.")]
    [InlineData("Failed restoring NuGet packages for solution.")]
    [InlineData("Failed building target project.")]
    [InlineData("Failed! - Failed: 1")]
    [InlineData("Failed: 0, Passed: 10, Total: 10")]
    public void ParseTestFailuresFromOutput_EnglishProseOrSummaryLines_DoesNotProduceFalsePositives(string proseLine)
    {
        var output = $"""
Build started...
{proseLine}
Total tests: 0
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_XUnitAndPytestPrefixes_ParsedCorrectly()
    {
        var output = """
[FAIL] MyNamespace.MyClass.XUnitTest
  Error Message:
   Assertion failed
FAIL: MyNamespace.MyClass.PytestLikeTest
  Error Message:
   ValueError: boom
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Equal(2, failures.Count);
        Assert.Contains("Failed MyNamespace.MyClass.XUnitTest", failures[0]);
        Assert.Contains("Failed MyNamespace.MyClass.PytestLikeTest", failures[1]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_NullOrEmptyOutput_ReturnsEmptyList()
    {
        Assert.Empty(TestDiagnosticsParser.ParseTestFailuresFromOutput(null!));
        Assert.Empty(TestDiagnosticsParser.ParseTestFailuresFromOutput(string.Empty));
        Assert.Empty(TestDiagnosticsParser.ParseTestFailuresFromOutput("   "));
    }

    [Fact]
    public void ParseTestFailuresFromOutput_LocalizedFrenchHeader_ParsedCorrectly()
    {
        var output = """
  Échec NightCodeForge.Agents.Tests.SampleFrenchTest [12 ms]
  Error Message:
   Assert.True() Failure
  Stack Trace:
     at NightCodeForge.Agents.Tests.SampleFrenchTest() in C:\repo\SampleFrenchTest.cs:line 15
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Failed NightCodeForge.Agents.Tests.SampleFrenchTest", failures[0]);
        Assert.Contains("Assert.True() Failure", failures[0]);
        Assert.Contains("at NightCodeForge.Agents.Tests.SampleFrenchTest()", failures[0]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_HyphenAndBacktickInName_ParsedCorrectly()
    {
        var output = """
  Failed MyProject.Tests.Cli-Runner`1.Test-Execution [5 ms]
  Error Message:
   Command failed
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Failed MyProject.Tests.Cli-Runner`1.Test-Execution", failures[0]);
        Assert.Contains("Command failed", failures[0]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_SummaryWithFailedColon_StopsErrorMessageWithoutPollution()
    {
        var output = """
  Failed NightCodeForge.SampleTest
  Error Message:
   Expected 1 but got 0
Failed: 1, Passed: 9, Total: 10
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Expected 1 but got 0", failures[0]);
        Assert.DoesNotContain("Failed: 1, Passed: 9, Total: 10", failures[0]);
    }

    [Fact]
    public void ParseFreshTrxFiles_StackTraceWithLeadingEmptyLines_ExtractsFirstNonEmptyFrame()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"trx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var trxContent = """
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" name="test" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Failed">
    <Counters total="1" executed="1" passed="0" failed="1" error="0" timeout="0" aborted="0" />
  </ResultSummary>
  <Results>
    <UnitTestResult outcome="Failed" testName="MyTestNamespace.MyClass.FailingTest">
      <Output>
        <ErrorInfo>
          <Message>Assertion failure</Message>
          <StackTrace>

   at MyTestNamespace.MyClass.FailingTest() in C:\Test.cs:line 42
   at System.RuntimeMethodHandle.InvokeMethod()
          </StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
</TestRun>
""";
            var filePath = Path.Combine(tempDir, "results.trx");
            File.WriteAllText(filePath, trxContent);

            var metrics = TestDiagnosticsParser.ParseFreshTrxFiles(tempDir, DateTime.UtcNow.AddMinutes(-1));

            Assert.NotNull(metrics);
            Assert.Equal(1, metrics.Total);
            Assert.Equal(1, metrics.Failed);
            Assert.Single(metrics.FailedTestNames);
            Assert.Contains("at MyTestNamespace.MyClass.FailingTest() in C:\\Test.cs:line 42", metrics.FailedTestNames[0]);
            Assert.DoesNotContain("InvokeMethod", metrics.FailedTestNames[0]);
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
    public void ParseTestFailuresFromOutput_ErrorMessageLineStartingWithFailedColon_DoesNotTruncateBlock()
    {
        var output = """
  Failed NightCodeForge.Agents.Tests.SampleTest [15 ms]
  Error Message:
   Failed: connection refused
   Passed: nothing to see here
   Expected: 1
  Stack Trace:
     at NightCodeForge.Agents.Tests.SampleTest() in C:\repo\SampleTest.cs:line 42
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Failed: connection refused", failures[0]);
        Assert.Contains("Expected: 1", failures[0]);
        Assert.Contains("Stack Trace:", failures[0]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_HeaderLookingLineInsideErrorMessage_DoesNotCreateBogusFailure()
    {
        var output = """
  Failed NightCodeForge.Agents.Tests.SampleTest [15 ms]
  Error Message:
   Failed Some.Other.Name(arg) was expected to throw
  Stack Trace:
     at NightCodeForge.Agents.Tests.SampleTest() in C:\repo\SampleTest.cs:line 42
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.Contains("Some.Other.Name(arg) was expected to throw", failures[0]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_ConsecutiveFailuresWithDurations_AreSplit()
    {
        var output = """
  Failed A.B.First [1 ms]
  Error Message:
   boom one
  Failed A.B.Second [2 ms]
  Error Message:
   boom two
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Equal(2, failures.Count);
        Assert.Contains("boom one", failures[0]);
        Assert.DoesNotContain("boom two", failures[0]);
        Assert.Contains("boom two", failures[1]);
        Assert.DoesNotContain("Total:", failures[1]);
    }

    [Fact]
    public void ParseTestFailuresFromOutput_SummaryCounterLine_TerminatesBlock()
    {
        var output = """
  Failed A.B.First [1 ms]
  Error Message:
   boom one
Failed:     1
Passed:     5
""";

        var failures = TestDiagnosticsParser.ParseTestFailuresFromOutput(output);

        Assert.Single(failures);
        Assert.DoesNotContain("Passed", failures[0]);
    }
}

