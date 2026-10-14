using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

public record CodeSymbol(
    string Kind,
    string Name,
    string RelativePath,
    int StartLine,
    int EndLine);

public interface IWorkspaceSymbolService
{
    Task<IReadOnlyList<CodeSymbol>> IndexWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    Task<string> GetMatchingSymbolsPromptAsync(string workspaceRoot, string taskText, LocalOptimizationFlags? flags = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic 0-GPU Roslyn symbol indexer that injects exact symbol locations into Stage 1 prompt.
/// </summary>
public partial class RoslynSymbolIndexProvider : IWorkspaceSymbolService
{
    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]{3,}\b")]
    private static partial Regex KeywordWordRegex();
    public async Task<IReadOnlyList<CodeSymbol>> IndexWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var symbols = new List<CodeSymbol>();
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return symbols;
        }

        var csFiles = Directory.GetFiles(workspaceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .ToList();

        foreach (var file in csFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var code = await File.ReadAllTextAsync(file, cancellationToken);
                var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);
                var relPath = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');

                var nodes = root.DescendantNodes();
                foreach (var node in nodes)
                {
                    if (node is TypeDeclarationSyntax typeDecl)
                    {
                        var lineSpan = tree.GetLineSpan(typeDecl.Span);
                        var kind = typeDecl switch
                        {
                            ClassDeclarationSyntax => "Class",
                            InterfaceDeclarationSyntax => "Interface",
                            RecordDeclarationSyntax => "Record",
                            StructDeclarationSyntax => "Struct",
                            _ => "Type"
                        };

                        symbols.Add(new CodeSymbol(
                            Kind: kind,
                            Name: typeDecl.Identifier.Text,
                            RelativePath: relPath,
                            StartLine: lineSpan.StartLinePosition.Line + 1,
                            EndLine: lineSpan.EndLinePosition.Line + 1
                        ));
                    }
                    else if (node is MethodDeclarationSyntax methodDecl && methodDecl.Parent is TypeDeclarationSyntax parentType)
                    {
                        var lineSpan = tree.GetLineSpan(methodDecl.Span);
                        symbols.Add(new CodeSymbol(
                            Kind: "Method",
                            Name: $"{parentType.Identifier.Text}.{methodDecl.Identifier.Text}",
                            RelativePath: relPath,
                            StartLine: lineSpan.StartLinePosition.Line + 1,
                            EndLine: lineSpan.EndLinePosition.Line + 1
                        ));
                    }
                }
            }
            catch
            {
                // Proceed with other files
            }
        }

        return symbols;
    }

    public async Task<string> GetMatchingSymbolsPromptAsync(string workspaceRoot, string taskText, LocalOptimizationFlags? flags = null, CancellationToken cancellationToken = default)
    {
        if (flags != null && !flags.DeterministicSymbolMap)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(taskText))
        {
            return string.Empty;
        }

        var allSymbols = await IndexWorkspaceAsync(workspaceRoot, cancellationToken);
        if (allSymbols.Count == 0)
        {
            return string.Empty;
        }

        // Extract keywords from task text
        var words = KeywordWordRegex().Matches(taskText)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = allSymbols
            .Where(s => words.Contains(s.Name) || words.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(15)
            .ToList();

        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<discovered_code_symbols>");
        sb.AppendLine("Deterministic host index resolved the following relevant code symbols in workspace:");
        foreach (var s in matches)
        {
            sb.AppendLine($"- {s.Kind} {s.Name}: {s.RelativePath} (Lines {s.StartLine}-{s.EndLine})");
        }
        sb.AppendLine("</discovered_code_symbols>");

        return sb.ToString().TrimEnd();
    }
}
