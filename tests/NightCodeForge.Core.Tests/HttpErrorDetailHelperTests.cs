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

    [Fact]
    public void FormatLlmInvocationError_WhenHttpClientTimeout_FormatsActionableTimeoutMessage()
    {
        var httpClientTimeoutEx = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(httpClientTimeoutEx);

        Assert.DoesNotContain("The request was canceled", formatted);
        Assert.DoesNotContain("The operation was canceled", formatted);
        Assert.Contains("LLM inference request timed out after 900 seconds", formatted);
        Assert.Contains("The prompt context may be too large", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenRetriedTimeout_FormatsRetriedActionableTimeoutMessage()
    {
        var timeoutEx = new TimeoutException("The request timed out after 900 seconds (2 attempt(s) exhausted).");
        timeoutEx.Data[ResilienceConstants.RetriedKey] = true;

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(timeoutEx);

        Assert.DoesNotContain("The request was canceled", formatted);
        Assert.Contains("LLM inference request timed out after 900 seconds (retried once; 2 attempts exhausted)", formatted);
        Assert.Contains("The prompt context may be too large or the inference server is overloaded.", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenGenericTimeoutException_FormatsCleanTimeoutMessage()
    {
        var timeoutEx = new TimeoutException("The operation has timed out.");

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(timeoutEx);

        Assert.Equal("LLM inference request timed out. The prompt context may be too large or the inference server is overloaded.", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenSingleAttemptTimeoutTaggedAsRetried_DoesNotReportRetriedOnce()
    {
        var timeoutEx = new TimeoutException("The request timed out after 30 seconds (1 attempt(s) exhausted).");
        timeoutEx.Data[ResilienceConstants.RetriedKey] = true;

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(timeoutEx);

        Assert.Equal("LLM inference request timed out after 30 seconds. The prompt context may be too large or the inference server is overloaded.", formatted);
        Assert.DoesNotContain("retried once", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenFirstTokenPrefillTimeout_FormatsPrefillTimeoutMessage()
    {
        var ex = new LlmStreamTimeoutException(
            "timeout",
            new TimeoutException(),
            StreamTimeoutPhase.FirstTokenPrefill,
            elapsedSeconds: 600.0,
            configuredTimeoutSeconds: 600.0,
            generatedTokens: 0,
            tokensPerSecond: 0.0);

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(ex);

        Assert.Equal("[TIMEOUT] LLM request aborted by client after 600.0s prefill timeout (0 tokens received; configured prefill timeout: 600s). The prompt context may be too large (cold KV cache) or the inference server queue is saturated.", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenMidStreamIdleStall_FormatsIdleStallMessage()
    {
        var ex = new LlmStreamTimeoutException(
            "timeout",
            new TimeoutException(),
            StreamTimeoutPhase.MidStreamIdleStall,
            elapsedSeconds: 857.1,
            configuredTimeoutSeconds: 300.0,
            generatedTokens: 15000,
            tokensPerSecond: 17.5);

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(ex);

        Assert.Equal("[TIMEOUT] LLM stream stalled after 857.1s (15,000 tokens generated at 17.5 tok/s): no tokens received for 300s.", formatted);
    }

    [Fact]
    public void FormatLlmInvocationError_WhenMidStreamConnectionClosed_FormatsStreamAbortedMessage()
    {
        var inner = new HttpRequestException("The connection was closed prematurely.");
        inner.Data[StreamTimeoutDiagnostics.DataKey] = new StreamTimeoutDiagnostics
        {
            Phase = StreamTimeoutPhase.MidStreamConnectionClosed,
            ElapsedSeconds = 666.7,
            ConfiguredTimeoutSeconds = 300.0,
            GeneratedTokens = 12000,
            TokensPerSecond = 18.0
        };

        var formatted = HttpErrorDetailHelper.FormatLlmInvocationError(inner);

        Assert.Equal("[STREAM ABORTED] LLM stream closed prematurely by upstream server/proxy after 666.7s while actively generating (12,000 tokens generated at 18.0 tok/s). Check upstream reverse-proxy timeout settings (e.g., OllamaRouter / Nginx proxy_read_timeout).", formatted);
    }

    [Theory]
    [InlineData(StreamTimeoutPhase.PreHeaderPrefill, true, true)]
    [InlineData(StreamTimeoutPhase.FirstTokenPrefill, true, true)]
    [InlineData(StreamTimeoutPhase.MidStreamIdleStall, true, true)]
    [InlineData(StreamTimeoutPhase.MidStreamConnectionClosed, false, true)]
    public void TransientErrorDetector_RecognizesAllStreamTimeoutPhases_WithoutRequiringInnerTimeoutException(
        StreamTimeoutPhase phase,
        bool expectedIsHttpTimeout,
        bool expectedIsTransient)
    {
        var ex = new LlmStreamTimeoutException(
            "stream failure",
            innerException: new OperationCanceledException("aborted"),
            phase: phase,
            elapsedSeconds: 120.0,
            configuredTimeoutSeconds: 120.0,
            generatedTokens: phase is StreamTimeoutPhase.PreHeaderPrefill or StreamTimeoutPhase.FirstTokenPrefill ? 0 : 500,
            tokensPerSecond: 15.0);

        Assert.Equal(expectedIsHttpTimeout, TransientErrorDetector.IsHttpTimeout(ex));
        Assert.Equal(expectedIsTransient, TransientErrorDetector.IsTransient(ex));
    }
}
