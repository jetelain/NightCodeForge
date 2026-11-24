using NightCodeForge.Core.Domain;

namespace NightCodeForge.Infrastructure.Templates;

/// <summary>
/// Built-in catalog of native CLI workspace scaffolding templates and argument rendering helpers.
/// </summary>
public static class WorkspaceTemplateCatalog
{
    private const string DotNetExecutable = "dotnet";

    public static IReadOnlyList<WorkspaceTemplateDefinition> BuiltInTemplates { get; } =
    [
        new WorkspaceTemplateDefinition(
            Id: WorkspaceTemplateConstants.DotNetCleanWebAppId,
            Name: "Web Application (Clean Architecture)",
            Description: "Blazor Interactive Server web app separated into Web, Core, and Infrastructure layers with xUnit test suites.",
            IconEmoji: "🌐",
            StackCategory: TemplateStackCategory.DotNet,
            Archetype: TemplateArchitectureArchetype.WebApp,
            BootstrapCommands:
            [
                DotNet("new sln -n {ProjectName} --format sln"),
                DotNet("new blazor -n {ProjectName}.Web -o src/{ProjectName}.Web --interactivity Server --no-restore"),
                DotNet("new classlib -n {ProjectName}.Core -o src/{ProjectName}.Core --no-restore"),
                DotNet("new classlib -n {ProjectName}.Infrastructure -o src/{ProjectName}.Infrastructure --no-restore"),
                DotNet("add src/{ProjectName}.Web/{ProjectName}.Web.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj src/{ProjectName}.Infrastructure/{ProjectName}.Infrastructure.csproj"),
                DotNet("add src/{ProjectName}.Infrastructure/{ProjectName}.Infrastructure.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Core.Tests -o tests/{ProjectName}.Core.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Core.Tests/{ProjectName}.Core.Tests.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Web.Tests -o tests/{ProjectName}.Web.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Web.Tests/{ProjectName}.Web.Tests.csproj reference src/{ProjectName}.Web/{ProjectName}.Web.csproj"),
                DotNet("sln {ProjectName}.sln add --include-references false src/{ProjectName}.Web src/{ProjectName}.Core src/{ProjectName}.Infrastructure tests/{ProjectName}.Core.Tests tests/{ProjectName}.Web.Tests")
            ],
            GeneratedProjects:
            [
                "src/{ProjectName}.Web",
                "src/{ProjectName}.Core",
                "src/{ProjectName}.Infrastructure",
                "tests/{ProjectName}.Core.Tests",
                "tests/{ProjectName}.Web.Tests"
            ]),

        new WorkspaceTemplateDefinition(
            Id: WorkspaceTemplateConstants.DotNetCleanWebApiId,
            Name: "Web API (Clean Architecture)",
            Description: "REST / Minimal Web API separated into Api, Core, and Infrastructure layers with xUnit test suites.",
            IconEmoji: "🔌",
            StackCategory: TemplateStackCategory.DotNet,
            Archetype: TemplateArchitectureArchetype.WebApi,
            BootstrapCommands:
            [
                DotNet("new sln -n {ProjectName} --format sln"),
                DotNet("new webapi -n {ProjectName}.Api -o src/{ProjectName}.Api --no-restore"),
                DotNet("new classlib -n {ProjectName}.Core -o src/{ProjectName}.Core --no-restore"),
                DotNet("new classlib -n {ProjectName}.Infrastructure -o src/{ProjectName}.Infrastructure --no-restore"),
                DotNet("add src/{ProjectName}.Api/{ProjectName}.Api.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj src/{ProjectName}.Infrastructure/{ProjectName}.Infrastructure.csproj"),
                DotNet("add src/{ProjectName}.Infrastructure/{ProjectName}.Infrastructure.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Core.Tests -o tests/{ProjectName}.Core.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Core.Tests/{ProjectName}.Core.Tests.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Api.Tests -o tests/{ProjectName}.Api.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Api.Tests/{ProjectName}.Api.Tests.csproj reference src/{ProjectName}.Api/{ProjectName}.Api.csproj"),
                DotNet("sln {ProjectName}.sln add --include-references false src/{ProjectName}.Api src/{ProjectName}.Core src/{ProjectName}.Infrastructure tests/{ProjectName}.Core.Tests tests/{ProjectName}.Api.Tests")
            ],
            GeneratedProjects:
            [
                "src/{ProjectName}.Api",
                "src/{ProjectName}.Core",
                "src/{ProjectName}.Infrastructure",
                "tests/{ProjectName}.Core.Tests",
                "tests/{ProjectName}.Api.Tests"
            ]),

        new WorkspaceTemplateDefinition(
            Id: WorkspaceTemplateConstants.DotNetCleanCliId,
            Name: "CLI Application (Clean Architecture)",
            Description: "Command-line application structured into Cli and Core projects with dedicated unit test suites.",
            IconEmoji: "💻",
            StackCategory: TemplateStackCategory.DotNet,
            Archetype: TemplateArchitectureArchetype.CliTool,
            BootstrapCommands:
            [
                DotNet("new sln -n {ProjectName} --format sln"),
                DotNet("new console -n {ProjectName}.Cli -o src/{ProjectName}.Cli --no-restore"),
                DotNet("new classlib -n {ProjectName}.Core -o src/{ProjectName}.Core --no-restore"),
                DotNet("add src/{ProjectName}.Cli/{ProjectName}.Cli.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Core.Tests -o tests/{ProjectName}.Core.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Core.Tests/{ProjectName}.Core.Tests.csproj reference src/{ProjectName}.Core/{ProjectName}.Core.csproj"),
                DotNet("new xunit -n {ProjectName}.Cli.Tests -o tests/{ProjectName}.Cli.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Cli.Tests/{ProjectName}.Cli.Tests.csproj reference src/{ProjectName}.Cli/{ProjectName}.Cli.csproj"),
                DotNet("sln {ProjectName}.sln add --include-references false src/{ProjectName}.Cli src/{ProjectName}.Core tests/{ProjectName}.Core.Tests tests/{ProjectName}.Cli.Tests")
            ],
            GeneratedProjects:
            [
                "src/{ProjectName}.Cli",
                "src/{ProjectName}.Core",
                "tests/{ProjectName}.Core.Tests",
                "tests/{ProjectName}.Cli.Tests"
            ]),

        new WorkspaceTemplateDefinition(
            Id: WorkspaceTemplateConstants.DotNetClassLibId,
            Name: "Class Library",
            Description: "Modular reusable .NET class library with Clean Architecture layout, documentation, and xUnit tests.",
            IconEmoji: "📦",
            StackCategory: TemplateStackCategory.DotNet,
            Archetype: TemplateArchitectureArchetype.ClassLibrary,
            BootstrapCommands:
            [
                DotNet("new sln -n {ProjectName} --format sln"),
                DotNet("new classlib -n {ProjectName} -o src/{ProjectName} --no-restore"),
                DotNet("new xunit -n {ProjectName}.Tests -o tests/{ProjectName}.Tests --no-restore"),
                DotNet("add tests/{ProjectName}.Tests/{ProjectName}.Tests.csproj reference src/{ProjectName}/{ProjectName}.csproj"),
                DotNet("sln {ProjectName}.sln add --include-references false src/{ProjectName} tests/{ProjectName}.Tests")
            ],
            GeneratedProjects:
            [
                "src/{ProjectName}",
                "tests/{ProjectName}.Tests"
            ])
    ];

    /// <summary>
    /// Substitutes <c>{ProjectName}</c> in a template command and tokenizes its argument list.
    /// </summary>
    public static IReadOnlyList<string> RenderArguments(WorkspaceTemplateCommand command, string projectName)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var rendered = command.ArgumentsTemplate.Replace(
            WorkspaceTemplateConstants.ProjectNamePlaceholder,
            projectName,
            StringComparison.Ordinal);

        return rendered.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Resolves generated project paths by substituting <c>{ProjectName}</c>.
    /// </summary>
    public static IReadOnlyList<string> ResolveGeneratedProjects(WorkspaceTemplateDefinition template, string projectName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        return template.GeneratedProjects
            .Select(p => p.Replace(WorkspaceTemplateConstants.ProjectNamePlaceholder, projectName, StringComparison.Ordinal))
            .ToList();
    }

    private static WorkspaceTemplateCommand DotNet(string argumentsTemplate) =>
        new(DotNetExecutable, argumentsTemplate);
}
