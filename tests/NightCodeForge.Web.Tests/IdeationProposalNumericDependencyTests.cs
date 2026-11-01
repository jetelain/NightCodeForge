using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationProposalNumericDependencyTests
{
    private static readonly IReadOnlyList<ForgeTask> Existing = new[]
    {
        new ForgeTask { Id = 101, Title = "Core LLM HTTP client abstractions" },
        new ForgeTask { Id = 102, Title = "Implement Ollama streaming response parser" }
    };

    [Theory]
    [InlineData("101")]
    [InlineData("#101")]
    [InlineData(" #101 ")]
    public void ResolveDependencies_SupportsNumericTaskIds(string reference)
    {
        var vm = new IdeationProposalViewModel { DependsOnTitles = new List<string> { reference } };

        vm.ResolveDependencies(Existing);

        Assert.Equal(new[] { 101 }, vm.SelectedDependencyIds);
    }

    [Fact]
    public void ResolveDependencies_StillMatchesTitles_AndAvoidsDuplicates()
    {
        var vm = new IdeationProposalViewModel
        {
            DependsOnTitles = new List<string> { "Implement Ollama streaming response parser", "#102" }
        };

        vm.ResolveDependencies(Existing);

        Assert.Equal(new[] { 102 }, vm.SelectedDependencyIds);
    }
}

