using System.Runtime.CompilerServices;
using Bunit;

namespace NightCodeForge.Web.Tests;

internal static class BunitTestDefaults
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
    }
}
