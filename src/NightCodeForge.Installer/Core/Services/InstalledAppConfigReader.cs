namespace NightCodeForge.Installer.Core.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Constants;

/// <summary>
/// Reads NightCodeForge configuration layers in host precedence order
/// (<c>config/config.json</c> &gt; <c>appsettings.Production.json</c> &gt; <c>appsettings.json</c>)
/// using case-insensitive JSON object lookups compatible with Native AOT.
/// </summary>
internal static class InstalledAppConfigReader
{
    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IEnumerable<string> GetConfigurationFilePaths(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            yield break;
        }

        yield return Path.Combine(
            installDirectory,
            InstallerConstants.ConfigDirectoryName,
            InstallerConstants.UserConfigFileName);
        yield return Path.Combine(installDirectory, InstallerConstants.AppSettingsProductionFileName);
        yield return Path.Combine(installDirectory, InstallerConstants.AppSettingsFileName);
    }

    public static JsonObject? TryReadJsonObject(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JsonNode.Parse(raw, nodeOptions: null, documentOptions: LenientJsonOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Justification: Corrupt, duplicate-key, or temporarily locked config layers fall back to lower-precedence config.
            _ = ex;
            return null;
        }
    }

    public static string? ReadFirstNonEmptyString(string installDirectory, params string[] propertyPath)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || propertyPath.Length == 0 || !Directory.Exists(installDirectory))
        {
            return null;
        }

        foreach (var filePath in GetConfigurationFilePaths(installDirectory))
        {
            var root = TryReadJsonObject(filePath);
            if (root is null)
            {
                continue;
            }

            if (TryGetNestedString(root, propertyPath, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    public static string ReadDefaultDatabasePath(string installDirectory)
    {
        var fallback = Path.Combine(
            installDirectory,
            InstallerConstants.DataDirectoryName,
            InstallerConstants.MainDatabaseFileName);

        var connectionString = ReadFirstNonEmptyString(
            installDirectory,
            InstallerConstants.KeyConnectionStrings,
            InstallerConstants.KeyDefaultConnection);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return fallback;
        }

        var dataSource = ExtractSqliteDataSource(connectionString);
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return fallback;
        }

        try
        {
            return Path.IsPathRooted(dataSource)
                ? Path.GetFullPath(dataSource)
                : Path.GetFullPath(Path.Combine(installDirectory, dataSource));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Invalid Data Source path falls back to standard data/nightcodeforge.db path.
            _ = ex;
            return fallback;
        }
    }

    public static string? ExtractSqliteDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var segments = SplitConnectionStringSegments(connectionString);
        foreach (var segment in segments)
        {
            var eqIdx = segment.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            var key = segment[..eqIdx].Trim();
            if (!string.Equals(key, "Data Source", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "DataSource", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "Filename", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = segment[(eqIdx + 1)..].Trim();
            if (rawValue.Length >= 2 &&
                ((rawValue[0] == '"' && rawValue[^1] == '"') ||
                 (rawValue[0] == '\'' && rawValue[^1] == '\'')))
            {
                var quote = rawValue[0];
                var inner = rawValue[1..^1];
                return quote == '"'
                    ? inner.Replace("\"\"", "\"", StringComparison.Ordinal)
                    : inner.Replace("''", "'", StringComparison.Ordinal);
            }

            return rawValue;
        }

        return null;
    }

    public static bool TryGetPropertyIgnoreCase(JsonObject obj, string key, out JsonNode? value)
    {
        try
        {
            if (obj.TryGetPropertyValue(key, out value))
            {
                return true;
            }

            foreach (var kvp in obj)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
        }
        catch (ArgumentException)
        {
            // Justification: JsonObject throws ArgumentException lazily on duplicate identical keys.
        }

        value = null;
        return false;
    }

    private static bool TryGetNestedString(JsonObject root, string[] propertyPath, out string? result)
    {
        JsonNode? current = root;
        for (var i = 0; i < propertyPath.Length; i++)
        {
            if (current is not JsonObject obj || !TryGetPropertyIgnoreCase(obj, propertyPath[i], out current))
            {
                result = null;
                return false;
            }
        }

        if (current is JsonValue val && val.TryGetValue<string>(out var str))
        {
            result = str;
            return true;
        }

        result = null;
        return false;
    }

    private static List<string> SplitConnectionStringSegments(string connectionString)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        char activeQuote = '\0';

        for (var i = 0; i < connectionString.Length; i++)
        {
            var ch = connectionString[i];
            if (activeQuote != '\0')
            {
                current.Append(ch);
                if (ch == activeQuote)
                {
                    if (i + 1 < connectionString.Length && connectionString[i + 1] == activeQuote)
                    {
                        current.Append(connectionString[++i]);
                    }
                    else
                    {
                        activeQuote = '\0';
                    }
                }
            }
            else if (ch is '"' or '\'')
            {
                activeQuote = ch;
                current.Append(ch);
            }
            else if (ch == ';')
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }
}
