using System.Collections.Concurrent;
using System.Text;
using EC = global::EditorConfig.Core;

namespace NightCodeForge.Agents.Tools.EditorConfig;

/// <summary>
/// Service to resolve EditorConfig settings hierarchically up to root = true.
/// </summary>
public class EditorConfigService : IEditorConfigService
{
    private readonly EC.EditorConfigParser _parser = new();
    private readonly ConcurrentDictionary<string, EditorConfigSettings> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<EditorConfigSettings> GetSettingsForPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(new EditorConfigSettings());
        }

        if (_cache.TryGetValue(filePath, out var cached))
        {
            return Task.FromResult(cached);
        }

        try
        {
            var config = _parser.Parse(filePath);
            var settings = MapConfiguration(config);
            _cache[filePath] = settings;
            return Task.FromResult(settings);
        }
        catch
        {
            // Fallback to default/empty settings if parsing fails
            var fallback = new EditorConfigSettings();
            return Task.FromResult(fallback);
        }
    }

    private static EditorConfigSettings MapConfiguration(EC.FileConfiguration config)
    {
        EndOfLine? eol = null;
        if (config.EndOfLine.HasValue)
        {
            eol = config.EndOfLine.Value switch
            {
                EC.EndOfLine.LF => EndOfLine.Lf,
                EC.EndOfLine.CRLF => EndOfLine.CrLf,
                EC.EndOfLine.CR => EndOfLine.Cr,
                _ => null
            };
        }

        IndentStyle? indentStyle = null;
        if (config.IndentStyle.HasValue)
        {
            indentStyle = config.IndentStyle.Value switch
            {
                EC.IndentStyle.Space => IndentStyle.Space,
                EC.IndentStyle.Tab => IndentStyle.Tab,
                _ => null
            };
        }

        int? indentSize = null;
        if (config.IndentSize != null && !config.IndentSize.IsUnset)
        {
            indentSize = config.IndentSize.NumberOfColumns;
        }

        int? tabWidth = config.TabWidth;
        bool? insertFinalNewline = config.InsertFinalNewline;
        bool? trimTrailingWhitespace = config.TrimTrailingWhitespace;

        Encoding? charset = null;
        if (config.Charset.HasValue)
        {
            charset = config.Charset.Value switch
            {
                EC.Charset.UTF8 => new UTF8Encoding(false),
                EC.Charset.UTF8BOM => new UTF8Encoding(true),
                EC.Charset.Latin1 => Encoding.Latin1,
                _ => null
            };
        }

        return new EditorConfigSettings(
            EndOfLine: eol,
            IndentStyle: indentStyle,
            IndentSize: indentSize,
            TabWidth: tabWidth,
            InsertFinalNewline: insertFinalNewline,
            TrimTrailingWhitespace: trimTrailingWhitespace,
            Charset: charset
        );
    }
}
