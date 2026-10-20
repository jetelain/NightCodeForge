namespace NightCodeForge.Core.Domain;

/// <summary>
/// Domain constants for delimiter markers used to manage deterministic, tool-owned blocks
/// across repository configuration files (.gitignore, .editorconfig, skills, etc.).
/// </summary>
public static class ManagedBlockConstants
{
    public const string Header = "# >>> nightcodeforge (managed — do not edit between markers) >>>";
    public const string Footer = "# <<< nightcodeforge <<<";
}
