using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public static class TestServices
{
    public static ToastService CreateToastService() =>
        new(new BackgroundTaskRunner(NullLogger<BackgroundTaskRunner>.Instance));
}
