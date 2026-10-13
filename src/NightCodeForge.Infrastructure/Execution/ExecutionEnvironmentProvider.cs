using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Resolves the appropriate execution environment (BareHost, WslDirect, or ContainerSandbox)
/// based on the workspace's configured ExecutionMode.
/// </summary>
public class ExecutionEnvironmentProvider : IExecutionEnvironmentProvider
{
    private readonly BareHostExecutionEnvironment _bareHost;
    private readonly ILogger<NightCodeForgeContainerExecutor> _containerLogger;
    private readonly ConcurrentDictionary<int, IExecutionEnvironment> _cachedEnvironments = new();

    public ExecutionEnvironmentProvider(
        BareHostExecutionEnvironment? bareHost = null,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null)
    {
        _bareHost = bareHost ?? new BareHostExecutionEnvironment();
        _containerLogger = containerLogger ?? NullLogger<NightCodeForgeContainerExecutor>.Instance;
    }

    public IExecutionEnvironment GetEnvironment(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return workspace.ExecutionMode switch
        {
            ExecutionMode.BareHost => _bareHost,

            ExecutionMode.WslDirect => _cachedEnvironments.GetOrAdd(workspace.Id, _ =>
                new WslExecutionEnvironment(
                    distribution: null,
                    defaultWorkingDirectory: !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                        ? workspace.Ext4MirrorPath
                        : workspace.RepositoryPath)),

            ExecutionMode.ContainerSandbox => _cachedEnvironments.GetOrAdd(workspace.Id, _ =>
            {
                var options = new ContainerExecutorOptions
                {
                    ContainerNameOrId = !string.IsNullOrWhiteSpace(workspace.ContainerImage)
                        ? workspace.ContainerImage
                        : "ncf-sandbox",
                    DefaultWorkingDirectory = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                        ? workspace.Ext4MirrorPath
                        : "/workspace"
                };
                return new NightCodeForgeContainerExecutor(options, _containerLogger);
            }),

            _ => _bareHost
        };
    }
}
