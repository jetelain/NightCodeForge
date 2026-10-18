using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class ReadOnlyCommandValidatorTests
{
    [Theory]
    [InlineData("find . -name \"*.cs\"")]
    [InlineData("fd -e cs")]
    [InlineData("rg -n \"IIdeationService\" src/")]
    [InlineData("grep -rn \"class\" src/")]
    [InlineData("ls -la")]
    [InlineData("tree src/")]
    [InlineData("pwd")]
    [InlineData("cat README.md")]
    [InlineData("head -n 20 src/Program.cs")]
    [InlineData("tail -n 50 log.txt")]
    [InlineData("wc -l src/Program.cs")]
    [InlineData("git status")]
    [InlineData("git log -n 5 --oneline")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git show abc1234")]
    [InlineData("git branch -a")]
    [InlineData("git ls-files")]
    [InlineData("git grep \"interface\"")]
    [InlineData("rg -n \"class > IFoo\" src/")] // Redirect symbol inside double quotes
    [InlineData("echo 'data > output'")]        // Redirect symbol inside single quotes
    public void ValidateCommand_AllowedCommands_Passes(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.True(isAllowed, $"Expected '{command}' to be allowed, but failed with: {errorMsg}");
        Assert.Null(errorMsg);
    }

    [Theory]
    [InlineData("find src/ -name \"*.cs\" | wc -l")]
    [InlineData("rg -n \"TODO\" | head -n 10")]
    [InlineData("git status && git log -n 1")]
    [InlineData("ls -la; pwd")]
    [InlineData("find . -type f | grep -v \".git\" | sort")]
    public void ValidateCommand_PipelinesAndChains_PassesWhenAllAllowed(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.True(isAllowed, $"Expected pipeline '{command}' to be allowed, but failed with: {errorMsg}");
        Assert.Null(errorMsg);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("del file.txt")]
    [InlineData("mkdir new_folder")]
    [InlineData("mv a.cs b.cs")]
    [InlineData("cp a.cs b.cs")]
    [InlineData("touch new_file.txt")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("npm install")]
    [InlineData("cargo build")]
    [InlineData("python -c \"print('exploit')\"")]
    [InlineData("node app.js")]
    [InlineData("chmod +x run.sh")]
    [InlineData("curl https://evil.com")]
    [InlineData("wget https://evil.com")]
    public void ValidateCommand_BlockedMutations_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.Contains("not permitted in read-only exploration mode", errorMsg);
    }

    [Theory]
    [InlineData("echo \"hello\" > file.txt")]
    [InlineData("ls >> out.txt")]
    [InlineData("rg -n \"test\" src/ > result.txt")]
    [InlineData("cat a.txt > b.txt")]
    public void ValidateCommand_FileRedirection_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed);
        Assert.Contains("File redirection", errorMsg);
    }

    [Theory]
    [InlineData("echo $(whoami)")]
    [InlineData("cat `ls`")]
    [InlineData("rg $(cat secret.txt)")]
    public void ValidateCommand_CommandSubstitution_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed);
        Assert.Contains("Command substitution", errorMsg);
    }

    [Theory]
    [InlineData("git commit -m \"mutation\"")]
    [InlineData("git checkout main")]
    [InlineData("git switch feat")]
    [InlineData("git push origin main")]
    [InlineData("git pull")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git clean -fd")]
    [InlineData("git merge other-branch")]
    [InlineData("git rebase main")]
    [InlineData("git branch -D old-branch")]
    [InlineData("git branch -d old-branch")]
    [InlineData("git tag -d v1.0.0")]
    public void ValidateCommand_MutatingGitSubcommands_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed);
        Assert.Contains("not permitted in read-only exploration mode", errorMsg);
    }

    [Fact]
    public async Task CanonicalTools_WhenReadOnly_WriteAndEditThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "original content");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create(), isReadOnly: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tools.WriteAsync("sample.txt", "overwritten"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tools.EditAsync("sample.txt", [new EditChunk("original", "modified")]));

            // Verify Read still succeeds
            var readContent = await tools.ReadAsync("sample.txt");
            Assert.Contains("original content", readContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CanonicalTools_WhenReadOnly_BashAsync_EnforcesValidator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeEnv = new FakeEchoEnv();
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create(), executionEnv: fakeEnv, isReadOnly: true);

            // Mutation command should be blocked with [READ-ONLY VIOLATION]
            var blockedResult = await tools.BashAsync("rm -rf src");
            Assert.StartsWith("[READ-ONLY VIOLATION]:", blockedResult);
            Assert.Contains("rm", blockedResult);
            Assert.False(fakeEnv.WasExecuted, "Execution environment should never have been invoked for blocked command.");

            // Redirection command should be blocked
            var redirectResult = await tools.BashAsync("echo test > file.txt");
            Assert.StartsWith("[READ-ONLY VIOLATION]:", redirectResult);
            Assert.False(fakeEnv.WasExecuted);

            // Permitted command should proceed to execution environment
            var allowedResult = await tools.BashAsync("rg -n \"search\" src");
            Assert.True(fakeEnv.WasExecuted);
            Assert.Contains("Exit Code: 0", allowedResult);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private class FakeEchoEnv : IExecutionEnvironment
    {
        public bool WasExecuted { get; private set; }
        public ExecutionTier Tier => ExecutionTier.BareHost;

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            WasExecuted = true;
            return Task.FromResult(new CommandResult(0, $"Executed: {command}", string.Empty, TimeSpan.FromMilliseconds(10), false));
        }
    }
}
