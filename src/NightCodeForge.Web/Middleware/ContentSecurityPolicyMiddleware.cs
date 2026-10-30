using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Web.Middleware;

/// <summary>
/// ASP.NET Core middleware enforcing a strict Content Security Policy (CSP) header
/// to guarantee zero unauthorized external browser subresource connections in air-gapped environments.
/// </summary>
public class ContentSecurityPolicyMiddleware
{
    public const string HeaderName = "Content-Security-Policy";

    public const string DefaultPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "font-src 'self' data:; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self';";

    private readonly RequestDelegate _next;
    private readonly IOptions<SecurityOptions> _securityOptions;

    public ContentSecurityPolicyMiddleware(RequestDelegate next, IOptions<SecurityOptions> securityOptions)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_securityOptions.Value.EnableContentSecurityPolicy)
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = DefaultPolicy;
            }
        }

        await _next(context);
    }
}

/// <summary>
/// Extension methods for registering <see cref="ContentSecurityPolicyMiddleware"/> in the ASP.NET Core pipeline.
/// </summary>
public static class ContentSecurityPolicyMiddlewareExtensions
{
    public static IApplicationBuilder UseContentSecurityPolicy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ContentSecurityPolicyMiddleware>();
    }
}

