using System.Net;
using System.Text;

namespace NightCodeForge.Infrastructure.Tests.Inference;

/// <summary>Routes requests to a delegate and records them; the first matching route wins.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath.TrimStart('/'), body));
        return _handler(request);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public static HttpClient Client(StubHttpHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost:11434/") };
}
