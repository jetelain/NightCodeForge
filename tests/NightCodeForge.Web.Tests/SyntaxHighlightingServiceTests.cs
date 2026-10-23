using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class SyntaxHighlightingServiceTests
{
    private readonly ISyntaxHighlightingService _highlighter = new SimpleSyntaxHighlighter();

    [Fact]
    public void CSharp_HighlightsKeywordsTypesAndComments()
    {
        var line = "public class Calculator : ICalculator // Main calc";
        var result = _highlighter.HighlightLine(line, "src/Calculator.cs");

        Assert.Contains("<span class=\"tok-keyword\">public</span>", result);
        Assert.Contains("<span class=\"tok-keyword\">class</span>", result);
        Assert.Contains("<span class=\"tok-type\">Calculator</span>", result);
        Assert.Contains("<span class=\"tok-comment\">// Main calc</span>", result);
    }

    [Fact]
    public void TypeScript_HighlightsConstAndString()
    {
        var line = "const message: string = \"Hello world\";";
        var result = _highlighter.HighlightLine(line, "app.ts");

        Assert.Contains("<span class=\"tok-keyword\">const</span>", result);
        Assert.Contains("<span class=\"tok-type\">string</span>", result);
        Assert.Contains("<span class=\"tok-string\">&quot;Hello world&quot;</span>", result);
    }

    [Fact]
    public void Python_HighlightsDefAndComment()
    {
        var line = "def calculate_sum(a, b): # Add numbers";
        var result = _highlighter.HighlightLine(line, "script.py");

        Assert.Contains("<span class=\"tok-keyword\">def</span>", result);
        Assert.Contains("<span class=\"tok-comment\"># Add numbers</span>", result);
    }

    [Fact]
    public void Json_HighlightsKeysAndStrings()
    {
        var line = "  \"name\": \"NightCodeForge\",";
        var result = _highlighter.HighlightLine(line, "package.json");

        Assert.Contains("<span class=\"tok-type\">&quot;name&quot;</span>", result);
        Assert.Contains("<span class=\"tok-string\">&quot;NightCodeForge&quot;</span>", result);
    }

    [Fact]
    public void HtmlEncodesSpecialCharactersForSafety()
    {
        var line = "if (x < 10 && y > 20) { return \"<script>alert(1)</script>\"; }";
        var result = _highlighter.HighlightLine(line, "test.cs");

        Assert.DoesNotContain("<script>", result);
        Assert.Contains("&lt;script&gt;", result);
        Assert.Contains("&amp;", result);
    }
}
