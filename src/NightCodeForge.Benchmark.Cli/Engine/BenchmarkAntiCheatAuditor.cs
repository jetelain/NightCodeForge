using System.Text.RegularExpressions;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Inspects executed bash commands post-run to detect high-signal anti-cheat or external source/cache
/// inspection attempts without altering deterministic scores.
/// </summary>
internal static partial class BenchmarkAntiCheatAuditor
{
    internal const string PackageArchiveDownload = "package_archive_download";
    internal const string CdnOrSourceHostFetch = "cdn_or_source_host_fetch";
    internal const string ExternalGitRemoteQuery = "external_git_remote_query";
    internal const string BinaryOrCacheInspection = "binary_or_cache_inspection";

    private const string ExecPrefixPattern = @"(?:(?:/|\.\.?/|~/)(?:[A-Za-z0-9._+-]+/)*)?";
    private const string LeadingFlagsPattern = @"(?:\s+-[A-Za-z0-9-]+(?:=\S+)?)*";

    private static readonly string[] BlockedHostPatterns = BuildBlockedHostPatterns();

    private static readonly HashSet<string> NpmPackValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--pack-destination",
        "--workspace",
        "-w",
        "--loglevel",
        "--cache",
        "--prefix",
        "--userconfig",
        "--registry",
        "-C"
    };

    private static readonly HashSet<string> GitValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--depth",
        "--shallow-since",
        "--shallow-exclude",
        "--deepen",
        "--negotiation-tip",
        "--refmap",
        "--recurse-submodules",
        "--recurse-submodules-default",
        "--filter",
        "--upload-pack",
        "-o",
        "--server-option",
        "-j",
        "--jobs",
        "--strategy",
        "-s",
        "--strategy-option",
        "-X",
        "--rebase",
        "-r"
    };

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"(?:pip\d*" + LeadingFlagsPattern + @"|python\d*(?:\.\d+)?(?:\s+-[A-Za-z0-9-]+)*\s+-m\s+pip" + LeadingFlagsPattern + @"|uv\s+pip" + LeadingFlagsPattern + @")\s+download\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipDownloadRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"npm" + LeadingFlagsPattern + @"\s+(?:view|info)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmMetadataRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"npm" + LeadingFlagsPattern + @"\s+pack\b(?<rest>[^;&|\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmPackRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"(?:curl|wget|httpie|http\s+|fetch\(|urllib|requests\.(?:get|head|request)|httpx|aiohttp|invoke-webrequest|iwr|python\d*(?:\.\d+)?\s+-c|node\s+(?:-e|--eval)|nc\s+|ncat|socat|openssl\s+s_client)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkFetchToolRegex();

    [GeneratedRegex(@"^\s*(?:[A-Za-z_][A-Za-z0-9_]*=\S+\s+)*(?:rg|grep|git\s+(?:log|show|diff))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PureLocalReadCommandRegex();

    [GeneratedRegex(@"https?://(?:www\.)?(?<host>[A-Za-z0-9.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlHostRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"git(?:\s+(?:-C\s+\S+|-c\s+\S+|--git-dir(?:=|\s+)\S+|--work-tree(?:=|\s+)\S+))*\s+(?<subcommand>ls-remote|fetch|clone|pull)\b(?<rest>[^;&|\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitRemoteCommandRegex();

    [GeneratedRegex(@"(?:https?://|ssh://|git://|git@)|(?:^|[\s'""])(?:[A-Za-z0-9._-]+@[A-Za-z0-9._-]+|[A-Za-z0-9._-]+\.[A-Za-z0-9._-]+):[^\s;'" + "\"" + @"|&]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalGitRemoteUriRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"(?:ilspycmd|monodis|ildasm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecompilerToolRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"(?:unzip|zipinfo|tar|7z|strings|cat|head|tail|less|cp|python\d*(?:\.\d+)?)\b[^;&|\r\n]*\.nupkg\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NupkgExtractionRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])" + ExecPrefixPattern + @"(?:cat|ls|find|rg|grep|head|tail|less|sed|awk|python\d*(?:\.\d+)?|cp|tar|unzip)\b[^;&|\r\n]*(?:/cache/nuget/|\.nuget/packages/|/cache/cargo/registry|\.cargo/registry|/cache/cargo\b|/cache/nuget\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CacheInspectionCommandRegex();

    [GeneratedRegex(@"(?:/cache/cargo/registry|\.cargo/registry|/cache/nuget/|\.nuget/packages/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectRegistryCachePathRegex();

    [GeneratedRegex(@"\b(?:CARGO_HOME|NUGET_PACKAGES)=(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryCacheEnvAssignmentRegex();

    [GeneratedRegex(@"^(?:\d*>>?|\d*<|&>>?)$", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneRedirectionRegex();

    [GeneratedRegex(@"^(?:\d*>>?&?\S+|\d*<&?\S+|\d*>&\d+|&>>?\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachedRedirectionRegex();

    private static readonly Regex BlockedHostRegex = new(
        $@"(?:^|[^A-Za-z0-9.-])(?:[A-Za-z0-9-]+\.)*(?:{string.Join("|", BlockedHostPatterns)})(?:$|[^A-Za-z0-9.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string[] BuildBlockedHostPatterns() =>
        BenchmarkDefaults.BlockedAntiCheatHosts
            .Select(h => Regex.Escape(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static IReadOnlyList<string> DetectSignals(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Array.Empty<string>();
        }

        var signals = new HashSet<string>(StringComparer.Ordinal);

        if (IsPackageArchiveDownload(command))
        {
            signals.Add(PackageArchiveDownload);
        }

        if (IsCdnOrSourceHostFetch(command))
        {
            signals.Add(CdnOrSourceHostFetch);
        }

        if (IsExternalGitRemoteQuery(command))
        {
            signals.Add(ExternalGitRemoteQuery);
        }

        if (IsBinaryOrCacheInspection(command))
        {
            signals.Add(BinaryOrCacheInspection);
        }

        return signals.Count == 0
            ? Array.Empty<string>()
            : signals.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static bool IsPackageArchiveDownload(string command)
    {
        if (PipDownloadRegex().IsMatch(command) || NpmMetadataRegex().IsMatch(command))
        {
            return true;
        }

        foreach (Match match in NpmPackRegex().Matches(command))
        {
            var rest = match.Groups["rest"].Value;
            if (HasExternalNpmPackTarget(rest))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExternalNpmPackTarget(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        var tokens = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = UnquoteToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (StandaloneRedirectionRegex().IsMatch(token))
            {
                if (i + 1 < tokens.Length)
                {
                    i++;
                }

                continue;
            }

            if (AttachedRedirectionRegex().IsMatch(token))
            {
                continue;
            }

            if (token.StartsWith('-'))
            {
                if (!token.Contains('=') &&
                    NpmPackValueFlags.Contains(token) &&
                    i + 1 < tokens.Length)
                {
                    i++;
                }

                continue;
            }

            if (token is "." or ".." ||
                token.StartsWith("./", StringComparison.Ordinal) ||
                token.StartsWith("../", StringComparison.Ordinal) ||
                token.StartsWith("/", StringComparison.Ordinal) ||
                token.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsCdnOrSourceHostFetch(string command)
    {
        if (NetworkFetchToolRegex().IsMatch(command) && BlockedHostRegex.IsMatch(command))
        {
            return true;
        }

        if (PureLocalReadCommandRegex().IsMatch(command))
        {
            return false;
        }

        foreach (Match urlMatch in HttpUrlHostRegex().Matches(command))
        {
            var host = urlMatch.Groups["host"].Value;
            if (BlockedHostRegex.IsMatch(host))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExternalGitRemoteQuery(string command)
    {
        foreach (Match match in GitRemoteCommandRegex().Matches(command))
        {
            var subcommand = match.Groups["subcommand"].Value;
            var rest = match.Groups["rest"].Value;

            if (ExternalGitRemoteUriRegex().IsMatch(rest))
            {
                return true;
            }

            if (string.Equals(subcommand, "clone", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nonFlagTokens = ExtractNonFlagGitArgs(rest);
            if (nonFlagTokens.Count == 0)
            {
                return true;
            }

            var target = nonFlagTokens[0];
            if (!IsLocalGitTarget(target))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ExtractNonFlagGitArgs(string rest)
    {
        var result = new List<string>();
        var tokens = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = UnquoteToken(tokens[i]);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (StandaloneRedirectionRegex().IsMatch(token))
            {
                if (i + 1 < tokens.Length)
                {
                    i++;
                }

                continue;
            }

            if (AttachedRedirectionRegex().IsMatch(token))
            {
                continue;
            }

            if (token.StartsWith('-'))
            {
                if (!token.Contains('=') &&
                    GitValueFlags.Contains(token) &&
                    i + 1 < tokens.Length)
                {
                    i++;
                }

                continue;
            }

            result.Add(token);
        }

        return result;
    }

    private static bool IsLocalGitTarget(string target) =>
        target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("/", StringComparison.Ordinal) ||
        target.StartsWith("./", StringComparison.Ordinal) ||
        target.StartsWith("../", StringComparison.Ordinal) ||
        target is "." or "..";

    private static bool IsBinaryOrCacheInspection(string command)
    {
        if (DecompilerToolRegex().IsMatch(command) ||
            NupkgExtractionRegex().IsMatch(command) ||
            CacheInspectionCommandRegex().IsMatch(command))
        {
            return true;
        }

        var stripped = RegistryCacheEnvAssignmentRegex().Replace(command, string.Empty);
        return DirectRegistryCachePathRegex().IsMatch(stripped);
    }

    private static string UnquoteToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
