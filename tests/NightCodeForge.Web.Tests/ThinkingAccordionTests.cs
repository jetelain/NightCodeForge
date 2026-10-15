using Bunit;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ThinkingAccordionTests : BunitContext
{
    public ThinkingAccordionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ThinkingAccordion_WhenEmpty_RendersPlaceholder()
    {
        var cut = Render<ThinkingAccordion>();

        Assert.Contains("0 tokens", cut.Find(".think-token-pill").TextContent);
        Assert.Contains("No reasoning thoughts captured yet", cut.Markup);
    }

    [Fact]
    public void ThinkingAccordion_WhenStreamingTokens_AppendsTextAndIncrementsTokens()
    {
        var cut = Render<ThinkingAccordion>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.AppendToken(10, "Analyzing ", true);
            cut.Instance.AppendToken(10, "the code ", true);
            cut.Instance.AppendToken(10, "structure.", true);
        });

        Assert.Contains("Analyzing the code structure.", cut.Find(".think-accordion-content").TextContent);
        Assert.Contains("3 tokens", cut.Find(".think-token-pill").TextContent);
        Assert.Contains("STREAMING REASONING", cut.Markup);

        // Finish thinking
        cut.InvokeAsync(() =>
        {
            cut.Instance.AppendToken(10, "Here is the response", false);
        });

        Assert.DoesNotContain("STREAMING REASONING", cut.Markup);
    }

    [Fact]
    public void ThinkingAccordion_ClearButton_ResetsContent()
    {
        var cut = Render<ThinkingAccordion>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.AppendToken(10, "Some thoughts", true);
        });

        var clearBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Clear");
        clearBtn.Click();

        Assert.Contains("0 tokens", cut.Find(".think-token-pill").TextContent);
        Assert.Contains("No reasoning thoughts captured yet", cut.Markup);
    }

    [Fact]
    public void ThinkingAccordion_ToggleOpen_HidesAndShowsContent()
    {
        var cut = Render<ThinkingAccordion>();
        Assert.NotEmpty(cut.FindAll(".think-accordion-content"));

        var header = cut.Find(".think-accordion-summary");
        header.Click();

        Assert.Empty(cut.FindAll(".think-accordion-content"));

        header.Click();
        Assert.NotEmpty(cut.FindAll(".think-accordion-content"));
    }
}
