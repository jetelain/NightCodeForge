namespace NightCodeForge.Core.Benchmarking;

/// <summary>
/// Problem category evaluated by the Crucible benchmark engine.
/// </summary>
public enum BenchmarkProblemCategory
{
    Implementation = 0,
    CodeReview = 1,
    TaskReview = 2
}

/// <summary>
/// Structured test report format parsed during implementation verification.
/// </summary>
public enum TestResultFormat
{
    Trx = 0,
    JUnitXml = 1,
    CargoTest = 2,
    ExitCode = 3
}

/// <summary>
/// Terminal outcome of evaluating a single benchmark instance for a model candidate.
/// </summary>
public enum BenchmarkInstanceOutcome
{
    Passed = 0,
    Failed = 1,
    Error = 2,
    Timeout = 3,
    Skipped = 4
}

/// <summary>
/// Expected or actual verdict for a Code Review benchmark instance.
/// </summary>
public enum CodeReviewVerdict
{
    Approve = 0,
    MinorFix = 1,
    RequestChanges = 2
}

/// <summary>
/// Expected or actual verdict for a ForgeTask Review benchmark instance.
/// </summary>
public enum TaskReviewVerdict
{
    Ready = 0,
    NeedsClarification = 1
}
