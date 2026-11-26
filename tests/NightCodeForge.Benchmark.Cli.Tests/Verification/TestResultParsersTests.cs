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

    [Fact]
    public void TestAssertionEvaluator_MatchesVitestAngleBracketHierarchyAgainstJUnitXml()
    {
        const string junitXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testsuite name="packages/compiler-core/__tests__/parse.spec.ts" tests="3" failures="1">
                <testcase classname="packages/compiler-core/__tests__/parse.spec.ts" name="compiler: parse &gt; Element &gt; simple div" time="0.01" />
                <testcase classname="packages/compiler-core/__tests__/parse.spec.ts" name="compiler: parse &gt; Element &gt; v-pre with half-open interpolation" time="0.01" />
                <testcase classname="packages/compiler-core/__tests__/parse.spec.ts" name="compiler: parse &gt; Element &gt; broken tag" time="0.01">
                  <failure message="AssertionError">mismatch</failure>
                </testcase>
              </testsuite>
            </testsuites>
            """;

        var outcomes = JUnitXmlTestResultParser.Parse(junitXml);

        var passEval = TestAssertionEvaluator.Evaluate(
            [
                "packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > simple div",
                "compiler: parse > Element > v-pre with half-open interpolation"
            ],
            [],
            outcomes);
        Assert.True(passEval.AllPassed);
        Assert.Equal(2, passEval.FailToPassPassed);

        var failEval = TestAssertionEvaluator.Evaluate(
            ["packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > broken tag"],
            [],
            outcomes);
        Assert.False(failEval.AllPassed);
        Assert.Equal(["packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > broken tag"], failEval.FailedTests);
    }

    [Fact]
    public void TestAssertionEvaluator_MatchesVitestLeafNamesBackslashClassnamesAndSkippedTests()
    {
        const string junitXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testsuite name="suite" tests="3" skipped="1">
                <testcase classname="packages\compiler-core\__tests__\parse.spec.ts" name="compiler: parse &gt; Element &gt; simple div" time="0.01" />
                <testcase classname="packages\compiler-core\__tests__\parse.spec.ts" name="renders" time="0.01" />
                <testcase classname="packages/compiler-core/__tests__/parse.spec.ts" name="compiler: parse &gt; Element &gt; skipped case" time="0.01">
                  <skipped />
                </testcase>
              </testsuite>
            </testsuites>
            """;

        var outcomes = JUnitXmlTestResultParser.Parse(junitXml);

        // Leaf test name (no separator) matching an outcome containing " > " and backslashes
        var leafEval = TestAssertionEvaluator.Evaluate(["simple div"], [], outcomes);
        Assert.True(leafEval.AllPassed);

        // Dot-delimited expected identifier matching backslash-separated classname in outcome
        var backslashEval = TestAssertionEvaluator.Evaluate(
            ["packages.compiler-core.__tests__.parse.spec.ts.renders"],
            [],
            outcomes);
        Assert.True(backslashEval.AllPassed);

        // Skipped Vitest test must not be counted as passed
        var skippedEval = TestAssertionEvaluator.Evaluate(
            ["packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > skipped case"],
            [],
            outcomes);
        Assert.False(skippedEval.AllPassed);
    }

    [Fact]
    public void JUnitXmlTestResultParser_AndEvaluator_HandleMochaJUnitWithUnrelatedSocketFailure()
    {
        const string mochaJunitXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuites name="Mocha Tests" time="0.250" tests="4" failures="1">
              <testsuite name="supports http with nodejs" timestamp="2026-11-24T12:00:00" tests="4" failures="1" time="0.250">
                <testcase name="supports http with nodejs defaults should transform request json" time="0.010" classname="should transform request json" />
                <testcase name="supports http with nodejs should follow redirects" time="0.015" classname="should follow redirects" />
                <testcase name="supports http with nodejs should parse custom headers" time="0.012" classname="should parse custom headers" />
                <testcase name="supports http with nodejs should support sockets" time="0.005" classname="should support sockets">
                  <failure message="listen ENOTSUP: operation not supported on socket ./test.sock" type="Error">Error: listen ENOTSUP</failure>
                </testcase>
              </testsuite>
            </testsuites>
            """;

        var outcomes = JUnitXmlTestResultParser.Parse(mochaJunitXml);

        Assert.Equal(4, outcomes.Count);
        Assert.Equal("supports http with nodejs defaults should transform request json", outcomes[0].Name);
        Assert.True(outcomes[0].Passed);
        Assert.False(outcomes[3].Passed);

        // F2P/P2P assertions evaluate accurately despite unrelated ENOTSUP socket failure
        var eval = TestAssertionEvaluator.Evaluate(
            ["supports http with nodejs > defaults > should transform request json"],
            ["supports http with nodejs should follow redirects", "should parse custom headers"],
            outcomes);
        Assert.True(eval.AllPassed);
        Assert.Equal(1, eval.FailToPassPassed);
        Assert.Equal(2, eval.PassToPassPassed);

        // Failing socket test is still caught when explicitly asserted
        var socketEval = TestAssertionEvaluator.Evaluate(
            ["supports http with nodejs > should support sockets"],
            [],
            outcomes);
        Assert.False(socketEval.AllPassed);
        Assert.Equal(["supports http with nodejs > should support sockets"], socketEval.FailedTests);

        // Repeated whitespace in Mocha identifiers is collapsed during matching
        var whitespaceOutcomes = new[]
        {
            new ParsedTestOutcome("supports http   should transform request json", Passed: true)
        };
        var whitespaceEval = TestAssertionEvaluator.Evaluate(
            ["supports http > should transform request json"],
            [],
            whitespaceOutcomes);
        Assert.True(whitespaceEval.AllPassed);
    }

    [Fact]
    public void TestAssertionEvaluator_DoesNotFallThroughFromMatchedFailingTestToMochaSpaceSuffix()
    {
        var rawOutcomes = new[]
        {
            new ParsedTestOutcome("Bar.foo", Passed: false),
            new ParsedTestOutcome("Baz foo", Passed: true)
        };
        var rawEval = TestAssertionEvaluator.Evaluate(["foo"], [], rawOutcomes);
        Assert.False(rawEval.AllPassed);
        Assert.Equal(["foo"], rawEval.FailedTests);

        var normalizedOutcomes = new[]
        {
            new ParsedTestOutcome("SuiteA::test_foo", Passed: false),
            new ParsedTestOutcome("other SuiteA.test_foo", Passed: true)
        };
        var normalizedEval = TestAssertionEvaluator.Evaluate(["SuiteA::test_foo"], [], normalizedOutcomes);
        Assert.False(normalizedEval.AllPassed);
        Assert.Equal(["SuiteA::test_foo"], normalizedEval.FailedTests);
    }

    [Fact]
    public void Step1163_TestAssertionEvaluator_MatchesUpstreamTruncatedPytestParameterizedIdsSafely()
    {
        var outcomes = new[]
        {
            new ParsedTestOutcome("testing.test_mark.test_pytest_param_id_allows_none_or_string[hello world]", Passed: true),
            new ParsedTestOutcome("testing.test_mark.test_mark_option[not xyz-expected_passed3]", Passed: true),
            new ParsedTestOutcome("tests.config.test_config.test_csv_regex_comma_in_quantifier[foo,bar]", Passed: true),
            new ParsedTestOutcome("testing.test_mark.test_mark_option_custom[not failed]", Passed: false)
        };

        var eval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_mark_option[not"],
            [
                "testing/test_mark.py::test_pytest_param_id_allows_none_or_string[hello",
                "tests/config/test_config.py::test_csv_regex_comma_in_quantifier[foo,"
            ],
            outcomes);

        Assert.True(eval.AllPassed);
        Assert.Equal(1, eval.FailToPassPassed);
        Assert.Equal(2, eval.PassToPassPassed);

        // Failing outcome under matching prefix must still fail evaluation
        var failEval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_mark_option_custom[not"],
            [],
            outcomes);
        Assert.False(failEval.AllPassed);

        // Different method name must NOT match an unrelated passing test
        var mismatchEval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_mark_other[not"],
            [],
            outcomes);
        Assert.False(mismatchEval.AllPassed);

        // Closing bracket before an unbalanced opening bracket must still be detected as unbalanced
        var outcomesWithClosing = new[]
        {
            new ParsedTestOutcome("testing.test_mark.test_bracket[a]b[c d]", Passed: true)
        };
        var closingBeforeOpenEval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_bracket[a]b[c"],
            [],
            outcomesWithClosing);
        Assert.True(closingBeforeOpenEval.AllPassed);

        // Truncated alphanumeric prefix must match delimiter-separated full parameter IDs without colliding with longer alphanumeric tokens
        var delimiterOutcomes = new[]
        {
            new ParsedTestOutcome("testing.test_mark.test_keyword_option_custom[1 or 2]", Passed: true)
        };
        var delimiterMatchEval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_keyword_option_custom[1"],
            [],
            delimiterOutcomes);
        Assert.True(delimiterMatchEval.AllPassed);

        var numericBoundaryOutcomes = new[]
        {
            new ParsedTestOutcome("testing.test_mark.test_keyword_option_custom[10]", Passed: true)
        };
        var numericBoundaryEval = TestAssertionEvaluator.Evaluate(
            ["testing/test_mark.py::test_keyword_option_custom[1"],
            [],
            numericBoundaryOutcomes);
        Assert.False(numericBoundaryEval.AllPassed);
    }
}
