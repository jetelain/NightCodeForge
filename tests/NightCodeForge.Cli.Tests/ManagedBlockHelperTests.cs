namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Common;

public class ManagedBlockHelperTests
{
    [Fact]
    public void ApplyManagedBlock_WhenContentIsNull_CreatesManagedBlock()
    {
        var result = ManagedBlockHelper.ApplyManagedBlock(null, "bin/\nobj/");

        Assert.Contains(Constants.ManagedBlockHeader, result);
        Assert.Contains("bin/", result);
        Assert.Contains("obj/", result);
        Assert.Contains(Constants.ManagedBlockFooter, result);
    }

    [Fact]
    public void ApplyManagedBlock_WhenContentIsEmpty_CreatesManagedBlock()
    {
        var result = ManagedBlockHelper.ApplyManagedBlock("", "bin/\nobj/");

        Assert.Contains(Constants.ManagedBlockHeader, result);
        Assert.Contains("bin/", result);
        Assert.Contains(Constants.ManagedBlockFooter, result);
    }

    [Fact]
    public void ApplyManagedBlock_WhenExistingHasUserContent_AppendsBlockPreservingUserEntries()
    {
        var existing = "# User custom section\ncustom.log\nsecrets.json\n";
        var result = ManagedBlockHelper.ApplyManagedBlock(existing, "bin/\nobj/");

        Assert.StartsWith("# User custom section\ncustom.log\nsecrets.json", result);
        Assert.Contains(Constants.ManagedBlockHeader, result);
        Assert.Contains("bin/", result);
        Assert.Contains(Constants.ManagedBlockFooter, result);
    }

    [Fact]
    public void ApplyManagedBlock_IsIdempotent_RunningTwiceProducesIdenticalOutput()
    {
        var initial = "# Existing rules\n*.bak\n";
        var pass1 = ManagedBlockHelper.ApplyManagedBlock(initial, "bin/\nobj/");
        var pass2 = ManagedBlockHelper.ApplyManagedBlock(pass1, "bin/\nobj/");

        Assert.Equal(pass1, pass2);
    }

    [Fact]
    public void ApplyManagedBlock_PreservesUserContentAfterBlock()
    {
        var beforeBlock = "# Before block\nkeep_this.txt";
        var afterBlock = "# After block\nkeep_this_too.txt";

        var existing = $"{beforeBlock}\n\n{Constants.ManagedBlockHeader}\nold_content\n{Constants.ManagedBlockFooter}\n\n{afterBlock}\n";

        var result = ManagedBlockHelper.ApplyManagedBlock(existing, "new_content");

        Assert.Contains(beforeBlock, result);
        Assert.Contains("new_content", result);
        Assert.DoesNotContain("old_content", result);
        Assert.Contains(afterBlock, result);

        // And running again is idempotent
        var recheck = ManagedBlockHelper.ApplyManagedBlock(result, "new_content");
        Assert.Equal(result, recheck);
    }

    [Fact]
    public void ExtractManagedBlock_ReturnsInnerContent_WhenMarkersExist()
    {
        var text = $"prefix\n{Constants.ManagedBlockHeader}\nline1\nline2\n{Constants.ManagedBlockFooter}\nsuffix";
        var extracted = ManagedBlockHelper.ExtractManagedBlock(text);

        Assert.NotNull(extracted);
        Assert.Equal("line1\nline2", extracted);
    }

    [Fact]
    public void ExtractManagedBlock_ReturnsNull_WhenMarkersMissing()
    {
        var text = "no markers here";
        var extracted = ManagedBlockHelper.ExtractManagedBlock(text);

        Assert.Null(extracted);
    }
}
