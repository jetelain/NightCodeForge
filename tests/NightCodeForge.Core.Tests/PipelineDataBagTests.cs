using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class PipelineDataBagTests
{
    private record SamplePlan(string Spec, List<string> FilesToTouch);

    [Fact]
    public void SetAndGet_StoresAndRetrievesTypedValues()
    {
        var bag = new PipelineDataBag();
        bag.Set("line_coverage", 0.945);
        bag.Set("test_count", 42);
        bag.Set("plan", new SamplePlan("Fix bug", new List<string> { "src/A.cs", "tests/A_Test.cs" }));

        Assert.Equal(0.945, bag.Get<double>("line_coverage"));
        Assert.Equal(42, bag.Get<int>("test_count"));

        var plan = bag.Get<SamplePlan>("plan");
        Assert.NotNull(plan);
        Assert.Equal("Fix bug", plan.Spec);
        Assert.Equal(2, plan.FilesToTouch.Count);
    }

    [Fact]
    public void TryGet_HandlesExistingAndMissingKeys()
    {
        var bag = new PipelineDataBag();
        bag.Set("status_flag", true);

        Assert.True(bag.TryGet<bool>("status_flag", out var value));
        Assert.True(value);

        Assert.False(bag.TryGet<bool>("non_existent_key", out var missingVal));
        Assert.False(missingVal);
    }

    [Fact]
    public void Serialization_RoundTripMaintainsIntegrity()
    {
        var bag = new PipelineDataBag();
        bag.Set("score", 98.7);
        bag.Set("tags", new[] { "fast", "opt" });

        var json = bag.ToJson();
        var reloaded = PipelineDataBag.FromJson(json);

        Assert.Equal(98.7, reloaded.Get<double>("score"));
        var tags = reloaded.Get<string[]>("tags");
        Assert.NotNull(tags);
        Assert.Equal(new[] { "fast", "opt" }, tags);
    }

    [Fact]
    public void ContextSync_PersistsToTaskAndSurvivesReload()
    {
        var task = new ForgeTask
        {
            Id = 1,
            WorkspaceId = 10,
            Title = "Test ForgeTask"
        };
        var workspace = new Workspace { Id = 10, Name = "MainWS" };

        var context1 = new PipelineContext(task, workspace);
        context1.DataBag.Set("warnings.baseline", 5);
        context1.SyncDataBagToTask();

        Assert.NotNull(task.PipelineDataJson);
        Assert.Contains("warnings.baseline", task.PipelineDataJson);

        // Simulate reload from task entity
        var context2 = new PipelineContext(task, workspace);
        Assert.True(context2.DataBag.TryGet<int>("warnings.baseline", out var baseline));
        Assert.Equal(5, baseline);
    }
}
