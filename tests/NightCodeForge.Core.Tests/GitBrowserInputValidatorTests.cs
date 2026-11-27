using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Security;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class GitBrowserInputValidatorTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/task-42")]
    [InlineData("release/v1.2.3")]
    [InlineData("a1b2c3d4e5f6")]
    public void ValidateRef_ValidRefs_ReturnsTrimmedRef(string validRef)
    {
        Assert.Equal(validRef, GitBrowserInputValidator.ValidateRef($"  {validRef}  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-exec=evil")]
    [InlineData("--orphan")]
    [InlineData("main:README.md")]
    [InlineData("main..develop")]
    [InlineData("main@{0}")]
    [InlineData("feature\\task-1")]
    [InlineData("main branch")]
    [InlineData("main\n")]
    [InlineData("main\0")]
    [InlineData("HEAD~1")]
    [InlineData("HEAD^")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("@")]
    [InlineData(".hidden")]
    [InlineData("feature/.bad")]
    [InlineData("main.lock")]
    public void ValidateRef_InvalidOrInjectionRefs_ThrowsGitBrowserInvalidInputException(string? invalidRef)
    {
        Assert.Throws<GitBrowserInvalidInputException>(() => GitBrowserInputValidator.ValidateRef(invalidRef));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("src/Core/Foo.cs", "src/Core/Foo.cs")]
    [InlineData("/src/Core/Foo.cs/", "src/Core/Foo.cs")]
    [InlineData("./src/./Core/Foo.cs", "src/Core/Foo.cs")]
    [InlineData("src\\Core\\Foo.cs", "src/Core/Foo.cs")]
    public void NormalizeAndValidatePath_ValidPaths_NormalizesToForwardSlashes(string? input, string expected)
    {
        Assert.Equal(expected, GitBrowserInputValidator.NormalizeAndValidatePath(input));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("..\\secret")]
    [InlineData("src\\..\\..\\secret")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("//server/share/file")]
    [InlineData("src/\0file.cs")]
    [InlineData("src/\nfile.cs")]
    public void NormalizeAndValidatePath_TraversalOrUnsafePaths_ThrowsGitBrowserInvalidInputException(string unsafePath)
    {
        Assert.Throws<GitBrowserInvalidInputException>(() => GitBrowserInputValidator.NormalizeAndValidatePath(unsafePath));
    }

    [Theory]
    [InlineData("feature/task-42", true, 42)]
    [InlineData("FEATURE/TASK-7", true, 7)]
    [InlineData("feature/task-42-extra", false, 0)]
    [InlineData("main", false, 0)]
    public void TryExtractTaskBranchId_MatchesExactTaskBranches(string refName, bool expectedMatch, int expectedId)
    {
        var actual = GitBrowserInputValidator.TryExtractTaskBranchId(refName, out var id);
        Assert.Equal(expectedMatch, actual);
        Assert.Equal(expectedId, id);
    }
}
