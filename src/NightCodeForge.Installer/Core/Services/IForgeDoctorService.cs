namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Runs 1-click Forge Doctor diagnostics covering WSL2, Docker, OllamaRouter endpoint,
/// NightCodeForge Web port 5270, companion NuGet tools, and SQLite database health.
/// </summary>
public interface IForgeDoctorService
{
    Task<ForgeDoctorReport> RunDiagnosticsAsync(
        string? installDirectory,
        string wslDistroName,
        string? routerBaseUrl,
        CancellationToken cancellationToken = default);
}
