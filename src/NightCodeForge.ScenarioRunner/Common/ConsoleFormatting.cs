namespace NightCodeForge.ScenarioRunner.Common;

/// <summary>
/// Console styling and formatting utilities providing ANSI color highlights, status badges, and inspection summaries.
/// </summary>
public static class ConsoleFormatting
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";

    public const string Black = "\u001b[30m";
    public const string Red = "\u001b[31m";
    public const string Green = "\u001b[32m";
    public const string Yellow = "\u001b[33m";
    public const string Blue = "\u001b[34m";
    public const string Magenta = "\u001b[35m";
    public const string Cyan = "\u001b[36m";
    public const string White = "\u001b[37m";

    public const string BgBlue = "\u001b[44m";
    public const string BgGreen = "\u001b[42m";
    public const string BgRed = "\u001b[41m";

    public static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine($"{Cyan}{Bold}╔══════════════════════════════════════════════════════════════════════════════════╗{Reset}");
        Console.WriteLine($"{Cyan}{Bold}║                        NightCodeForge Scenario Runner                            ║{Reset}");
        Console.WriteLine($"{Cyan}{Bold}║          Autonomous Overnight Multi-Agent Code Generation Platform               ║{Reset}");
        Console.WriteLine($"{Cyan}{Bold}╚══════════════════════════════════════════════════════════════════════════════════╝{Reset}");
        Console.WriteLine();
    }

    public static void PrintSection(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"{Cyan}{Bold}─── {title.ToUpperInvariant()} ─────────────────────────────────────────────────────────────{Reset}"[..Math.Min(80, 8 + title.Length + 60)]);
    }

    public static void PrintStep(string stepNumber, string description)
    {
        Console.WriteLine($"  {Blue}{Bold}[STEP {stepNumber}]{Reset} {description}");
    }

    public static void PrintSuccess(string message)
    {
        Console.WriteLine($"  {Green}{Bold}[PASS]{Reset} {message}");
    }

    public static void PrintFailure(string message)
    {
        Console.WriteLine($"  {Red}{Bold}[FAIL]{Reset} {message}");
    }

    public static void PrintWarning(string message)
    {
        Console.WriteLine($"  {Yellow}{Bold}[WARN]{Reset} {message}");
    }

    public static void PrintInfo(string message)
    {
        Console.WriteLine($"  {White}{Dim}[INFO]{Reset} {message}");
    }

    public static void PrintDetails(string label, string value)
    {
        Console.WriteLine($"         {Dim}{label}: {Reset}{Cyan}{value}{Reset}");
    }

    public static void PrintInspectionBox(string rootDir, string repoPath, string dbPath)
    {
        Console.WriteLine();
        Console.WriteLine($"{Yellow}{Bold}╔══════════════════════════════════════════════════════════════════════════════════╗{Reset}");
        Console.WriteLine($"{Yellow}{Bold}║                        SYNTHETIC ARTIFACTS KEPT FOR INSPECTION                   ║{Reset}");
        Console.WriteLine($"{Yellow}{Bold}╚══════════════════════════════════════════════════════════════════════════════════╝{Reset}");
        Console.WriteLine($"  {Bold}Directory:{Reset}  {repoPath}");
        Console.WriteLine($"  {Bold}Database:{Reset}   {dbPath}");
        Console.WriteLine();
        Console.WriteLine($"  {Bold}Commands to inspect results:{Reset}");
        Console.WriteLine($"    {Cyan}git -C \"{repoPath}\" status{Reset}");
        Console.WriteLine($"    {Cyan}git -C \"{repoPath}\" log --all --graph --oneline{Reset}");
        Console.WriteLine($"    {Cyan}git -C \"{repoPath}\" branch -a{Reset}");
        Console.WriteLine($"    {Cyan}sqlite3 \"{dbPath}\" \"SELECT Id, StageId, Status, DurationMs FROM StageExecutionRecords;\"{Reset}");
        Console.WriteLine();
    }

    public static void PrintSummaryTable(IReadOnlyList<(string Id, string Name, bool Success, TimeSpan Duration, string Summary)> results, bool kept)
    {
        Console.WriteLine();
        Console.WriteLine($"{Bold}═══════════════════════════════════════════════════════════════════════════════════════════════{Reset}");
        Console.WriteLine($"{Bold}{"SCENARIO",-6} | {"NAME",-42} | {"STATUS",-8} | {"DURATION",-10} | {"SUMMARY"}{Reset}");
        Console.WriteLine($"───────┼────────────────────────────────────────────┼──────────┼────────────┼───────────────────────────");

        int totalPass = 0;
        int totalFail = 0;
        var totalDuration = TimeSpan.Zero;

        foreach (var r in results)
        {
            totalDuration += r.Duration;
            if (r.Success) totalPass++; else totalFail++;

            var statusStr = r.Success ? $"{Green}{Bold}PASS{Reset}" : $"{Red}{Bold}FAIL{Reset}";
            var durStr = $"{r.Duration.TotalSeconds:F2}s";
            var truncSummary = r.Summary.Length > 28 ? r.Summary[..25] + "..." : r.Summary;

            Console.WriteLine($"#{r.Id,-5} | {r.Name,-42} | {statusStr,-17} | {durStr,-10} | {truncSummary}");
        }

        Console.WriteLine($"═══════════════════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"Total: {results.Count} | Passed: {Green}{totalPass}{Reset} | Failed: {(totalFail > 0 ? Red : Green)}{totalFail}{Reset} | Total Duration: {totalDuration.TotalSeconds:F2}s | Artifacts Kept: {(kept ? $"{Yellow}YES{Reset}" : "NO (Cleaned)")}");
        Console.WriteLine();
    }
}
