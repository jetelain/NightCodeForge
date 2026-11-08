using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightCodeForge.Infrastructure.Configuration;

namespace NightCodeForge.Web.Tailscale;

/// <summary>
/// Persists Tailscale configuration into <c>config/config.json</c> using atomic temp-file replacement
/// while preserving other root sections (e.g. LlmProvider, Security).
/// </summary>
public interface ITailscaleConfigurationWriter
{
    Task SaveTailscaleOptionsAsync(TailscaleOptions options, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITailscaleConfigurationWriter"/>
public class TailscaleConfigurationWriter : ITailscaleConfigurationWriter
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<TailscaleConfigurationWriter> _logger;

    public TailscaleConfigurationWriter(
        IHostEnvironment hostEnvironment,
        ILogger<TailscaleConfigurationWriter> logger)
    {
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SaveTailscaleOptionsAsync(TailscaleOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configFilePath = await UserConfigJsonFile.UpdateAsync(
            _hostEnvironment.ContentRootPath,
            _logger,
            root =>
            {
                UserConfigJsonFile.FindAndNormalizeSection(root, TailscaleOptions.SectionName);

                var tailscaleNode = JsonSerializer.SerializeToNode(options, UserConfigJsonFile.JsonOptions) as JsonObject;
                if (tailscaleNode != null)
                {
                    root[TailscaleOptions.SectionName] = tailscaleNode;
                }
            },
            cancellationToken);

        _logger.LogInformation("Updated Tailscale configuration saved to {Path}", configFilePath);
    }
}
