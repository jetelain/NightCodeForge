using System.Globalization;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Parses and validates backlog task <see cref="MergeMode"/> values against workspace GitHub and security constraints.
/// </summary>
public interface IBacklogMergeModeValidator
{
    Task<MergeMode> ParseAndValidateAsync(int workspaceId, string rawMergeMode, string paramName, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BacklogMergeModeValidator : IBacklogMergeModeValidator
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGitHubPullRequestDeliveryService _pullRequestDeliveryService;
    private readonly IOptions<SecurityOptions> _securityOptions;

    public BacklogMergeModeValidator(
        IWorkspaceRepository workspaceRepository,
        IGitHubPullRequestDeliveryService pullRequestDeliveryService,
        IOptions<SecurityOptions> securityOptions)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pullRequestDeliveryService = pullRequestDeliveryService ?? throw new ArgumentNullException(nameof(pullRequestDeliveryService));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
    }

    public async Task<MergeMode> ParseAndValidateAsync(
        int workspaceId,
        string rawMergeMode,
        string paramName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawMergeMode);

        var normalizedMergeMode = rawMergeMode.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (int.TryParse(normalizedMergeMode, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !Enum.TryParse<MergeMode>(normalizedMergeMode, ignoreCase: true, out var parsedMergeMode) ||
            !Enum.IsDefined(parsedMergeMode))
        {
            throw new ArgumentException(
                $"Invalid mergeMode '{rawMergeMode}'. Expected one of: {string.Join(", ", Enum.GetNames<MergeMode>())}.",
                paramName);
        }

        if (parsedMergeMode == MergeMode.ExternalPullRequest)
        {
            if (_securityOptions.Value.AirGappedMode)
            {
                throw new InvalidOperationException("MergeMode 'ExternalPullRequest' is not permitted when Air-Gapped Mode (AirGappedMode) is enabled.");
            }

            var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
            if (workspace?.GitHubSettings is not { Enabled: true, PullRequestDeliveryEnabled: true })
            {
                throw new InvalidOperationException(
                    $"MergeMode 'ExternalPullRequest' requires GitHub integration and Pull Request Delivery to be enabled on workspace #{workspaceId} (Pull Request Delivery is disabled).");
            }

            if (!await _pullRequestDeliveryService.IsPullRequestDeliveryAvailableAsync(workspace, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"MergeMode 'ExternalPullRequest' requires a configured GitHub repository (RepositoryOwner/RepositoryName) and valid GitHub OAuth authentication on workspace #{workspaceId}.");
            }
        }

        return parsedMergeMode;
    }
}
