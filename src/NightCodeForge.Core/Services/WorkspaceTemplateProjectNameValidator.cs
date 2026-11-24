using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Result of validating a template project name or remote repository name.
/// </summary>
public record ProjectNameValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>
/// Validates filesystem-, solution-, and repository-safe project names for template scaffolding.
/// </summary>
public static partial class WorkspaceTemplateProjectNameValidator
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidProjectNamePattern();

    /// <summary>
    /// Validates a project name for native CLI scaffolding and solution naming.
    /// </summary>
    public static ProjectNameValidationResult Validate(string? projectName) =>
        ValidateIdentifier(projectName, "Project name");

    /// <summary>
    /// Validates a remote repository name for forge publishing.
    /// </summary>
    public static ProjectNameValidationResult ValidateRepositoryName(string? repositoryName) =>
        ValidateIdentifier(repositoryName, "Repository name");

    private static ProjectNameValidationResult ValidateIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ProjectNameValidationResult(false, $"{label} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > WorkspaceTemplateConstants.MaxProjectNameLength)
        {
            return new ProjectNameValidationResult(
                false,
                $"{label} must be at most {WorkspaceTemplateConstants.MaxProjectNameLength} characters.");
        }

        if (!ValidProjectNamePattern().IsMatch(trimmed))
        {
            return new ProjectNameValidationResult(
                false,
                $"{label} must start with a letter and contain only letters, digits, dots, underscores, or hyphens.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal) || trimmed.EndsWith('.'))
        {
            return new ProjectNameValidationResult(
                false,
                $"{label} cannot contain '..' or end with a dot.");
        }

        var dotIndex = trimmed.IndexOf('.');
        var firstSegment = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
        if (ReservedDeviceNames.Contains(trimmed) || ReservedDeviceNames.Contains(firstSegment))
        {
            return new ProjectNameValidationResult(
                false,
                $"{label} '{trimmed}' uses a reserved operating system device name.");
        }

        return new ProjectNameValidationResult(true, null);
    }
}
