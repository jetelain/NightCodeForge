using System.Text.RegularExpressions;

namespace NightCodeForge.Web.Tests;

internal static class WebProjectTestHelper
{
    public static string FindWebProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var directWebProj = Path.Combine(current.FullName, "NightCodeForge.Web.csproj");
            if (File.Exists(directWebProj))
            {
                return current.FullName;
            }

            var srcWebProj = Path.Combine(current.FullName, "src", "NightCodeForge.Web", "NightCodeForge.Web.csproj");
            if (File.Exists(srcWebProj))
            {
                return Path.Combine(current.FullName, "src", "NightCodeForge.Web");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate NightCodeForge.Web project directory from " + AppContext.BaseDirectory);
    }

    public static bool ContainsCssSelector(string css, string selector)
    {
        var pattern = $@"(?:^|[\s,}}]){Regex.Escape(selector)}(?![A-Za-z0-9_-])";
        return Regex.IsMatch(css, pattern, RegexOptions.Multiline);
    }
}
