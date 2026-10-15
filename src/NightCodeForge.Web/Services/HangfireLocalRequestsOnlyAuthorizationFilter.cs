using System.Net;
using Hangfire.Dashboard;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Hangfire dashboard authorization filter restricting access to local requests (localhost / loopback).
/// </summary>
public class HangfireLocalRequestsOnlyAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            return true;
        }

        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip == null)
        {
            return true;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var host = httpContext.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }
}
