using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable tool module encapsulating technical web search (<c>web_search</c>) with <c>/tmp</c> markdown offload
/// and on-demand skill loading (<c>load_skill</c>).
/// Resolves <see cref="IWebSearchService"/> per invocation via <see cref="IServiceScopeFactory"/> when configured
/// so warm sessions never retain a disposed scoped or transient <c>HttpClient</c>.
/// </summary>
public sealed partial class ResearchAndSkillsToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    private IWebSearchService? _defaultWebSearchService;

    public IWebSearchService? WebSearchService => _defaultWebSearchService;
    public IWorkspaceSkillsService SkillsService { get; }
    public ISandboxPathResolver SandboxPathResolver { get; set; }
    public IServiceScopeFactory? ScopeFactory { get; set; }

    public bool IsWebSearchConfigured => WithWebSearchService(s => s.IsConfigured, fallback: false);
    public bool IsWebSearchEnabled => WithWebSearchService(s => s.IsEnabled, fallback: false);

    public ResearchAndSkillsToolModule(
        ISandboxPathResolver sandboxPathResolver,
        IWebSearchService webSearchService,
        IWorkspaceSkillsService skillsService)
    {
        SandboxPathResolver = sandboxPathResolver ?? throw new ArgumentNullException(nameof(sandboxPathResolver));
        _defaultWebSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        SkillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
    }

    public ResearchAndSkillsToolModule(
        ISandboxPathResolver sandboxPathResolver,
        IServiceScopeFactory scopeFactory,
        IWorkspaceSkillsService skillsService)
    {
        SandboxPathResolver = sandboxPathResolver ?? throw new ArgumentNullException(nameof(sandboxPathResolver));
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        SkillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
    }

    public void ConfigureWebSearch(IWebSearchService? defaultWebSearchService, IServiceScopeFactory? scopeFactory)
    {
        _defaultWebSearchService = defaultWebSearchService;
        if (scopeFactory != null)
        {
            ScopeFactory = scopeFactory;
        }
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this);

    public static IReadOnlyList<AIFunction> CreateFunctions(ResearchAndSkillsToolModule? module) =>
    [
        AIFunctionFactory.Create(
            ([Description("Exact name of the skill to load from the <skills> catalog")] string skill_name,
             CancellationToken ct = default) =>
                module != null
                    ? module.LoadSkillAsync(skill_name, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.LoadSkill,
            "Loads full instructions and reference documentation for a modular skill on demand."),

        AIFunctionFactory.Create(
            ([Description("Search query string")] string query,
             [Description("Maximum results to return (1-10)")] int? max_results = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.WebSearchAsync(query, max_results, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.WebSearch,
            "Searches technical web documentation for APIs, errors, or libraries. Large results are capped inline and saved in full under /tmp/web-search-*.md.")
    ];

    public async Task<string> LoadSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return "Error: skill_name parameter is required.";
        }

        return await SkillsService.LoadSkillAsync(skillName, cancellationToken);
    }

    public async Task<string> WebSearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (_defaultWebSearchService != null)
        {
            return await ExecuteWebSearchAsync(_defaultWebSearchService, query, maxResults, cancellationToken);
        }

        if (ScopeFactory != null)
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var scopedService = scope.ServiceProvider.GetRequiredService<IWebSearchService>();
            return await ExecuteWebSearchAsync(scopedService, query, maxResults, cancellationToken);
        }

        throw new InvalidOperationException("No web search service or scope factory is configured.");
    }

    private T WithWebSearchService<T>(Func<IWebSearchService, T> selector, T fallback)
    {
        if (_defaultWebSearchService != null)
        {
            return selector(_defaultWebSearchService);
        }

        if (ScopeFactory != null)
        {
            var scope = ScopeFactory.CreateAsyncScope();
            try
            {
                var scopedService = scope.ServiceProvider.GetService<IWebSearchService>();
                return scopedService != null ? selector(scopedService) : fallback;
            }
            finally
            {
                if (SynchronizationContext.Current != null)
                {
                    Task.Run(() => scope.DisposeAsync().AsTask()).GetAwaiter().GetResult();
                }
                else
                {
                    var disposeTask = scope.DisposeAsync();
                    if (disposeTask.IsCompletedSuccessfully)
                    {
                        disposeTask.GetAwaiter().GetResult();
                    }
                    else
                    {
                        disposeTask.AsTask().GetAwaiter().GetResult();
                    }
                }
            }
        }

        return fallback;
    }

    private async Task<string> ExecuteWebSearchAsync(
        IWebSearchService webSearchService,
        string query,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        if (!webSearchService.IsEnabled &&
            (webSearchService.IsConfigured || webSearchService.DisabledReason?.Contains("Air-Gapped", StringComparison.OrdinalIgnoreCase) == true))
        {
            return WebSearchConstants.AirGappedToolErrorMessage;
        }

        if (!webSearchService.IsConfigured)
        {
            return WebSearchConstants.UnconfiguredToolErrorMessage;
        }

        WebSearchResponse response;
        try
        {
            response = await webSearchService.SearchAsync(query, maxResults ?? WebSearchSettings.DefaultMaxResultsCount, cancellationToken);
        }
        catch (AirGappedOperationException)
        {
            return WebSearchConstants.AirGappedToolErrorMessage;
        }

        if (response.Results.Count == 0)
        {
            return $"No web search results found for: '{query}'.";
        }

        bool anyItemExceedsInlineCap = false;
        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for: '{query}' ({response.Results.Count} items):");
        for (int i = 0; i < response.Results.Count; i++)
        {
            var r = response.Results[i];
            var content = r.Content ?? string.Empty;
            if (content.Length > WorkflowDefaults.MaxWebSearchResultInlineChars)
            {
                anyItemExceedsInlineCap = true;
            }
            sb.AppendLine($"[{i + 1}] {r.Title} ({r.Url})");
            sb.AppendLine($"    {content}");
        }

        var standardOutput = sb.ToString().TrimEnd();
        if (!anyItemExceedsInlineCap && standardOutput.Length <= WorkflowDefaults.MaxWebSearchTotalInlineChars)
        {
            return standardOutput;
        }

        var fileLines = new List<string>
        {
            $"# Web Search Results for: '{query}' ({response.Results.Count} items)",
            string.Empty
        };

        var lineRanges = new (int startLine, int endLine)[response.Results.Count];
        int totalContentChars = 0;

        for (int i = 0; i < response.Results.Count; i++)
        {
            var r = response.Results[i];
            var content = r.Content ?? string.Empty;
            totalContentChars += content.Length;

            int startLine = fileLines.Count + 1;
            fileLines.Add($"## [{i + 1}] {r.Title} ({r.Url})");
            fileLines.Add(string.Empty);

            var contentLines = NewlineRegex().Split(content);
            fileLines.AddRange(contentLines);
            fileLines.Add(string.Empty);
            int endLine = fileLines.Count;
            lineRanges[i] = (startLine, endLine);
        }

        var fullMarkdown = string.Join("\n", fileLines);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var virtualTmpPath = $"/tmp/web-search-{timestamp}-{shortId}.md";

        bool savedToTmp = false;
        try
        {
            var fullPath = SandboxPathResolver.ResolveHostPath(virtualTmpPath, forWrite: false);
            var parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            await File.WriteAllTextAsync(fullPath, fullMarkdown, cancellationToken);
            savedToTmp = true;
        }
        catch (UnauthorizedAccessException)
        {
            // Justification: When running in a system scope where /tmp is not mounted, return inline-capped results without disk offload.
        }

        var footer = savedToTmp
            ? $"[Full untruncated web search results ({totalContentChars:N0} chars across {response.Results.Count} items) saved to '{virtualTmpPath}'. Use 'read' (with offset/limit) or 'bash' (e.g. grep/rg) on '{virtualTmpPath}' to inspect specific sections without flooding context.]"
            : $"[Web search results ({totalContentChars:N0} chars across {response.Results.Count} items) were truncated inline to fit context budget.]";

        var sbTrunc = new StringBuilder();
        sbTrunc.AppendLine($"Web search results for: '{query}' ({response.Results.Count} items):");

        for (int i = 0; i < response.Results.Count; i++)
        {
            var r = response.Results[i];
            var content = r.Content ?? string.Empty;
            var (startLine, endLine) = lineRanges[i];

            var header = savedToTmp
                ? $"[{i + 1}] {r.Title} ({r.Url}) [full content: {virtualTmpPath} lines {startLine}-{endLine}, {content.Length:N0} chars]"
                : $"[{i + 1}] {r.Title} ({r.Url}) [{content.Length:N0} chars]";

            string snippet = content.Length > WorkflowDefaults.MaxWebSearchResultInlineChars
                ? content[..WorkflowDefaults.MaxWebSearchResultInlineChars] + "... [truncated inline]"
                : content;

            var itemWithSnippet = $"{header}\n    {snippet}\n";
            var headerOnly = $"{header}\n";

            if (FitsInlineBudget(sbTrunc.Length, itemWithSnippet.Length, footer.Length))
            {
                sbTrunc.Append(itemWithSnippet);
            }
            else if (FitsInlineBudget(sbTrunc.Length, headerOnly.Length, footer.Length))
            {
                sbTrunc.Append(headerOnly);
            }
            else
            {
                var remainingBudget = WorkflowDefaults.MaxWebSearchTotalInlineChars - (sbTrunc.Length + footer.Length + 2);
                if (remainingBudget > 20 && headerOnly.Length > remainingBudget)
                {
                    sbTrunc.AppendLine(headerOnly[..(remainingBudget - 3)] + "...");
                }
                break;
            }
        }

        sbTrunc.AppendLine();
        sbTrunc.Append(footer);
        var finalResult = sbTrunc.ToString().TrimEnd();

        if (finalResult.Length > WorkflowDefaults.MaxWebSearchTotalInlineChars)
        {
            var allowed = WorkflowDefaults.MaxWebSearchTotalInlineChars - footer.Length - 4;
            if (allowed > 0 && finalResult.Length > allowed)
            {
                finalResult = finalResult[..allowed] + "\n\n" + footer;
            }
        }

        return finalResult;
    }

    private static bool FitsInlineBudget(int currentLength, int candidateLength, int footerLength) =>
        currentLength + candidateLength + footerLength + 2 <= WorkflowDefaults.MaxWebSearchTotalInlineChars;
}
