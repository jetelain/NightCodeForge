using System.Net;
using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class HttpErrorDetailHelperTests
{
    private sealed class OllamaException(string message) : Exception(message);
    [Fact]
    public void ExtractErrorMessageFromBody_WhenOllamaRouterErrorJson_ExtractsMessage()
    {
        var json = "{\"error\":\"No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest'\"}";
        var result = HttpErrorDetailHelper.ExtractErrorMessageFromBody(json);

        Assert.Equal("No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest'", result);
    }

    [Fact]
    public void ExtractErrorMessageFromBody_WhenNestedErrorMessage_ExtractsMessage()
    {
        var json = "{\"error\":{\"message\":\"Context length exceeded\"}}";
        var result = HttpErrorDetailHelper.ExtractErrorMessageFromBody(json);

        Assert.Equal("Context length exceeded", result);
    }

    [Fact]
    public void ExtractErrorMessageFromBody_WhenStandardMessageProperty_ExtractsMessage()
    {
        var json = "{\"message\":\"Service temporarily unavailable\"}";
        var result = HttpErrorDetailHelper.ExtractErrorMessageFromBody(json);

        Assert.Equal("Service temporarily unavailable", result);
    }

    [Fact]
    public void ExtractErrorMessageFromBody_WhenPlainText_ReturnsPlainText()
    {
        var plain = "503 Gateway Timeout";
        var result = HttpErrorDetailHelper.ExtractErrorMessageFromBody(plain);

        Assert.Equal("503 Gateway Timeout", result);
    }

    [Fact]
    public void ExtractErrorMessageFromBody_WhenNullOrEmpty_ReturnsNull()
    {
        Assert.Null(HttpErrorDetailHelper.ExtractErrorMessageFromBody(null));
        Assert.Null(HttpErrorDetailHelper.ExtractErrorMessageFromBody("   "));
    }

    [Fact]
    public void FormatLlmInvocationError_WhenNull_ReturnsEmptyString()
    {
        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(null);
        Assert.Equal(string.Empty, formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenHttpRequestExceptionWithBody_FormatsWithStatusCodeAndMessage()
    {
        var httpEx = new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable).", null, HttpStatusCode.ServiceUnavailable);
        httpEx.Data[HttpErrorDetailHelper.ResponseBodyKey] = "{\"error\":\"No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest'\"}";

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(httpEx);

        Assert.Equal("HTTP 503 (ServiceUnavailable): No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest'", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenHttpRequestExceptionWithEnrichedReasonPhrase_FormatsCorrectly()
    {
        var httpEx = new HttpRequestException(
            "Response status code does not indicate success: 503 (Service Unavailable: No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest').",
            null,
            HttpStatusCode.ServiceUnavailable);

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(httpEx);

        Assert.Equal("HTTP 503 (ServiceUnavailable): No enabled routing target is available to handle the request for model 'Qwen3.8-27B:latest'", formatted);
    }

    [Fact]
    public void ExtractErrorMessageFromHttpExceptionMessage_WhenUrlOrHostInParentheses_ReturnsNull()
    {
        var msg = "An error occurred while sending the request (http://127.0.0.1:11434/api/chat)";
        var detail = HttpErrorDetailHelper.ExtractErrorMessageFromHttpExceptionMessage(msg);

        Assert.Null(detail);
    }

    [Fact]
    public void ExtractErrorMessageFromHttpExceptionMessage_WhenEnriched_ExtractsDetail()
    {
        var msg = "Response status code does not indicate success: 503 (Service Unavailable: No enabled routing target).";
        var detail = HttpErrorDetailHelper.ExtractErrorMessageFromHttpExceptionMessage(msg);

        Assert.Equal("No enabled routing target", detail);
    }

    [Fact]
    public void ExtractErrorMessageFromHttpExceptionMessage_WhenStandardWithoutEnrichment_ReturnsNull()
    {
        var msg = "Response status code does not indicate success: 503 (Service Unavailable).";
        var detail = HttpErrorDetailHelper.ExtractErrorMessageFromHttpExceptionMessage(msg);

        Assert.Null(detail);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenOllamaException_ReturnsOllamaMessage()
    {
        var ollamaEx = new OllamaException("Model does not support tools.");
        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(ollamaEx);

        Assert.Equal("Model does not support tools.", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenWrappedInAggregateException_FindsInnerAndFormats()
    {
        var httpEx = new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        httpEx.Data[HttpErrorDetailHelper.ResponseBodyKey] = "{\"error\":\"Fleet offline\"}";
        var agg = new AggregateException(httpEx);

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(agg);

        Assert.Equal("HTTP 503 (ServiceUnavailable): Fleet offline", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenOllamaExceptionInsideMultiExceptionAggregate_FindsAndFormats()
    {
        var inner1 = new InvalidOperationException("Generic operational failure");
        var inner2 = new OllamaException("Model does not support tools.");
        var agg = new AggregateException(inner1, inner2);

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(agg);

        Assert.Equal("Model does not support tools.", formatted);
    }

    [Fact]
    public void SanitizeForReasonPhrase_StripsNewlinesAndLimitsLength()
    {
        var input = "Line 1\r\nLine 2\nLine 3";
        var sanitized = HttpErrorDetailHelper.SanitizeForReasonPhrase(input);

        Assert.DoesNotContain("\r", sanitized);
        Assert.DoesNotContain("\n", sanitized);
        Assert.Equal("Line 1  Line 2 Line 3", sanitized);
    }

    [Fact]
    public void SanitizeForReasonPhrase_StripsControlCharacters()
    {
        var input = "Error\0with\tnulls and\bcontrol chars";
        var sanitized = HttpErrorDetailHelper.SanitizeForReasonPhrase(input);

        Assert.DoesNotContain('\0', sanitized);
        Assert.DoesNotContain('\t', sanitized);
        Assert.DoesNotContain('\b', sanitized);
        Assert.Equal("Error with nulls and control chars", sanitized);
    }
}
