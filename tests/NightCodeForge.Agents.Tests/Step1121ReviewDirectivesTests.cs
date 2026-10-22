using NightCodeForge.Agents.Crucible;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class Step1121ReviewDirectivesTests
{
    [Fact]
    public void BuildReviewDirective_ContainsAutomatedTestInstruction()
    {
        var directive = CrucibleAgentHarness.BuildReviewDirective();

        Assert.Contains("For every defect you find, instruct the implementation agent to author a corresponding automated test verifying the fix.", directive);
    }

    [Fact]
    public void BuildImplementationDirective_IsConfigured()
    {
        var directive = CrucibleAgentHarness.BuildImplementationDirective();

        Assert.Contains("Senior Software Engineer & TDD Practitioner", directive);
        Assert.Contains("Implement clean, robust code according to the task specification", directive);
    }

    [Fact]
    public void BuildImplementationLoopbackNotice_ContainsTestMandate()
    {
        var notice = CrucibleAgentHarness.BuildImplementationLoopbackNotice();

        Assert.Contains("The Reviewer requested changes.", notice);
        Assert.Contains("You are required to implement a dedicated automated test for every bug or defect identified in the reviewer's feedback before completing your implementation.", notice);
    }
}
