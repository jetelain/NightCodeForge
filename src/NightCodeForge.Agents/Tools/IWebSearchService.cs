namespace NightCodeForge.Agents.Tools;

public record WebSearchResultItem(string Title, string Url, string Content);

public record WebSearchResponse(IReadOnlyList<WebSearchResultItem> Results);

public interface IWebSearchService
{
    string ProviderName { get; }
    bool IsConfigured { get; }
    Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);
}

public class MockWebSearchService : IWebSearchService
{
    private readonly Func<string, int, Task<WebSearchResponse>>? _searchHandler;
    private readonly bool _isConfigured;

    public string ProviderName => "Mock";
    public bool IsConfigured => _isConfigured;

    public MockWebSearchService()
        : this(null, true)
    {
    }

    public MockWebSearchService(bool isConfigured)
        : this(null, isConfigured)
    {
    }

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>> searchHandler)
        : this(searchHandler, true)
    {
    }

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>>? searchHandler, bool isConfigured)
    {
        _searchHandler = searchHandler;
        _isConfigured = isConfigured;
    }

    public async Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
    {
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
