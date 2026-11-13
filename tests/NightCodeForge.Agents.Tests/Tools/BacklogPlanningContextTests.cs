using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tests;

public class BacklogPlanningContextTests
{
    [Fact]
    public void Constructor_ValidatesAllRequiredDependencies_AndExposesProperties()
    {
        var summary = new Mock<IWorkspaceBacklogSummaryService>().Object;
        var validator = new Mock<ITaskMarkdownValidator>().Object;
        var mutation = new Mock<IBacklogMutationToolService>().Object;
        var logger = NullLogger<CanonicalTools>.Instance;
        var scopeFactory = new Mock<IServiceScopeFactory>().Object;

        Assert.Throws<ArgumentNullException>(() =>
            new BacklogPlanningContext(null!, validator, mutation, logger, scopeFactory));
        Assert.Throws<ArgumentNullException>(() =>
            new BacklogPlanningContext(summary, null!, mutation, logger, scopeFactory));
        Assert.Throws<ArgumentNullException>(() =>
            new BacklogPlanningContext(summary, validator, null!, logger, scopeFactory));
        Assert.Throws<ArgumentNullException>(() =>
            new BacklogPlanningContext(summary, validator, mutation, null!, scopeFactory));
        Assert.Throws<ArgumentNullException>(() =>
            new BacklogPlanningContext(summary, validator, mutation, logger, null!));

        var context = new BacklogPlanningContext(summary, validator, mutation, logger, scopeFactory);
        Assert.Same(summary, context.SummaryService);
        Assert.Same(validator, context.TaskMarkdownValidator);
        Assert.Same(mutation, context.BacklogMutationService);
        Assert.Same(logger, context.CanonicalToolsLogger);
        Assert.Same(scopeFactory, context.ScopeFactory);
    }

    [Fact]
    public async Task BuildBacklogDigestAsync_ResolvesScopedSummaryServicePerCall_AndFailsFastWhenUnregistered()
    {
        var services = new ServiceCollection();
        int instancesCreated = 0;
        services.AddScoped<IWorkspaceBacklogSummaryService>(_ =>
        {
            var id = Interlocked.Increment(ref instancesCreated);
            var mock = new Mock<IWorkspaceBacklogSummaryService>();
            mock.Setup(s => s.BuildDigestAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int wsId, int? exId, CancellationToken _) => $"digest-{id}-ws{wsId}-ex{exId}");
            return mock.Object;
        });

        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var context = new BacklogPlanningContext(
            new Mock<IWorkspaceBacklogSummaryService>().Object,
            new Mock<ITaskMarkdownValidator>().Object,
            new Mock<IBacklogMutationToolService>().Object,
            NullLogger<CanonicalTools>.Instance,
            scopeFactory);

        var d1 = await context.BuildBacklogDigestAsync(42, 7, CancellationToken.None);
        var d2 = await context.BuildBacklogDigestAsync(42, null, CancellationToken.None);

        Assert.Equal("digest-1-ws42-ex7", d1);
        Assert.Equal("digest-2-ws42-ex", d2);
        Assert.Equal(2, instancesCreated);

        // Fails fast when IWorkspaceBacklogSummaryService is not registered in the scope
        await using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var emptyContext = new BacklogPlanningContext(
            new Mock<IWorkspaceBacklogSummaryService>().Object,
            new Mock<ITaskMarkdownValidator>().Object,
            new Mock<IBacklogMutationToolService>().Object,
            NullLogger<CanonicalTools>.Instance,
            emptyProvider.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            emptyContext.BuildBacklogDigestAsync(1, null, CancellationToken.None));
    }
}
