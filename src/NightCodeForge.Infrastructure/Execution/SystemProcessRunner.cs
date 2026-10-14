using Microsoft.Extensions.Logging;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Infrastructure adapter and subclass for SystemProcessRunner.
/// </summary>
public class SystemProcessRunner : NightCodeForge.Core.Execution.SystemProcessRunner
{
    public SystemProcessRunner(ILogger<SystemProcessRunner>? logger = null)
        : base(logger)
    {
    }
}
