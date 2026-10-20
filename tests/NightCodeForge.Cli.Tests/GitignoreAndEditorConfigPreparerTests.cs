namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;
using NightCodeForge.Core.Domain;

public class GitignoreAndEditorConfigPreparerTests
{
    [Fact]
    public void GitignorePreparer_ContainsAllMandatoryEntries_AndMatchesDomainTaskSyncConstants()
    {
        var result = GitignorePreparer.Prepare(null);

        Assert.Contains(Constants.ManagedBlockHeader, result);
        Assert.Contains(Constants.ManagedBlockFooter, result);

        // Every required entry from Domain TaskSyncConstants must be in the block
        foreach (var entry in TaskSyncConstants.RequiredGitIgnoreEntries)
        {
            Assert.Contains(entry, result);
        }

        Assert.Equal(string.Join("\n", TaskSyncConstants.RequiredGitIgnoreEntries), GitignorePreparer.DefaultGitignoreBlock);
    }

    [Fact]
    public void GitignorePreparer_PreservesUserEntries_AndIsIdempotent()
    {
        var userGitignore = "# Custom ignore rules\nnode_modules/\n.env\n";
        var pass1 = GitignorePreparer.Prepare(userGitignore);

        Assert.Contains("# Custom ignore rules", pass1);
        Assert.Contains("node_modules/", pass1);
        Assert.Contains(".env", pass1);
        Assert.Contains("bin/", pass1);
        Assert.Contains("docs/tasks/", pass1);

        var pass2 = GitignorePreparer.Prepare(pass1);
        Assert.Equal(pass1, pass2);
    }

    [Fact]
    public void EditorConfigPreparer_ContainsRootTrueAndLfEnforcement_AndMatchesDomainBaseline()
    {
        var result = EditorConfigPreparer.Prepare(null);

        Assert.Contains(Constants.ManagedBlockHeader, result);
        Assert.Contains("root = true", result);
        Assert.Contains("end_of_line = lf", result);
        Assert.Contains("insert_final_newline = true", result);
        Assert.Contains("trim_trailing_whitespace = true", result);
        Assert.Contains("charset = utf-8", result);

        // Language sections
        Assert.Contains("[*.{cs,csx}]", result);
        Assert.Contains("[*.{json,yml,yaml,md}]", result);
        Assert.Contains("[*.{js,jsx,ts,tsx}]", result);
        Assert.Contains("[*.py]", result);
        Assert.Contains("[*.rs]", result);
        Assert.Contains("[*.go]", result);
        Assert.Contains("[*.{bat,cmd}]", result);
        Assert.Contains(Constants.ManagedBlockFooter, result);

        Assert.Equal(EditorConfigConstants.BaselineEditorConfigContent, EditorConfigPreparer.DefaultEditorConfigBlock);
    }

    [Fact]
    public void EditorConfigPreparer_PreservesUserEntries_AndIsIdempotent()
    {
        var userEditorConfig = "# My custom rules\n[*.proto]\nindent_size = 2\n";
        var pass1 = EditorConfigPreparer.Prepare(userEditorConfig);

        Assert.Contains("# My custom rules", pass1);
        Assert.Contains("[*.proto]", pass1);
        Assert.Contains("root = true", pass1);

        var pass2 = EditorConfigPreparer.Prepare(pass1);
        Assert.Equal(pass1, pass2);
    }

    [Fact]
    public void CorePreparers_FunctionIdenticallyToCliShims()
    {
        var sampleGitignore = "build/\n";
        var cliResult = GitignorePreparer.Prepare(sampleGitignore);
        var coreResult = NightCodeForge.Core.Services.GitignorePreparer.Prepare(sampleGitignore);
        Assert.Equal(cliResult, coreResult);

        var sampleEditorConfig = "[*]\nindent_size = 4\n";
        var cliEcResult = EditorConfigPreparer.Prepare(sampleEditorConfig);
        var coreEcResult = NightCodeForge.Core.Services.EditorConfigPreparer.Prepare(sampleEditorConfig);
        Assert.Equal(cliEcResult, coreEcResult);
    }
}
