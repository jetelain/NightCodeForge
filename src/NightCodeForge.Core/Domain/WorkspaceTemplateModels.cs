namespace NightCodeForge.Core.Domain;

/// <summary>
/// Technology stack category for workspace scaffolding templates.
/// </summary>
public enum TemplateStackCategory
{
    DotNet = 1,
    TypeScriptNode = 2,
    Rust = 3,
    Python = 4
}

/// <summary>
/// Architectural archetype for workspace scaffolding templates.
/// </summary>
public enum TemplateArchitectureArchetype
{
    WebApp = 1,
    WebApi = 2,
    CliTool = 3,
    ClassLibrary = 4
}

/// <summary>
/// Represents a single native CLI command step in a workspace template recipe.
/// </summary>
public record WorkspaceTemplateCommand(
    string ToolExecutable,
    string ArgumentsTemplate,
    string? RelativeWorkingDirectory = null);

/// <summary>
/// Declarative recipe describing a from-scratch workspace template.
/// </summary>
public record WorkspaceTemplateDefinition(
    string Id,
    string Name,
    string Description,
    string IconEmoji,
    TemplateStackCategory StackCategory,
    TemplateArchitectureArchetype Archetype,
    IReadOnlyList<WorkspaceTemplateCommand> BootstrapCommands,
    IReadOnlyList<string> GeneratedProjects);

/// <summary>
/// Constants for workspace template scaffolding and repository hygiene initialization.
/// </summary>
public static class WorkspaceTemplateConstants
{
    public const string DotNetCleanWebAppId = "dotnet-clean-webapp";
    public const string DotNetCleanWebApiId = "dotnet-clean-webapi";
    public const string DotNetCleanCliId = "dotnet-clean-cli";
    public const string DotNetClassLibId = "dotnet-classlib";

    public const string ProjectNamePlaceholder = "{ProjectName}";
    public const string DefaultBranch = "main";
    public const string DefaultGitUserName = "NightCodeForge";
    public const string DefaultGitUserEmail = "forge@localhost";
    public const string GitTerminalPromptEnvVar = "GIT_TERMINAL_PROMPT";
    public const string GitTerminalPromptDisabledValue = "0";
    public const int DefaultCliCommandTimeoutSeconds = 120;
    public const string BaselineGitAttributesContent = "* text=auto eol=lf\n";
    public const int MaxProjectNameLength = 100;
}
