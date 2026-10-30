namespace NightCodeForge.Core.Interfaces;

public record CompanionToolSpec(
    string ToolCommand,
    string PackageId,
    IReadOnlyList<string> ApplicableStacks);

public interface ICompanionToolInstaller
{
    IReadOnlyList<CompanionToolSpec> KnownTools { get; }
    Task<bool> IsToolInstalledAsync(string command, bool useWsl = false, string? wslDistribution = null, CancellationToken ct = default);
    Task EnsureToolsInstalledAsync(string stack, bool useWsl = false, string? wslDistribution = null, CancellationToken ct = default);
    void ClearCache();
}

