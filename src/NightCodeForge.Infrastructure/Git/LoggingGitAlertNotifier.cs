using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Default implementation of IGitAlertNotifier logging warnings to standard application logger.
/// </summary>
public class LoggingGitAlertNotifier : IGitAlertNotifier
{
    private readonly ILogger<LoggingGitAlertNotifier> _logger;

    public LoggingGitAlertNotifier(ILogger<LoggingGitAlertNotifier>? logger = null)
    {
        _logger = logger ?? NullLogger<LoggingGitAlertNotifier>.Instance;
    }

    public Task NotifyAlertAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[Git Alert] {Title}: {Message}", title, message);
        return Task.CompletedTask;
    }
}
