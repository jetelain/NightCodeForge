using NightCodeForge.Agents.Tools.EditorConfig;

namespace NightCodeForge.Agents.Tests;

public class EditorConfigFileApplierTests
{
    private readonly EditorConfigFileApplier _applier = new();

    [Fact]
    public void ApplyFormatting_EnforcesEndOfLine_LfAndCrLf()
    {
        var input = "line1\r\nline2\r\nline3";
        var lfSettings = new EditorConfigSettings(EndOfLine: EndOfLine.Lf);
        var formattedLf = _applier.ApplyFormatting(input, lfSettings);

        Assert.Equal("line1\nline2\nline3\n", formattedLf);

        var crlfSettings = new EditorConfigSettings(EndOfLine: EndOfLine.CrLf);
        var formattedCrLf = _applier.ApplyFormatting("line1\nline2\nline3", crlfSettings);

        Assert.Equal("line1\r\nline2\r\nline3\r\n", formattedCrLf);
    }

    [Fact]
    public void ApplyFormatting_ConvertsSpacesToTabs_WhenIndentStyleIsTab()
    {
        var input = "    public void Method()\n    {\n        int x = 1;\n    }";
        var settings = new EditorConfigSettings(IndentStyle: IndentStyle.Tab, IndentSize: 4, EndOfLine: EndOfLine.Lf);
        var formatted = _applier.ApplyFormatting(input, settings);

        Assert.Contains("\tpublic void Method()", formatted);
        Assert.Contains("\t\tint x = 1;", formatted);
    }

    [Fact]
    public void ApplyFormatting_ConvertsTabsToSpaces_WhenIndentStyleIsSpace()
    {
        var input = "\tpublic void Method()\n\t{\n\t\tint x = 1;\n\t}";
        var settings = new EditorConfigSettings(IndentStyle: IndentStyle.Space, IndentSize: 4, EndOfLine: EndOfLine.Lf);
        var formatted = _applier.ApplyFormatting(input, settings);

        Assert.Contains("    public void Method()", formatted);
        Assert.Contains("        int x = 1;", formatted);
    }

    [Fact]
    public void ApplyFormatting_TrimsTrailingWhitespace_WhenEnabled()
    {
        var input = "line1   \nline2\t\t\nline3";
        var settings = new EditorConfigSettings(TrimTrailingWhitespace: true, EndOfLine: EndOfLine.Lf);
        var formatted = _applier.ApplyFormatting(input, settings);

        Assert.Equal("line1\nline2\nline3\n", formatted);
    }

    [Fact]
    public void ApplyFormatting_InsertsFinalNewline_WhenTrue()
    {
        var input = "line1\nline2";
        var settings = new EditorConfigSettings(InsertFinalNewline: true, EndOfLine: EndOfLine.Lf);
        var formatted = _applier.ApplyFormatting(input, settings);

        Assert.EndsWith("\n", formatted);
    }

    [Fact]
    public void ApplyFormatting_RemovesFinalNewline_WhenFalse()
    {
        var input = "line1\nline2\n\n\n";
        var settings = new EditorConfigSettings(InsertFinalNewline: false, EndOfLine: EndOfLine.Lf);
        var formatted = _applier.ApplyFormatting(input, settings);

        Assert.Equal("line1\nline2", formatted);
    }

    [Fact]
    public void ResolveTargetEol_AutoDetectsDominantEol_WhenUnconfigured()
    {
        var crlfSample = "line1\r\nline2\r\nline3";
        var detectedCrlf = EditorConfigFileApplier.ResolveTargetEol(null, crlfSample);
        Assert.Equal("\r\n", detectedCrlf);

        var lfSample = "line1\nline2\nline3";
        var detectedLf = EditorConfigFileApplier.ResolveTargetEol(null, lfSample);
        Assert.Equal("\n", detectedLf);
    }

    [Fact]
    public void ResolveTargetEol_WslUncPath_ForcesLf_EvenIfContentIsCrlf()
    {
        var crlfSample = "line1\r\nline2\r\nline3\r\n";
        var wslUncPath = @"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge\wks\1\clone\file.cs";
        var eol = EditorConfigFileApplier.ResolveTargetEol(EndOfLine.CrLf, crlfSample, wslUncPath);

        Assert.Equal("\n", eol);
    }

    [Fact]
    public void ResolveTargetEol_WslDollarUncPath_ForcesLf()
    {
        var crlfSample = "line1\r\nline2\r\nline3\r\n";
        var wslDollarPath = @"\\wsl$\Ubuntu\home\user\repo\file.cs";
        var eol = EditorConfigFileApplier.ResolveTargetEol(null, crlfSample, wslDollarPath);

        Assert.Equal("\n", eol);
    }

    [Fact]
    public void ResolveTargetEol_PosixWslPath_ForcesLf()
    {
        var crlfSample = "line1\r\nline2\r\nline3\r\n";
        var posixPath = "/home/user/.nightcodeforge/wks/42/task-1/app.cs";
        var eol = EditorConfigFileApplier.ResolveTargetEol(null, crlfSample, posixPath);

        Assert.Equal("\n", eol);
    }

    [Fact]
    public void ResolveTargetEol_WindowsWorkspaceMirror_DefaultsToLf_WhenUnconfigured()
    {
        var crlfSample = "line1\r\nline2\r\nline3\r\n";
        var mirrorPath = @"C:\Users\User\.nightcodeforge\wks\1\task-2\app.cs";
        var eol = EditorConfigFileApplier.ResolveTargetEol(null, crlfSample, mirrorPath);

        Assert.Equal("\n", eol);
    }

    [Fact]
    public void ApplyFormatting_WithWslTargetPath_EnforcesLfFormatting()
    {
        var crlfContent = "class Foo\r\n{\r\n    void Bar() {}\r\n}";
        var wslPath = @"\\wsl.localhost\Ubuntu\home\user\project\Foo.cs";
        var settings = new EditorConfigSettings(); // Unset EOL
        var formatted = _applier.ApplyFormatting(crlfContent, settings, existingContent: crlfContent, targetPath: wslPath);

        Assert.DoesNotContain("\r\n", formatted);
        Assert.Contains("\n", formatted);
    }
}
