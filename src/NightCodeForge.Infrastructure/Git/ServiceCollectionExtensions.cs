using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Service registration extensions for Git infrastructure components.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Git CLI runner, workspace manager, and Ext4 mirror synchronization services.
    /// </summary>
    public static IServiceCollection AddGitInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IGitCliRunner, GitCliRunner>();
        services.TryAddScoped<IGitWorkspaceManager, GitWorkspaceManager>();
        services.TryAddScoped<IGitMirrorSyncService, GitMirrorSyncService>();
        return services;
    }
}
