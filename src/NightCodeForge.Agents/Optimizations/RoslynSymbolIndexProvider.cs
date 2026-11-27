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
    private const int MaxDiscoveredSymbols = 15;
    private const int MinSubstringKeywordLength = 4;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "into", "onto", "upon", "over", "under", "about",
        "above", "below", "after", "before", "between", "during", "through", "without", "within",
        "against", "around", "because", "since", "while", "where", "when", "what", "which",
        "whose", "whom", "they", "them", "their", "there", "here", "these", "those", "then",
        "than", "thus", "also", "only", "just", "very", "more", "most", "some", "such", "many",
        "much", "each", "both", "same", "other", "another", "still", "even", "ever", "never",
        "always", "often", "have", "has", "had", "having", "been", "being", "were", "will",
        "would", "could", "should", "must", "shall", "might", "does", "doing", "done", "make",
        "makes", "made", "take", "takes", "taken", "give", "gives", "given", "need", "needs",
        "needed", "want", "wants", "work", "works", "working", "like", "used", "uses", "using",
        "please", "hello", "thanks", "issue", "problem", "expected", "actual", "currently",
        "instead", "however", "therefore", "example", "examples", "note", "notes", "following",
        "description", "summary", "details", "behavior", "behaviour", "change", "changes",
        "changed", "code", "file", "files", "line", "lines", "class", "classes", "method",
        "methods", "property", "properties", "field", "fields", "interface", "struct", "record",
        "enum", "namespace", "public", "private", "protected", "internal", "static", "readonly",
        "const", "virtual", "override", "abstract", "sealed", "async", "await", "return",
        "returns", "void", "null", "true", "false", "bool", "string", "object", "byte", "char",
        "short", "long", "float", "double", "decimal", "value", "values", "type", "types",
        "name", "names", "data", "item", "items", "list", "test", "tests", "testing", "unit",
        "bench", "benchmark", "benchmarks", "sample", "samples"
    };

    private static readonly HashSet<string> AuxiliaryPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bench", "benchmarks", "benchmark", "sample", "samples", "example", "examples", "demo", "demos", "sandbox"
    };

    private static readonly string[] AuxiliaryPathSuffixes = [".benchmarks", ".samples", ".examples"];

    private static readonly HashSet<string> TestPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs"
    };

    private static readonly string[] TestPathSuffixes = [".tests", ".test", ".unittests", ".integrationtests"];

    private static readonly HashSet<string> ProductionRootSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "lib", "source", "sources"
    };

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]{3,}\b")]
    private static partial Regex KeywordWordRegex();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_+", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierSubTokenSplitRegex();
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Proceed with other files when a single source file is transiently unreadable.
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

        // Extract meaningful keywords from task text
        var keywords = KeywordWordRegex().Matches(taskText)
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keywords.Count == 0)
        {
            return string.Empty;
        }

        var matches = allSymbols
            .Select(s =>
            {
                var relevance = ScoreSymbolRelevance(s, keywords, taskText);
                var (pathCategory, isExplicitSourceRoot) = ClassifyPathPriority(s.RelativePath);
                return (Symbol: s, Relevance: relevance, PathCategory: pathCategory, IsExplicitSourceRoot: isExplicitSourceRoot);
            })
            .Where(x => x.Relevance > 0)
            .OrderByDescending(x => x.PathCategory)
            .ThenByDescending(x => x.Relevance)
            .ThenByDescending(x => x.IsExplicitSourceRoot)
            .ThenBy(x => x.Symbol.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Symbol.StartLine)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDiscoveredSymbols)
            .Select(x => x.Symbol)
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

    internal static (int PathCategory, bool IsExplicitSourceRoot) ClassifyPathPriority(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var seg in segments)
        {
            if (AuxiliaryPathSegments.Contains(seg) ||
                AuxiliaryPathSuffixes.Any(suffix => seg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return (0, false);
            }
        }

        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        if (fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(seg =>
                TestPathSegments.Contains(seg) ||
                TestPathSuffixes.Any(suffix => seg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))))
        {
            return (1, false);
        }

        bool isExplicitSourceRoot = segments.Length > 0 && ProductionRootSegments.Contains(segments[0]);
        return (2, isExplicitSourceRoot);
    }

    internal static int ScoreSymbolRelevance(CodeSymbol symbol, IReadOnlySet<string> keywords, string taskText)
    {
        if (keywords.Count == 0 || string.IsNullOrWhiteSpace(symbol.Name))
        {
            return 0;
        }

        int score = 0;
        if (keywords.Contains(symbol.Name))
        {
            score += 100;
        }

        if (symbol.Name.Contains('.') && ContainsWholeQualifiedSymbol(taskText, symbol.Name))
        {
            score += 120;
        }

        var dotSegments = symbol.Name.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in dotSegments)
        {
            if (keywords.Contains(segment))
            {
                score += 80;
                continue;
            }

            var subTokens = IdentifierSubTokenSplitRegex().Split(segment)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool matchedSubToken = false;
            foreach (var keyword in keywords)
            {
                if (subTokens.Contains(keyword))
                {
                    score += 20;
                    matchedSubToken = true;
                }
                else if (keyword.Length >= MinSubstringKeywordLength &&
                         segment.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                    matchedSubToken = true;
                }
            }

            if (!matchedSubToken &&
                segment.Length >= MinSubstringKeywordLength &&
                !StopWords.Contains(segment) &&
                keywords.Any(k => k.Length >= MinSubstringKeywordLength &&
                                  k.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            {
                score += 5;
            }
        }

        return score;
    }

    private static bool ContainsWholeQualifiedSymbol(string taskText, string qualifiedName)
    {
        if (string.IsNullOrEmpty(taskText) || string.IsNullOrEmpty(qualifiedName))
        {
            return false;
        }

        int searchStart = 0;
        while (searchStart <= taskText.Length - qualifiedName.Length)
        {
            int idx = taskText.IndexOf(qualifiedName, searchStart, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool startBoundaryOk = idx == 0 || !IsIdentifierChar(taskText[idx - 1]);
            int endIdx = idx + qualifiedName.Length;
            bool endBoundaryOk = endIdx == taskText.Length || !IsIdentifierChar(taskText[endIdx]);

            if (startBoundaryOk && endBoundaryOk)
            {
                return true;
            }

            searchStart = idx + 1;
        }

        return false;
    }

    private static bool IsIdentifierChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
}
