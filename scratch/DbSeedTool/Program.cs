using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;

var webDir = Path.GetFullPath(@"src\NightCodeForge.Web");
var dbPath = Path.Combine(webDir, "nightcodeforge.db");

Console.WriteLine("==========================================================");
Console.WriteLine(" NightCodeForge - Debug Instance Fixture Seeder           ");
Console.WriteLine("==========================================================");
Console.WriteLine($"Target DB path: {dbPath}");

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseSqlite($"Data Source={dbPath};");

using var context = new AppDbContext(optionsBuilder.Options);
await context.EnsureDatabaseCreatedAndWalAsync();

var defaultModel = "Qwen3.8-27B:latest";
await context.EnsureDefaultGlobalAgentProfilesAsync(defaultModel);
var defaultWorkflow = await context.EnsureDefaultWorkflowsAsync();

// Ensure workspace sequence is at least 1200000
await context.SeedWorkspaceIdSequenceAsync(1200001);

// =========================================================================
// Fixture 1: BareHost Workspace (#1200001)
// =========================================================================
await SeedBareHostFixtureAsync(context, defaultWorkflow);

// =========================================================================
// Fixture 2: ContainerSandbox Workspace (#1200002)
// =========================================================================
await SeedContainerFixtureAsync(context, defaultWorkflow);

Console.WriteLine("\nAll fixtures verified and seeded successfully!");

static async Task SeedBareHostFixtureAsync(AppDbContext context, WorkflowDefinition defaultWorkflow)
{
    const int wsId = 1200001;
    var repoPath = @"C:\Users\Julien\source\repos\SyntheticLib";
    Console.WriteLine($"\n[1/2] Checking BareHost Fixture (Workspace #{wsId})...");

    await EnsureSyntheticRepoScaffoldedAsync(repoPath, "SyntheticLib", "BareHost");

    var existing = await context.Workspaces.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == wsId);
    if (existing == null)
    {
        existing = new Workspace
        {
            Id = wsId,
            Name = "SyntheticLib (Bare Host)",
            RepositoryPath = repoPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            DefaultWorkflowId = defaultWorkflow.Id,
            BuildTimeoutSeconds = 300,
            TestTimeoutSeconds = 300,
            CreatedAt = DateTime.UtcNow
        };
        context.Workspaces.Add(existing);
        await context.SaveChangesAsync();
        Console.WriteLine($"Workspace #{wsId} (BareHost) seeded.");
    }
    else
    {
        Console.WriteLine($"Workspace #{wsId} (BareHost) exists.");
    }

    var tasks = await context.Tasks.Where(t => t.WorkspaceId == wsId).ToListAsync();
    if (tasks.Count == 0)
    {
        Console.WriteLine($"Seeding tasks for Workspace #{wsId}...");
        var t1 = new ForgeTask
        {
            Id = 1200001,
            WorkspaceId = wsId,
            Title = "Add Multiply method to Calculator with XML documentation",
            Description = "Implement Multiply(int a, int b) in Calculator.cs with XML documentation and full unit test coverage.",
            Status = TaskOverallStatus.Closed,
            Origin = TaskOrigin.Ideation,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 6,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.High,
            BaseBranch = "main",
            FeatureBranch = "task/1200001-multiply",
            CurrentStageId = "auto-merge",
            SquashCommitSha = "e2e0001a",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-45),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
        };

        var t2 = new ForgeTask
        {
            Id = 1200002,
            WorkspaceId = wsId,
            Title = "Implement Divide method with DivideByZeroException handling",
            Description = "Add Divide(int a, int b) ensuring zero division throws DivideByZeroException, and add unit tests.",
            Status = TaskOverallStatus.Ready,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 5,
            MergeMode = MergeMode.ManualReviewRequired,
            Priority = TaskPriority.High,
            BaseBranch = "main",
            FeatureBranch = "task/1200002-divide-method",
            CurrentStageId = "code-review-fix",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var t3 = new ForgeTask
        {
            Id = 1200003,
            WorkspaceId = wsId,
            Title = "Implement Power (Exponentiation) method",
            Description = "Add Power(int baseVal, int exp) supporting non-negative exponents.",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 1,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.Normal,
            BaseBranch = "main",
            FeatureBranch = "task/1200003-power",
            CurrentStageId = "task-review",
            IsWaitingForHuman = true,
            InterruptedAgentProfileId = "task-review",
            InterruptedStageId = "task-review",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-15),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        t3.Questions.Add(new Question
        {
            TaskId = 1200003,
            StageId = "task-review",
            Prompt = "Should Power(int baseVal, int exp) support negative exponents with decimal result, or throw ArgumentOutOfRangeException for exp < 0?",
            Rationale = "The return type is currently integer. If negative exponents are permitted, the return type should be double or decimal.",
            QuestionType = QuestionType.MultipleChoice,
            OptionsJson = "[\"Throw ArgumentOutOfRangeException for negative exponents (keep int signature)\",\"Change return type to double and support negative exponents\"]",
            Status = QuestionStatus.Pending,
            AgentProfileId = "task-review",
            BatchId = Guid.NewGuid().ToString("N")[..8],
            AskedAt = DateTime.UtcNow.AddMinutes(-2)
        });

        var t4 = new ForgeTask
        {
            Id = 1200004,
            WorkspaceId = wsId,
            Title = "Add Percentage calculation helper",
            Description = "Create a PercentageOf(double value, double total) utility in Calculator.",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = false,
            MaxExecutedStageOrder = 0,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.Normal,
            BaseBranch = "main",
            FeatureBranch = "task/1200004-percentage",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var t5 = new ForgeTask
        {
            Id = 1200005,
            WorkspaceId = wsId,
            Title = "Refactor Calculator into static helper",
            Description = "Consider making Calculator a static class since all mathematical operations are currently pure functions.",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.Ideation,
            HasExecutionRun = false,
            MaxExecutedStageOrder = 0,
            MergeMode = MergeMode.ManualReviewRequired,
            Priority = TaskPriority.Low,
            BaseBranch = "main",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        context.Tasks.AddRange(t1, t2, t3, t4, t5);
        await context.SaveChangesAsync();
        Console.WriteLine($"5 tasks seeded for Workspace #{wsId}.");
    }
}

static async Task SeedContainerFixtureAsync(AppDbContext context, WorkflowDefinition defaultWorkflow)
{
    const int wsId = 1200002;
    var repoPath = @"C:\Users\Julien\source\repos\SyntheticLib-Container";
    var ext4Path = $"/home/gruearbre/.nightcodeforge/wks/{wsId}/clone";
    Console.WriteLine($"\n[2/2] Checking ContainerSandbox Fixture (Workspace #{wsId})...");

    await EnsureSyntheticRepoScaffoldedAsync(repoPath, "SyntheticLib-Container", "Container");

    // Initialize WSL2 Ext4 mirror
    try
    {
        var wslHostRepo = "/mnt/c/Users/Julien/source/repos/SyntheticLib-Container";
        await RunCliAsync("wsl", $"mkdir -p /home/gruearbre/.nightcodeforge/wks/{wsId}", ".");
        await RunCliAsync("wsl", $"rm -rf {ext4Path}", ".");
        await RunCliAsync("wsl", $"git clone {wslHostRepo} {ext4Path}", ".");
        await RunCliAsync("wsl", $"git -C {ext4Path} remote add host-origin {wslHostRepo}", ".");
        await RunCliAsync("wsl", $"git -C {ext4Path} config user.name \"NCF Container Bot\"", ".");
        await RunCliAsync("wsl", $"git -C {ext4Path} config user.email \"bot@nightcodeforge.local\"", ".");
        Console.WriteLine($"WSL Ext4 Git mirror at '{ext4Path}' ready.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning initializing WSL mirror: {ex.Message}");
    }

    var existing = await context.Workspaces.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == wsId);
    if (existing == null)
    {
        existing = new Workspace
        {
            Id = wsId,
            Name = "SyntheticLib (Container Sandbox)",
            RepositoryPath = repoPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = "ncf-agent-base:latest",
            ContainerName = $"ncf-sandbox-{wsId}",
            Ext4MirrorPath = ext4Path,
            UseWslForContainers = true,
            WslDistribution = "Ubuntu",
            DefaultWorkflowId = defaultWorkflow.Id,
            BuildTimeoutSeconds = 300,
            TestTimeoutSeconds = 300,
            CreatedAt = DateTime.UtcNow
        };
        context.Workspaces.Add(existing);
        await context.SaveChangesAsync();
        Console.WriteLine($"Workspace #{wsId} (ContainerSandbox) seeded.");
    }
    else
    {
        Console.WriteLine($"Workspace #{wsId} (ContainerSandbox) exists.");
    }

    var tasks = await context.Tasks.Where(t => t.WorkspaceId == wsId).ToListAsync();
    if (tasks.Count == 0)
    {
        Console.WriteLine($"Seeding tasks for Workspace #{wsId}...");
        var t1 = new ForgeTask
        {
            Id = 1200011,
            WorkspaceId = wsId,
            Title = "Container: Implement Multiply method with XML documentation",
            Description = "Executed within ncf-agent-base container on Ext4 mirror with rootless Docker.",
            Status = TaskOverallStatus.Closed,
            Origin = TaskOrigin.Ideation,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 6,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.High,
            BaseBranch = "main",
            FeatureBranch = "task/1200011-multiply",
            CurrentStageId = "auto-merge",
            SquashCommitSha = "e2e0002c",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-40),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-15)
        };

        var t2 = new ForgeTask
        {
            Id = 1200012,
            WorkspaceId = wsId,
            Title = "Container: Implement Divide method with zero check",
            Description = "Add Divide(int a, int b) with DivideByZeroException handling in container sandbox.",
            Status = TaskOverallStatus.Ready,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 5,
            MergeMode = MergeMode.ManualReviewRequired,
            Priority = TaskPriority.High,
            BaseBranch = "main",
            FeatureBranch = "task/1200012-divide-method",
            CurrentStageId = "code-review-fix",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-25),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var t3 = new ForgeTask
        {
            Id = 1200013,
            WorkspaceId = wsId,
            Title = "Container: Implement SquareRoot method",
            Description = "Add Sqrt(double val) with precision configuration.",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 1,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.Normal,
            BaseBranch = "main",
            FeatureBranch = "task/1200013-sqrt",
            CurrentStageId = "task-review",
            IsWaitingForHuman = true,
            InterruptedAgentProfileId = "task-review",
            InterruptedStageId = "task-review",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-12),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        t3.Questions.Add(new Question
        {
            TaskId = 1200013,
            StageId = "task-review",
            Prompt = "Should Sqrt return double.NaN or throw ArgumentException when given a negative input?",
            Rationale = "Math.Sqrt in .NET returns double.NaN, but domain standards often prefer explicit argument exceptions.",
            QuestionType = QuestionType.MultipleChoice,
            OptionsJson = "[\"Throw ArgumentException with clear message\",\"Return double.NaN to match Math.Sqrt behavior\"]",
            Status = QuestionStatus.Pending,
            AgentProfileId = "task-review",
            BatchId = Guid.NewGuid().ToString("N")[..8],
            AskedAt = DateTime.UtcNow.AddMinutes(-2)
        });

        var t4 = new ForgeTask
        {
            Id = 1200014,
            WorkspaceId = wsId,
            Title = "Container: Add Modulo operation helper",
            Description = "Implement Modulo(int a, int b) helper method.",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            HasExecutionRun = false,
            MaxExecutedStageOrder = 0,
            MergeMode = MergeMode.Automatic,
            Priority = TaskPriority.Normal,
            BaseBranch = "main",
            FeatureBranch = "task/1200014-modulo",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-8),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-8)
        };

        var t5 = new ForgeTask
        {
            Id = 1200015,
            WorkspaceId = wsId,
            Title = "Container: Optimize inner math loops with SIMD",
            Description = "Investigate System.Numerics.Vector for batch arithmetic operations inside container.",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.Ideation,
            HasExecutionRun = false,
            MaxExecutedStageOrder = 0,
            MergeMode = MergeMode.ManualReviewRequired,
            Priority = TaskPriority.Low,
            BaseBranch = "main",
            WorkflowId = defaultWorkflow.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-4),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-4)
        };

        context.Tasks.AddRange(t1, t2, t3, t4, t5);
        await context.SaveChangesAsync();
        Console.WriteLine($"5 tasks seeded for Workspace #{wsId}.");
    }

    // Ensure sqlite_sequence for Tasks is updated
    using var conn = context.Database.GetDbConnection();
    await conn.OpenAsync();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE sqlite_sequence SET seq = MAX(seq, 1200015) WHERE name = 'Tasks';";
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureSyntheticRepoScaffoldedAsync(string repoPath, string name, string mode)
{
    if (Directory.Exists(repoPath) && Directory.Exists(Path.Combine(repoPath, ".git")))
    {
        return;
    }

    Directory.CreateDirectory(repoPath);
    var srcDir = Path.Combine(repoPath, "src", "SyntheticLib");
    var testDir = Path.Combine(repoPath, "tests", "SyntheticLib.Tests");
    Directory.CreateDirectory(srcDir);
    Directory.CreateDirectory(testDir);
    Directory.CreateDirectory(Path.Combine(repoPath, ".nightcodeforge"));
    Directory.CreateDirectory(Path.Combine(repoPath, "docs", "tasks"));
    Directory.CreateDirectory(Path.Combine(repoPath, "docs", "transcripts"));

    await File.WriteAllTextAsync(Path.Combine(srcDir, "SyntheticLib.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>");

    await File.WriteAllTextAsync(Path.Combine(srcDir, "Calculator.cs"), @"namespace SyntheticLib;

/// <summary>
/// Arithmetic computation engine.
/// </summary>
public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Subtract(int a, int b) => a - b;
    public int Multiply(int a, int b) => a * b;
}
");

    await File.WriteAllTextAsync(Path.Combine(testDir, "SyntheticLib.Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.14.1"" />
    <PackageReference Include=""xunit"" Version=""2.9.3"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""3.1.4"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\..\src\SyntheticLib\SyntheticLib.csproj"" />
  </ItemGroup>
</Project>");

    await File.WriteAllTextAsync(Path.Combine(testDir, "CalculatorTests.cs"), @"namespace SyntheticLib.Tests;

using SyntheticLib;
using Xunit;

public class CalculatorTests
{
    private readonly Calculator _calc = new();

    [Fact]
    public void Add_ReturnsCorrectSum() => Assert.Equal(5, _calc.Add(2, 3));

    [Fact]
    public void Subtract_ReturnsCorrectDifference() => Assert.Equal(1, _calc.Subtract(4, 3));

    [Fact]
    public void Multiply_ReturnsCorrectProduct() => Assert.Equal(12, _calc.Multiply(3, 4));
}
");

    await File.WriteAllTextAsync(Path.Combine(repoPath, ".nightcodeforge", "config.json"),
        @"{""CodeStack"": ""dotnet"", ""BuildCommand"": ""dotnet build"", ""TestCommand"": ""dotnet test --no-build""}");

    await File.WriteAllTextAsync(Path.Combine(repoPath, "AGENTS.md"),
        "# Directives\n- Maintain 100% test coverage.\n- Public APIs must include XML doc.\n- Add unit tests for every method.\n");

    await File.WriteAllTextAsync(Path.Combine(repoPath, ".gitignore"),
        "bin/\nobj/\n.vs/\ndocs/tasks/\ndocs/transcripts/\nTestResults/\n*.trx\n");

    await File.WriteAllTextAsync(Path.Combine(repoPath, ".gitattributes"),
        "* text=auto eol=lf\n");

    await RunCliAsync("dotnet", "new sln", repoPath);
    await RunCliAsync("dotnet", "sln add src/SyntheticLib/SyntheticLib.csproj", repoPath);
    await RunCliAsync("dotnet", "sln add tests/SyntheticLib.Tests/SyntheticLib.Tests.csproj", repoPath);

    await RunCliAsync("git", "init -b main", repoPath);
    await RunCliAsync("git", "config user.name \"NightCodeForge Seeder\"", repoPath);
    await RunCliAsync("git", "config user.email \"seeder@nightcodeforge.local\"", repoPath);
    await RunCliAsync("git", "config commit.gpgsign false", repoPath);
    await RunCliAsync("git", "config receive.denyCurrentBranch updateInstead", repoPath);
    await RunCliAsync("git", "config --local core.autocrlf false", repoPath);
    await RunCliAsync("git", "add -A", repoPath);
    await RunCliAsync("git", "commit -m \"Initial commit: Scaffold synthetic test library\" --no-verify", repoPath);

    // Feature branch for Divide
    await RunCliAsync("git", "checkout -b task/divide-method", repoPath);
    await File.WriteAllTextAsync(Path.Combine(srcDir, "Calculator.cs"), @"namespace SyntheticLib;

public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Subtract(int a, int b) => a - b;
    public int Multiply(int a, int b) => a * b;
    public int Divide(int a, int b)
    {
        if (b == 0) throw new DivideByZeroException();
        return a / b;
    }
}
");
    await RunCliAsync("git", "add -A", repoPath);
    await RunCliAsync("git", "commit -m \"feat: Add Divide method\" --no-verify", repoPath);
    await RunCliAsync("git", "checkout main", repoPath);
}

static async Task<string> RunCliAsync(string fileName, string args, string workingDir)
{
    var psi = new ProcessStartInfo(fileName, args)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var proc = Process.Start(psi)!;
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();

    var stdout = await stdoutTask;
    var stderr = await stderrTask;

    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command '{fileName} {args}' in '{workingDir}' failed with code {proc.ExitCode}: {stderr}\n{stdout}");
    }
    return stdout.Trim();
}
