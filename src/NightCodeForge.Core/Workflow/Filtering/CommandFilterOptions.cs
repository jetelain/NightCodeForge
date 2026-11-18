namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Options controlling smart CLI output filtering behavior.
/// </summary>
public record CommandFilterOptions(
    int MaxWarnings = 5,
    bool RelativizePaths = true,
    bool SuppressWarningsOnErrors = true,
    bool StripSdkInternalLocations = true);
