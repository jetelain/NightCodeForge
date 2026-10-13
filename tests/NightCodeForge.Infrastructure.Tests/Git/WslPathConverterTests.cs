using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class WslPathConverterTests
{
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge", true)]
    [InlineData(@"\\wsl$\Ubuntu\home\user\.nightcodeforge", true)]
    [InlineData(@"//wsl.localhost/Ubuntu/home", true)]
    [InlineData(@"//wsl$/Ubuntu/home", true)]
    [InlineData(@"C:\Users\Julien\repo", false)]
    [InlineData(@"/mnt/c/Users/Julien/repo", false)]
    public void IsWslUncPath_DetectsUncPathsCorrectly(string path, bool expected)
    {
        var result = WslPathConverter.IsWslUncPath(path);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryExtractWslUnc_ExtractsDistroAndLinuxPath()
    {
        const string unc = @"\\wsl.localhost\Ubuntu\home\julien\.nightcodeforge\wks\1";
        var success = WslPathConverter.TryExtractWslUnc(unc, out var distro, out var linuxPath);

        Assert.True(success);
        Assert.Equal("Ubuntu", distro);
        Assert.Equal("/home/julien/.nightcodeforge/wks/1", linuxPath);
    }

    [Fact]
    public void TryExtractWslUnc_WithWslDollarSign_ExtractsDistroAndLinuxPath()
    {
        const string unc = @"\\wsl$\Debian\var\workspace";
        var success = WslPathConverter.TryExtractWslUnc(unc, out var distro, out var linuxPath);

        Assert.True(success);
        Assert.Equal("Debian", distro);
        Assert.Equal("/var/workspace", linuxPath);
    }

    [Fact]
    public void ToWslUncPath_FormatsProperUncString()
    {
        const string linuxPath = "/home/julien/.nightcodeforge/wks/42";
        var unc = WslPathConverter.ToWslUncPath(linuxPath, "Ubuntu");

        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\julien\.nightcodeforge\wks\42", unc);
    }
}
