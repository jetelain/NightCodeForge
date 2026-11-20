using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class ReadOnlyCommandValidatorTests
{
    [Theory]
    [InlineData("find . -name \"*.cs\"")]
    [InlineData("find . -maxdepth 3 -name \"*.cs\"")]
    [InlineData("fd -e cs")]
    [InlineData("rg -n \"IIdeationConversationCoordinator\" src/")]
    [InlineData("rg -n -- 'pattern' src")]
    [InlineData("grep -rn \"class\" src/")]
    [InlineData("ls -la")]
    [InlineData("tree src/")]
    [InlineData("pwd")]
    [InlineData("cat README.md")]
    [InlineData("cat src/File.cs")]
    [InlineData("head -n 20 src/Program.cs")]
    [InlineData("head -n 20")]
    [InlineData("tail -n 50 log.txt")]
    [InlineData("tail -n 20")]
    [InlineData("wc -l src/Program.cs")]
    [InlineData("wc -l")]
    [InlineData("stat src/Program.cs")]
    [InlineData("diff a.cs b.cs")]
    [InlineData("sed -n \"10,20p\" file.cs")]
    [InlineData("awk '{print $1}' file.txt")]
    [InlineData("git status")]
    [InlineData("git log -n 5")]
    [InlineData("git log -n 5 --oneline")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git show abc1234")]
    [InlineData("git show HEAD")]
    [InlineData("git branch")]
    [InlineData("git branch -a")]
    [InlineData("git branch --show-current")]
    [InlineData("git tag")]
    [InlineData("git tag -l \"v1.*\"")]
    [InlineData("git remote -v")]
    [InlineData("git remote show origin")]
    [InlineData("git ls-files")]
    [InlineData("git grep \"interface\"")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("git blame src/File.cs")]
    [InlineData("rg pattern src 2>/dev/null")]
    [InlineData("git status 2>&1")]
    [InlineData("rg -n \"class > IFoo\" src/")] // Redirect symbol inside double quotes
    [InlineData("echo 'data > output'")]        // Redirect symbol inside single quotes
    [InlineData("csnav definition -t CrucibleAgentHarness")]
    [InlineData("csnav explore -t IdeationConversationCoordinator")]
    [InlineData("csnav references -t IWorkspaceSymbolService")]
    [InlineData("csnav refs --symbol Foo")]
    [InlineData("csnav search \"*Symbol*\"")]
    [InlineData("csnav api-search -p \"*Symbol*\"")]
    [InlineData("csnav implementations --symbol IFoo")]
    [InlineData("csnav inspect --type Foo")]
    [InlineData("csnav decompile --type System.Uri")]
    [InlineData("csnav sync")]
    [InlineData("csnav server status")]
    [InlineData("csnav server start -s NightCodeForge.sln")]
    [InlineData("csnav -s NightCodeForge.sln server reload")]
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
    [InlineData("bash -c \"rm -rf /\"")]
    [InlineData("sh -c \"ls\"")]
    [InlineData("./script.sh")]
    [InlineData("/tmp/evil")]
    public void ValidateCommand_DisallowedBinariesAndPaths_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
    }

    [Theory]
    [InlineData("git checkout main")]
    [InlineData("git commit -m \"mutation\"")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git clean -fd")]
    [InlineData("git push origin main")]
    [InlineData("git stash")]
    [InlineData("git branch new-branch")]
    [InlineData("git branch -D old")]
    [InlineData("git branch -d old")]
    [InlineData("git tag v1.0")]
    [InlineData("git tag -d v1.0")]
    [InlineData("git remote add origin url")]
    [InlineData("git -c core.pager=less status")]
    [InlineData("git diff --output=/tmp/out.patch")]
    [InlineData("git grep -Osh foo")]
    public void ValidateCommand_MutatingGitCommands_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
    }

    [Theory]
    [InlineData("csnav skills install")]
    [InlineData("csnav skill install")]
    [InlineData("csnav server stop")]
    [InlineData("csnav")]
    [InlineData("csnav -s NightCodeForge.sln")]
    public void ValidateCommand_DisallowedCsNavCommands_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
    }

    [Theory]
    [InlineData("find . -delete")]
    [InlineData("find . -exec rm {} +")]
    [InlineData("find . -fprint /tmp/out.txt")]
    [InlineData("rg --pre=sh pattern")]
    [InlineData("sed -i \"s/a/b/\" file.cs")]
    [InlineData("sed -ni \"s/a/b/\" file.cs")]
    [InlineData("sed --in-place file.cs")]
    public void ValidateCommand_MutatingToolFlags_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
    }

    [Theory]
    [InlineData("awk -F: '{print $1}' /etc/hosts")]
    [InlineData("awk '$3 > 100 {print $1}' data.txt")]
    [InlineData("awk 'length($0) > 80' file.md")]
    [InlineData("awk '$1 == \"a\" || $2 == \"b\" {print $0}' data.txt")]
    [InlineData("awk '{print $1} $3 > 100 {print $2}' data.txt")]
    [InlineData("awk '/>>/{print $0}' file.txt")]
    [InlineData("cat /workspace/memory/MEMORY.md")]
    [InlineData("sed -n '/foo/p' file.md")]
    [InlineData("sed -n '/foo/p' /foo/workspace/bar.md")]
    [InlineData("sed -n '1,10p' /workspace/memory/MEMORY.md")]
    [InlineData("sed -n '1,10p' /foo/workspace/bar.md")]
    [InlineData("sed -n '1,10p' workspace/README.md")]
    [InlineData("sed -n '1,5p' memory/MEMORY.md")]
    [InlineData("sed 's/we/you/' file.md")]
    [InlineData("sed 's/a/b/g' src/series/sew.cs")]
    [InlineData("sed -e 's/a/b/g' -e 's/c/d/' file.md")]
    [InlineData("sort -u file.txt")]
    public void ValidateCommand_SafeAwkAndSedPrograms_Passes(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.True(isAllowed, $"Expected '{command}' to be allowed, but failed with: {errorMsg}");
    }

    [Theory]
    [InlineData("awk '{print > \"/workspace/memory/x.md\"}' file.txt")]
    [InlineData("awk '{print >> \"/workspace/memory/x.md\"}' file.txt")]
    [InlineData("awk '{printf \"%s\", $1 > \"out.md\"}' file.txt")]
    [InlineData("awk '{print \"a;b\" > \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '{print \"a}b\" > \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '{print \"a;b\" >> \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '{print 10 / 2 > \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '{print ($0 ~ /;/) > \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '{print \"x\" \\\n > \"/workspace/memory/x.sh\"}' file.txt")]
    [InlineData("awk '@load \"filefuncs\"' file.txt")]
    [InlineData("awk '@include \"/etc/passwd\"' file.txt")]
    [InlineData("awk -l filefuncs '{print $1}' file.txt")]
    [InlineData("awk -i lib.awk '{print $1}' file.txt")]
    [InlineData("awk 'BEGIN{system(\"touch x\")}'")]
    [InlineData("awk 'BEGIN{\"date\" | getline d; print d}'")]
    [InlineData("awk '{print $1 | \"sh\"}' file.txt")]
    [InlineData("awk -f script.awk file.txt")]
    [InlineData("awk --file script.awk file.txt")]
    [InlineData("sed -n 'w /workspace/memory/x.md' file.txt")]
    [InlineData("sed -n '/foo/w out.md' file.txt")]
    [InlineData("sed -n 'W /workspace/memory/x.sh' file.txt")]
    [InlineData("sed '1W /workspace/memory/x.sh' file.txt")]
    [InlineData("sed -n 'w/workspace/memory/x.sh' file.txt")]
    [InlineData("sed '1w/workspace/memory/x.sh' file.txt")]
    [InlineData("sed '/foo/w/workspace/memory/x.sh' file.txt")]
    [InlineData("sed '/foo/wout.sh' file.txt")]
    [InlineData("sed -n '/foo/wout.sh' file.txt")]
    [InlineData("sed '1,/foo/wout.sh' file.txt")]
    [InlineData("sed '1e/bin/sh' file.txt")]
    [InlineData("sed 's/a/b/p;w/workspace/memory/x.sh' file.txt")]
    [InlineData("sed -e'w /workspace/memory/x.sh' file.txt")]
    [InlineData("sed --expression='w /workspace/memory/x.sh' file.txt")]
    [InlineData("sed --expression='e id' file.txt")]
    [InlineData("sed -e '/foo/wout.sh' file.txt")]
    [InlineData("sed 'wout.sh' file.txt")]
    [InlineData("sed -n 'wout.sh' file.txt")]
    [InlineData("sed 's_a_b_w /workspace/memory/x.sh' file.txt")]
    [InlineData("sed 's_a_b_e' file.txt")]
    [InlineData("sed 's/a/b/w out.md' file.txt")]
    [InlineData("sed 's/a/b/e' file.txt")]
    [InlineData("sed '1e echo hi' file.txt")]
    [InlineData("sed -f script.sed file.txt")]
    [InlineData("sed --file=script.sed file.txt")]
    [InlineData("sed -i 's/a/b/' /workspace/memory/x.md")]
    [InlineData("sort -o /workspace/memory/out.sh file.txt")]
    [InlineData("sort --output=/workspace/memory/out.sh file.txt")]
    [InlineData("sort --output /workspace/memory/out.sh file.txt")]
    public void ValidateCommand_AwkAndSedWritesAndExec_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
    }
    [Theory]
    [InlineData("echo \"hello\" > file.txt")]
    [InlineData("ls >> out.txt")]
    [InlineData("cat < /etc/passwd")]
    [InlineData("echo $(whoami)")]
    [InlineData("cat `ls`")]
    [InlineData("echo $HOME")]
    [InlineData("find . $'-exec' rm -rf {} +")]
    [InlineData("diff <(ls a) <(ls b)")]
    public void ValidateCommand_ShellEscapeHatches_Rejects(string command)
    {
        var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);

        Assert.False(isAllowed, $"Expected '{command}' to be blocked, but it was allowed.");
        Assert.False(string.IsNullOrWhiteSpace(errorMsg));
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

            // Redirection command should be blocked with [READ-ONLY VIOLATION]
            var redirectResult = await tools.BashAsync("echo test > file.txt");
            Assert.StartsWith("[READ-ONLY VIOLATION]:", redirectResult);
            Assert.Contains("redirection", redirectResult, StringComparison.OrdinalIgnoreCase);
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
