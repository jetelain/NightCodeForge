using NightCodeForge.Core.Security;

namespace NightCodeForge.Core.Tests.Security;

public class ApiKeyEndpointNormalizerTests
{
    [Theory]
    [InlineData("  HTTPS://API.OpenAI.COM:443/v1/  ", "https://api.openai.com/v1")]
    [InlineData("HTTP://LOCALHOST:80/v1///", "http://localhost/v1")]
    [InlineData("http://localhost:1234/v1/", "http://localhost:1234/v1")]
    [InlineData("https://OpenRouter.ai/api/v1?foo=bar#frag", "https://openrouter.ai/api/v1")]
    [InlineData("https://user:pass@api.deepseek.com/V1/Custom/", "https://api.deepseek.com/V1/Custom")]
    [InlineData("https://api.example.com/", "https://api.example.com")]
    [InlineData("https://api.example.com", "https://api.example.com")]
    [InlineData("http://[::1]:8080/v1/", "http://[::1]:8080/v1")]
    public void TryNormalizeEndpoint_ValidHttpOrHttpsUrls_ReturnsCanonicalForm(string rawEndpoint, string expectedNormalized)
    {
        var ok = ApiKeyEndpointNormalizer.TryNormalizeEndpoint(rawEndpoint, out var normalized);

        Assert.True(ok);
        Assert.Equal(expectedNormalized, normalized);
        Assert.Equal(expectedNormalized, ApiKeyEndpointNormalizer.NormalizeEndpoint(rawEndpoint));
    }

    [Fact]
    public void TryNormalizeEndpoint_EquivalentUrls_ProduceIdenticalCanonicalKey()
    {
        var a = ApiKeyEndpointNormalizer.NormalizeEndpoint("https://api.openai.com/v1");
        var b = ApiKeyEndpointNormalizer.NormalizeEndpoint("  HTTPS://API.OPENAI.COM:443/v1/?ignore=1#hash ");

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("file:///C:/temp")]
    [InlineData("https:///missing-host")]
    public void TryNormalizeEndpoint_And_NormalizeEndpoint_FailFastOnInvalidEndpoints(string? invalidEndpoint)
    {
        Assert.False(ApiKeyEndpointNormalizer.TryNormalizeEndpoint(invalidEndpoint, out var normalized));
        Assert.Null(normalized);

        Assert.Throws<ArgumentException>(() => ApiKeyEndpointNormalizer.NormalizeEndpoint(invalidEndpoint));
    }
}
