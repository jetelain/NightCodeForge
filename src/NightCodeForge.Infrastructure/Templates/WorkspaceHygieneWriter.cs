using System.Text;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Infrastructure.Templates;

/// <summary>
/// Writes canonical NightCodeForge repository hygiene assets (<c>docs/</c>, <c>.gitignore</c>,
/// <c>.editorconfig</c>, <c>.gitattributes</c>, <c>README.md</c>) with deterministic LF line endings.
/// </summary>
public static class WorkspaceHygieneWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const string BaseDotNetGitIgnore =
@"# Build results
[Dd]ebug/
[Rr]elease/
x64/
x86/
[Ww][Ii][Nn]32/
[Aa][Rr][Mm]/
[Aa][Rr][Mm]64/
bld/
[Bb]in/
[Oo]bj/
[Ll]og/
[Ll]ogs/

# User-specific and IDE files
*.rsuser
*.suo
*.user
*.userosscache
*.sln.docstates
.idea/
.vscode/*
!.vscode/settings.json
!.vscode/tasks.json
!.vscode/launch.json
!.vscode/extensions.json
*.log

# NuGet & packages
*.nupkg
*.snupkg
**/[Pp]ackages/*
!**/[Pp]ackages/build/
project.lock.json
project.fragment.lock.json
artifacts/";

    /// <summary>
    /// Writes baseline architecture docs, task sync directory placeholder, and repository hygiene files.
    /// </summary>
    public static async Task WriteAsync(
        string targetDirectoryPath,
        string projectName,
        WorkspaceTemplateDefinition template,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(template);

        var docsDir = Path.Combine(targetDirectoryPath, TaskSyncConstants.DocsSegment);
        var docsTasksDir = Path.Combine(docsDir, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(docsTasksDir);

        var resolvedProjects = WorkspaceTemplateCatalog.ResolveGeneratedProjects(template, projectName);

        var architectureContent = BuildArchitectureMarkdown(projectName, template, resolvedProjects);
        var gitignoreContent = GitignorePreparer.Prepare(BaseDotNetGitIgnore);
        var editorConfigContent = EditorConfigPreparer.Prepare(null);
        var gitAttributesContent = WorkspaceTemplateConstants.BaselineGitAttributesContent;
        var readmeContent = BuildReadmeMarkdown(projectName, template, resolvedProjects);

        await WriteLfTextAsync(Path.Combine(docsDir, "ARCHITECTURE.md"), architectureContent, cancellationToken).ConfigureAwait(false);
        await WriteLfTextAsync(Path.Combine(docsTasksDir, ".gitkeep"), string.Empty, cancellationToken).ConfigureAwait(false);
        await WriteLfTextAsync(Path.Combine(targetDirectoryPath, ".gitignore"), gitignoreContent, cancellationToken).ConfigureAwait(false);
        await WriteLfTextAsync(Path.Combine(targetDirectoryPath, ".editorconfig"), editorConfigContent, cancellationToken).ConfigureAwait(false);
        await WriteLfTextAsync(Path.Combine(targetDirectoryPath, ".gitattributes"), gitAttributesContent, cancellationToken).ConfigureAwait(false);
        await WriteLfTextAsync(Path.Combine(targetDirectoryPath, "README.md"), readmeContent, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildArchitectureMarkdown(
        string projectName,
        WorkspaceTemplateDefinition template,
        IReadOnlyList<string> resolvedProjects)
    {
        var projectBullets = string.Join("\n", resolvedProjects.Select(p => $"- `{p}`"));
        return $"""
            # {projectName} — Architecture Overview

            Generated from NightCodeForge template: **{template.Name}** (`{template.Id}`).

            ## Repository Layout

            - `docs/` — Architectural specifications and `docs/tasks/` autonomous task markdown files.
            - `src/` — Application and Clean Architecture source layers (Presentation/Entry, Core domain/interfaces, Infrastructure implementations).
            - `tests/` — Automated unit and integration test suites mirroring `src/`.

            ## Projects

            {projectBullets}

            ## Dependency Direction

            1. `Core` holds domain entities, interfaces, and pure domain logic with zero infrastructure dependencies.
            2. `Infrastructure` implements `Core` interfaces (persistence, external integrations, process/file adapters).
            3. Entry/Presentation (`Web`, `Api`, or `Cli`) coordinates user/HTTP interaction and depends on `Core` (and `Infrastructure` at composition root).
            """;
    }

    private static string BuildReadmeMarkdown(
        string projectName,
        WorkspaceTemplateDefinition template,
        IReadOnlyList<string> resolvedProjects)
    {
        var projectBullets = string.Join("\n", resolvedProjects.Select(p => $"- `{p}`"));
        return $"""
            # {projectName}

            {template.Description}

            Scaffolded with NightCodeForge workspace scaffolding using the **{template.Name}** (`{template.Id}`) template.

            ## Structure

            - `docs/ARCHITECTURE.md` — Layered architecture overview
            - `docs/tasks/` — Task specifications synchronized with NightCodeForge
            - `{projectName}.sln` — Solution file

            ### Generated Projects

            {projectBullets}

            ## Quick Start

            ```bash
            dotnet restore
            dotnet build {projectName}.sln
            dotnet test {projectName}.sln
            ```
            """;
    }

    private static Task WriteLfTextAsync(string filePath, string content, CancellationToken cancellationToken)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Length > 0 && !normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        return File.WriteAllTextAsync(filePath, normalized, Utf8NoBom, cancellationToken);
    }
}
