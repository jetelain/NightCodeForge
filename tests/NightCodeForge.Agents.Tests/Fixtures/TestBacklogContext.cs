using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tests.Fixtures;

/// <summary>Default <see cref="IBacklogPlanningContext"/> for tests: empty backlog and a pass-through validator.</summary>
public static class TestBacklogContext
{
    public static IBacklogPlanningContext CreateDefault()
    {
        var validator = new Mock<ITaskMarkdownValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskMarkdownValidationResult.Valid);
        var context = new Mock<IBacklogPlanningContext>();
        context.SetupGet(c => c.TaskMarkdownValidator).Returns(validator.Object);
        context.SetupGet(c => c.BacklogMutationService).Returns(new Mock<IBacklogMutationToolService>().Object);
        context.SetupGet(c => c.CanonicalToolsLogger).Returns(NullLogger<CanonicalTools>.Instance);
        context.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        return context.Object;
    }
}

