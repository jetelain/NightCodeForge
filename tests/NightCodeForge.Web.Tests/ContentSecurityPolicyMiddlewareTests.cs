using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Security;
using NightCodeForge.Web.Middleware;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ContentSecurityPolicyMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenCspEnabled_AppendsStrictCspHeader()
    {
        var context = new DefaultHttpContext();
        var options = Options.Create(new SecurityOptions { EnableContentSecurityPolicy = true });
        var nextExecuted = false;

        var middleware = new ContentSecurityPolicyMiddleware(
            next: ctx =>
            {
                nextExecuted = true;
                return Task.CompletedTask;
            },
            securityOptions: options);

        await middleware.InvokeAsync(context);

        Assert.True(nextExecuted);
        Assert.True(context.Response.Headers.ContainsKey(ContentSecurityPolicyMiddleware.HeaderName));

        var headerValue = context.Response.Headers[ContentSecurityPolicyMiddleware.HeaderName].ToString();
        Assert.Contains("default-src 'self'", headerValue);
        Assert.Contains("script-src 'self' 'unsafe-inline' 'unsafe-eval'", headerValue);
        Assert.Contains("style-src 'self' 'unsafe-inline'", headerValue);
        Assert.Contains("font-src 'self' data:", headerValue);
        Assert.Contains("img-src 'self' data: blob:", headerValue);
        Assert.Contains("connect-src 'self' ws: wss:", headerValue);
        Assert.Contains("worker-src 'self' blob:", headerValue);
        Assert.Contains("form-action 'self'", headerValue);
        Assert.Contains("frame-ancestors 'none'", headerValue);
        Assert.Contains("base-uri 'self'", headerValue);
    }

    [Fact]
    public async Task InvokeAsync_WhenCspDisabled_DoesNotAddCspHeader()
    {
        var context = new DefaultHttpContext();
        var options = Options.Create(new SecurityOptions { EnableContentSecurityPolicy = false });
        var nextExecuted = false;

        var middleware = new ContentSecurityPolicyMiddleware(
            next: ctx =>
            {
                nextExecuted = true;
                return Task.CompletedTask;
            },
            securityOptions: options);

        await middleware.InvokeAsync(context);

        Assert.True(nextExecuted);
        Assert.False(context.Response.Headers.ContainsKey(ContentSecurityPolicyMiddleware.HeaderName));
    }

    [Fact]
    public async Task InvokeAsync_WhenHeaderAlreadyPresent_DoesNotOverwrite()
    {
        var context = new DefaultHttpContext();
        const string customPolicy = "default-src 'self'; report-uri /csp-report;";
        context.Response.Headers[ContentSecurityPolicyMiddleware.HeaderName] = customPolicy;

        var options = Options.Create(new SecurityOptions { EnableContentSecurityPolicy = true });
        var middleware = new ContentSecurityPolicyMiddleware(
            next: ctx => Task.CompletedTask,
            securityOptions: options);

        await middleware.InvokeAsync(context);

        Assert.Equal(customPolicy, context.Response.Headers[ContentSecurityPolicyMiddleware.HeaderName].ToString());
    }

    [Fact]
    public void Constructor_RequiresNonNullDependencies()
    {
        var options = Options.Create(new SecurityOptions());
        RequestDelegate next = ctx => Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new ContentSecurityPolicyMiddleware(null!, options));
        Assert.Throws<ArgumentNullException>(() => new ContentSecurityPolicyMiddleware(next, null!));
    }

    [Fact]
    public async Task InvokeAsync_RequiresNonNullContext()
    {
        var options = Options.Create(new SecurityOptions());
        var middleware = new ContentSecurityPolicyMiddleware(ctx => Task.CompletedTask, options);

        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SecurityOptions_AirGappedMode_EvaluatesEnvironmentVariable(string? envValue, bool expectedResult)
    {
        var originalEnv = Environment.GetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, envValue);

            var options = new SecurityOptions { AirGappedMode = false };
            Assert.Equal(expectedResult, options.AirGappedMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, originalEnv);
        }
    }

    [Fact]
    public void SecurityOptions_AirGappedMode_WhenExplicitlyTrue_RemainsTrueRegardlessOfEnv()
    {
        var originalEnv = Environment.GetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, null);

            var options = new SecurityOptions { AirGappedMode = true };
            Assert.True(options.AirGappedMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, originalEnv);
        }
    }
}

