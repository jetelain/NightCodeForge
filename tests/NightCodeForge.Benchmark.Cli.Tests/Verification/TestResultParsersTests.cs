using NightCodeForge.Benchmark.Cli.Verification;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

public sealed class TestResultParsersTests
{
    [Fact]
    public void TrxTestResultParser_ParsesUnitTestResultsAndMatchesAssertions()
    {
        const string trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="11111111-1111-1111-1111-111111111111" testName="Add_ReturnsSum" outcome="Passed" />
                <UnitTestResult testId="22222222-2222-2222-2222-222222222222" testName="Subtract_ReturnsDiff" outcome="Failed" />
                <UnitTestResult testId="33333333-3333-3333-3333-333333333333" testName="ExistingTest" outcome="Passed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="11111111-1111-1111-1111-111111111111">
                  <TestMethod className="MyLib.Tests.CalculatorTests" name="Add_ReturnsSum" />
                </UnitTest>
                <UnitTest id="22222222-2222-2222-2222-222222222222">
                  <TestMethod className="MyLib.Tests.CalculatorTests" name="Subtract_ReturnsDiff" />
                </UnitTest>
                <UnitTest id="33333333-3333-3333-3333-333333333333">
                  <TestMethod className="MyLib.Tests.CalculatorTests" name="ExistingTest" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var outcomes = TrxTestResultParser.Parse(trxXml);

        Assert.Equal(3, outcomes.Count);
        var passingEval = TestAssertionEvaluator.Evaluate(
            ["MyLib.Tests.CalculatorTests.Add_ReturnsSum"],
            ["ExistingTest"],
            outcomes);
        Assert.True(passingEval.AllPassed);
        Assert.Equal(1, passingEval.FailToPassPassed);
        Assert.Equal(1, passingEval.PassToPassPassed);

        var failingEval = TestAssertionEvaluator.Evaluate(
            ["Add_ReturnsSum", "Subtract_ReturnsDiff"],
            ["ExistingTest"],
            outcomes);
        Assert.False(failingEval.AllPassed);
        Assert.Equal(["Subtract_ReturnsDiff"], failingEval.FailedTests);
    }

    [Fact]
    public void JUnitXmlTestResultParser_ParsesPytestAndVitestSuites()
    {
        const string junitXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testsuite name="pytest" tests="3" failures="1" skipped="1">
                <testcase classname="tests.test_auth" name="test_valid_token" time="0.01" />
                <testcase classname="tests.test_auth" name="test_expired_token" time="0.02">
                  <failure message="AssertionError">Expected 401</failure>
                </testcase>
                <testcase classname="tests.test_auth" name="test_optional" time="0.00">
                  <skipped message="optional" />
                </testcase>
              </testsuite>
            </testsuites>
            """;

        var outcomes = JUnitXmlTestResultParser.Parse(junitXml);

        Assert.Equal(3, outcomes.Count);
        Assert.True(outcomes[0].Passed);
        Assert.False(outcomes[1].Passed);
        Assert.True(outcomes[2].Skipped);

        var eval = TestAssertionEvaluator.Evaluate(["test_valid_token"], ["tests.test_auth.test_expired_token"], outcomes);
        Assert.False(eval.AllPassed);
        Assert.Equal(1, eval.FailToPassPassed);
        Assert.Equal(0, eval.PassToPassPassed);
    }

    [Fact]
    public void CargoTestResultParser_ParsesRustTestLines()
    {
        const string stdout = """
            running 3 tests
            test parser::tests::handles_empty_input ... ok
            test parser::tests::handles_unicode ... FAILED
            test parser::tests::slow_bench ... ignored

            test result: FAILED. 1 passed; 1 failed; 1 ignored; 0 measured; 0 filtered out
            """;

        var outcomes = CargoTestResultParser.Parse(stdout);

        Assert.Equal(3, outcomes.Count);
        var eval = TestAssertionEvaluator.Evaluate(["handles_empty_input"], ["parser::tests::handles_unicode"], outcomes);
        Assert.False(eval.AllPassed);
        Assert.Equal(1, eval.FailToPassPassed);
        Assert.Equal(0, eval.PassToPassPassed);
    }

    [Fact]
    public void TrxTestResultParser_StripsAssemblyQualification_AndPreservesParameterizedMethodClassFqn()
    {
        const string trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="11111111-1111-1111-1111-111111111111" testName="Compute(1, 2)" outcome="Passed" />
                <UnitTestResult testId="22222222-2222-2222-2222-222222222222" testName="Ns.CalcTests.TheoryTest(x: 5)" outcome="Failed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="11111111-1111-1111-1111-111111111111">
                  <TestMethod className="Ns.CalcTests, MyAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" name="Compute" />
                </UnitTest>
                <UnitTest id="22222222-2222-2222-2222-222222222222">
                  <TestMethod className="Ns.CalcTests, MyAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" name="TheoryTest" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var outcomes = TrxTestResultParser.Parse(trxXml);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal("Ns.CalcTests.Compute(1, 2)", outcomes[0].Name);
        Assert.Equal("Ns.CalcTests.TheoryTest(x: 5)", outcomes[1].Name);

        var passEval = TestAssertionEvaluator.Evaluate(["Ns.CalcTests.Compute"], [], outcomes);
        Assert.True(passEval.AllPassed);

        var failEval = TestAssertionEvaluator.Evaluate(["Ns.CalcTests.TheoryTest"], [], outcomes);
        Assert.False(failEval.AllPassed);
    }

    [Fact]
    public void TestAssertionEvaluator_MatchesPytestNodeIdsAndParameterizedBracketNamesAgainstJUnitXml()
    {
        const string junitXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testsuite name="pytest" tests="3">
                <testcase classname="tests.test_auth.TestAuth" name="test_valid_token" />
                <testcase classname="tests.test_auth" name="test_param_token[case1]" />
                <testcase classname="tests.test_auth" name="test_param_token[case2]" />
              </testsuite>
            </testsuites>
            """;

        var outcomes = JUnitXmlTestResultParser.Parse(junitXml);

        var eval = TestAssertionEvaluator.Evaluate(
            [
                "tests/test_auth.py::TestAuth::test_valid_token",
                "tests/test_auth.py::test_param_token[case1]",
                "tests/test_auth.py::test_param_token"
            ],
            [],
            outcomes);

        Assert.True(eval.AllPassed);
        Assert.Equal(3, eval.FailToPassPassed);
    }

    [Fact]
    public void CargoTestResultParser_ParsesShouldPanicAndIgnoredWithReasonLines()
    {
        const string stdout = """
            running 3 tests
            test parser::tests::rejects_bad_input - should panic ... ok
            test parser::tests::slow_bench ... ignored, expensive
            test parser::tests::panics_wrong - should panic ... FAILED
            """;

        var outcomes = CargoTestResultParser.Parse(stdout);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal("parser::tests::rejects_bad_input", outcomes[0].Name);
        Assert.True(outcomes[0].Passed);
        Assert.Equal("parser::tests::slow_bench", outcomes[1].Name);
        Assert.True(outcomes[1].Skipped);
        Assert.Equal("parser::tests::panics_wrong", outcomes[2].Name);
        Assert.False(outcomes[2].Passed);
    }
}
