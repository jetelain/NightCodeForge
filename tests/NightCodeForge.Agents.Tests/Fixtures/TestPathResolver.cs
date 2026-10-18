namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

public static class TestPathResolver
{
    public static WorkspacePathResolver Create() =>
        new(new Mock<IWslMetadataService>().Object, Options.Create(new ExecutionEnvironmentProviderOptions()));

    public static WorkspacePathResolver Create(bool isWindowsOverride) =>
        new(new Mock<IWslMetadataService>().Object, Options.Create(new ExecutionEnvironmentProviderOptions()), isWindowsOverride);
}
