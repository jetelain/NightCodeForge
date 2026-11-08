using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Agents.Tools;

public record WebSearchResultItem(string Title, string Url, string Content);

public record WebSearchResponse(IReadOnlyList<WebSearchResultItem> Results);

public static class WebSearchConstants
{
    public const string AirGappedDisabledReason =
        "Web search is disabled in Air-Gapped mode (external cloud search to ollama.com is prohibited).";

    public const string UnconfiguredDisabledReason =
        "Web search is disabled because no API key is configured.";

    public const string AirGappedToolErrorMessage =
        "Error: Web search is unavailable because the system is operating in Air-Gapped mode.";

    public const string UnconfiguredToolErrorMessage =
        "Web search is disabled because no API key is configured. Please solve the task using local repository inspection tools.";
}

public interface IWebSearchService
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    bool IsEnabled { get; }
    string? DisabledReason { get; }
    Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);
}

public class MockWebSearchService : IWebSearchService
{
    private readonly Func<string, int, Task<WebSearchResponse>>? _searchHandler;
    private readonly bool _isConfigured;
    private readonly bool _isEnabled;
    private readonly string? _disabledReason;

    public string ProviderName => "Mock";
    public bool IsConfigured => _isConfigured;
    public bool IsEnabled => _isEnabled;
    public string? DisabledReason => _disabledReason;

    public MockWebSearchService()
        : this(null, true, true, null)
    {
    }

    public MockWebSearchService(bool isConfigured)
        : this(null, isConfigured, isConfigured, isConfigured ? null : WebSearchConstants.UnconfiguredDisabledReason)
    {
    }

    public MockWebSearchService(bool isConfigured, bool isEnabled, string? disabledReason)
        : this(null, isConfigured, isEnabled, disabledReason)
    {
    }

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>> searchHandler)
        : this(searchHandler, true, true, null)
    {
    }

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>> searchHandler, bool isConfigured)
        : this(searchHandler, isConfigured, isConfigured, isConfigured ? null : WebSearchConstants.UnconfiguredDisabledReason)
    {
    }

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>>? searchHandler, bool isConfigured, bool isEnabled, string? disabledReason)
    {
        _searchHandler = searchHandler;
        _isConfigured = isConfigured;
        _isEnabled = isEnabled;
        _disabledReason = disabledReason;
    }

    public async Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isEnabled)
        {
            if ((_disabledReason != null && _disabledReason.Contains("Air-Gapped", StringComparison.OrdinalIgnoreCase)) || _isConfigured)
            {
                throw new AirGappedOperationException(_disabledReason ?? WebSearchConstants.AirGappedDisabledReason);
            }

            throw new InvalidOperationException(_disabledReason ?? WebSearchConstants.UnconfiguredDisabledReason);
        }

        if (!_isConfigured)
        {
            throw new InvalidOperationException(_disabledReason ?? WebSearchConstants.UnconfiguredDisabledReason);
        }

        if (_searchHandler != null)
        {
            return await _searchHandler(query, maxResults);
        }

        var results = new List<WebSearchResultItem>
        {
            new($"Documentation for {query}", $"https://docs.example.com/search?q={Uri.EscapeDataString(query)}", $"Summary of search results for query '{query}'.")
        };
        return new WebSearchResponse(results);
    }
}
