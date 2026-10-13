using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Tests;

public class DependencyEngineTests
{
    [Fact]
    public void HasCycle_ReturnsFalse_WhenAcyclic()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        var dependencies = new List<(int IssueId, int DependsOnIssueId)>
        {
            (2, 1),
            (3, 2),
            (4, 2)
        };

        var hasCycle = engine.HasCycle(1, dependencies);
        Assert.False(hasCycle);
    }

    [Fact]
    public void HasCycle_ReturnsTrue_AndThrows_WhenCycleDetected()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        var dependencies = new List<(int IssueId, int DependsOnIssueId)>
        {
            (1, 2),
            (2, 3),
            (3, 1)
        };

        Assert.True(engine.HasCycle(1, dependencies));
        Assert.Throws<CircularDependencyException>(() => engine.ValidateNoCycles(1, dependencies));
    }

    [Fact]
    public void GetTopologicalOrder_ResolvesCorrectExecutionSequence()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        // Issue 3 depends on 2; Issue 2 depends on 1
        var dependencies = new List<(int IssueId, int DependsOnIssueId)>
        {
            (3, 2),
            (2, 1)
        };

        var order = engine.GetTopologicalOrder(dependencies);
        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void GetTopologicalOrder_ResolvesDiamondGraph()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        // 2 depends on 1; 3 depends on 1; 4 depends on 2 and 3
        var dependencies = new List<(int IssueId, int DependsOnIssueId)>
        {
            (2, 1),
            (3, 1),
            (4, 2),
            (4, 3)
        };

        var order = engine.GetTopologicalOrder(dependencies);
        Assert.Equal(1, order[0]);
        Assert.Contains(2, order.Take(3));
        Assert.Contains(3, order.Take(3));
        Assert.Equal(4, order[3]);
    }

    [Fact]
    public async Task IsReadyForExecution_ReturnsExpectedReadiness()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        store.AddIssue(10, 1, IssueOverallStatus.Open);
        store.AddIssue(20, 1, IssueOverallStatus.Open);
        store.AddDependency(1, 20, 10);

        // 10 has no dependencies, should be ready
        Assert.True(engine.IsReadyForExecution(10));

        // 20 depends on 10 which is Open (not closed) -> not ready
        Assert.False(engine.IsReadyForExecution(20));

        // Close 10
        await store.UpdateIssueStatusAsync(10, IssueOverallStatus.Closed);

        // 20 should now be ready
        Assert.True(engine.IsReadyForExecution(20));
    }

    [Fact]
    public async Task ResolveCascadeUnblockedIssuesAsync_UnblocksDependentWhenAllPrerequisitesComplete()
    {
        var store = new InMemoryIssueDependencyStore();
        var engine = new DependencyResolutionEngine(store);

        // Issue 30 depends on 10 and 20
        store.AddIssue(10, 1, IssueOverallStatus.Closed);
        store.AddIssue(20, 1, IssueOverallStatus.Open);
        store.AddIssue(30, 1, IssueOverallStatus.Open);

        store.AddDependency(1, 30, 10);
        store.AddDependency(1, 30, 20);

        // Closing 10 alone should not unblock 30 because 20 is still Open
        var unblocked = await engine.ResolveCascadeUnblockedIssuesAsync(10);
        Assert.Empty(unblocked);

        // Close 20
        await store.UpdateIssueStatusAsync(20, IssueOverallStatus.Closed);
        unblocked = await engine.ResolveCascadeUnblockedIssuesAsync(20);

        Assert.Single(unblocked);
        Assert.Equal(30, unblocked[0]);

        var status30 = await store.GetIssueStatusAsync(30);
        Assert.Equal(IssueOverallStatus.Open, status30);
    }
}
