namespace NightCodeForge.Agents.Tools;

public record WebSearchResultItem(string Title, string Url, string Content);

public record WebSearchResponse(IReadOnlyList<WebSearchResultItem> Results);

public interface IWebSearchService
{
    string ProviderName { get; }
    Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);
}

public class MockWebSearchService : IWebSearchService
{
    private readonly Func<string, int, Task<WebSearchResponse>>? _searchHandler;

    public string ProviderName => "Mock";

    public MockWebSearchService(Func<string, int, Task<WebSearchResponse>>? searchHandler = null)
    {
        _searchHandler = searchHandler;
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
