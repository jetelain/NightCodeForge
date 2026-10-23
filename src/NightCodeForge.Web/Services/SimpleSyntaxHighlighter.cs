using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Deterministic server-side syntax highlighter producing styled token spans
/// with full HTML entity encoding.
/// </summary>
public partial class SimpleSyntaxHighlighter : ISyntaxHighlightingService
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "break", "case", "catch", "class",
        "const", "continue", "default", "delegate", "do", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "for", "foreach", "goto",
        "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace",
        "new", "null", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "record", "ref", "return", "sealed", "sizeof", "stackalloc",
        "static", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "unchecked", "unsafe", "using", "virtual", "void", "volatile", "while",
        "yield", "get", "set", "init", "var"
    };

    private static readonly HashSet<string> CSharpPrimitiveTypes = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint",
        "nint", "nuint", "long", "ulong", "short", "ushort", "object", "string", "dynamic"
    };

    private static readonly HashSet<string> TsJsKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "export", "extends", "finally", "for", "function",
        "if", "import", "in", "instanceof", "new", "return", "super", "switch",
        "this", "throw", "try", "typeof", "var", "void", "while", "with", "yield",
        "let", "static", "enum", "implements", "interface", "package", "private",
        "protected", "public", "type", "async", "await", "from", "as", "true", "false",
        "null", "undefined", "NaN"
    };

    private static readonly HashSet<string> TsPrimitiveTypes = new(StringComparer.Ordinal)
    {
        "string", "number", "boolean", "any", "unknown", "never", "void", "symbol", "bigint"
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break",
        "class", "continue", "def", "del", "elif", "else", "except", "finally",
        "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
        "not", "or", "pass", "raise", "return", "try", "while", "with", "yield"
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "case", "esac", "for", "select",
        "while", "until", "do", "done", "in", "function", "time", "echo",
        "exit", "return", "export", "source", "set", "unset"
    };

    public string HighlightLine(string? line, string? filePathOrExtension)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var ext = ExtractExtension(filePathOrExtension);
        return ext switch
        {
            ".cs" => TokenizeCSharp(line),
            ".ts" or ".js" or ".tsx" or ".jsx" => TokenizeTypeScript(line),
            ".py" => TokenizePython(line),
            ".json" => TokenizeJson(line),
            ".xml" or ".html" or ".razor" or ".csproj" => TokenizeXml(line),
            ".sh" or ".bash" or ".ps1" or ".cmd" or ".bat" => TokenizeShell(line),
            ".md" or ".markdown" => TokenizeMarkdown(line),
            _ => HtmlEncoder.Default.Encode(line)
        };
    }

    private static string ExtractExtension(string? pathOrExt)
    {
        if (string.IsNullOrWhiteSpace(pathOrExt)) return string.Empty;
        var dotIdx = pathOrExt.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            return pathOrExt[dotIdx..].ToLowerInvariant();
        }
        return pathOrExt.StartsWith('.') ? pathOrExt.ToLowerInvariant() : "." + pathOrExt.ToLowerInvariant();
    }

    private static string TokenizeCSharp(string line)
    {
        return TokenizeCStyle(line, CSharpKeywords, CSharpPrimitiveTypes, isCSharp: true);
    }

    private static string TokenizeTypeScript(string line)
    {
        return TokenizeCStyle(line, TsJsKeywords, TsPrimitiveTypes, isCSharp: false);
    }

    private static string TokenizeCStyle(string line, HashSet<string> keywords, HashSet<string> primitiveTypes, bool isCSharp)
    {
        var sb = new StringBuilder(line.Length * 2);
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            // Line comment: //
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                sb.Append("<span class=\"tok-comment\">")
                  .Append(HtmlEncoder.Default.Encode(line[i..]))
                  .Append("</span>");
                break;
            }

            // Block comment single-line: /* ... */
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '*')
            {
                var endComment = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (endComment >= 0)
                {
                    var commentContent = line[i..(endComment + 2)];
                    sb.Append("<span class=\"tok-comment\">")
                      .Append(HtmlEncoder.Default.Encode(commentContent))
                      .Append("</span>");
                    i = endComment + 2;
                    continue;
                }
                else
                {
                    sb.Append("<span class=\"tok-comment\">")
                      .Append(HtmlEncoder.Default.Encode(line[i..]))
                      .Append("</span>");
                    break;
                }
            }

            // String literals: "...", '...', $"...", @"..."
            if (isCSharp && (line[i] == '@' || line[i] == '$') && i + 1 < len && line[i + 1] == '"')
            {
                var stringEnd = FindStringEnd(line, i + 2, '"');
                var strContent = line[i..stringEnd];
                sb.Append("<span class=\"tok-string\">")
                  .Append(HtmlEncoder.Default.Encode(strContent))
                  .Append("</span>");
                i = stringEnd;
                continue;
            }

            if (line[i] == '"' || (!isCSharp && line[i] == '\'') || (!isCSharp && line[i] == '`'))
            {
                char quote = line[i];
                var stringEnd = FindStringEnd(line, i + 1, quote);
                var strContent = line[i..stringEnd];
                sb.Append("<span class=\"tok-string\">")
                  .Append(HtmlEncoder.Default.Encode(strContent))
                  .Append("</span>");
                i = stringEnd;
                continue;
            }

            if (isCSharp && line[i] == '\'')
            {
                var stringEnd = FindStringEnd(line, i + 1, '\'');
                var strContent = line[i..stringEnd];
                sb.Append("<span class=\"tok-string\">")
                  .Append(HtmlEncoder.Default.Encode(strContent))
                  .Append("</span>");
                i = stringEnd;
                continue;
            }

            // Numbers: 0x..., 123.45, etc.
            if (char.IsDigit(line[i]) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_'))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                {
                    i++;
                }
                sb.Append("<span class=\"tok-number\">")
                  .Append(HtmlEncoder.Default.Encode(line[start..i]))
                  .Append("</span>");
                continue;
            }

            // Identifiers / Keywords / Types
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }
                var word = line[start..i];
                if (keywords.Contains(word))
                {
                    sb.Append("<span class=\"tok-keyword\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else if (primitiveTypes.Contains(word) || (word.Length > 1 && char.IsUpper(word[0]) && word.Skip(1).Any(char.IsLower)))
                {
                    sb.Append("<span class=\"tok-type\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else
                {
                    sb.Append(HtmlEncoder.Default.Encode(word));
                }
                continue;
            }

            // Operators & Punctuation
            char c = line[i];
            if ("{}()[];:,.=+-*/%<>!&|^~?".Contains(c))
            {
                sb.Append("<span class=\"tok-punct\">")
                  .Append(HtmlEncoder.Default.Encode(c.ToString()))
                  .Append("</span>");
                i++;
                continue;
            }

            // Other character (whitespace, etc.)
            sb.Append(HtmlEncoder.Default.Encode(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TokenizePython(string line)
    {
        var sb = new StringBuilder(line.Length * 2);
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            // Comment: #
            if (line[i] == '#')
            {
                sb.Append("<span class=\"tok-comment\">")
                  .Append(HtmlEncoder.Default.Encode(line[i..]))
                  .Append("</span>");
                break;
            }

            // Strings: "..." or '...'
            if (line[i] == '"' || line[i] == '\'')
            {
                char quote = line[i];
                var stringEnd = FindStringEnd(line, i + 1, quote);
                sb.Append("<span class=\"tok-string\">")
                  .Append(HtmlEncoder.Default.Encode(line[i..stringEnd]))
                  .Append("</span>");
                i = stringEnd;
                continue;
            }

            // Numbers
            if (char.IsDigit(line[i]) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_'))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                {
                    i++;
                }
                sb.Append("<span class=\"tok-number\">")
                  .Append(HtmlEncoder.Default.Encode(line[start..i]))
                  .Append("</span>");
                continue;
            }

            // Identifiers / Keywords
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }
                var word = line[start..i];
                if (PythonKeywords.Contains(word))
                {
                    sb.Append("<span class=\"tok-keyword\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else if (word.Length > 1 && char.IsUpper(word[0]))
                {
                    sb.Append("<span class=\"tok-type\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else
                {
                    sb.Append(HtmlEncoder.Default.Encode(word));
                }
                continue;
            }

            // Punctuation
            char c = line[i];
            if ("{}()[];:,.=+-*/%<>!&|^~@".Contains(c))
            {
                sb.Append("<span class=\"tok-punct\">")
                  .Append(HtmlEncoder.Default.Encode(c.ToString()))
                  .Append("</span>");
                i++;
                continue;
            }

            sb.Append(HtmlEncoder.Default.Encode(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TokenizeJson(string line)
    {
        var sb = new StringBuilder(line.Length * 2);
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            if (line[i] == '"')
            {
                var stringEnd = FindStringEnd(line, i + 1, '"');
                var text = line[i..stringEnd];

                // Check if followed by colon (key vs string value)
                int checkCol = stringEnd;
                while (checkCol < len && char.IsWhiteSpace(line[checkCol])) checkCol++;
                bool isKey = checkCol < len && line[checkCol] == ':';

                if (isKey)
                {
                    sb.Append("<span class=\"tok-type\">")
                      .Append(HtmlEncoder.Default.Encode(text))
                      .Append("</span>");
                }
                else
                {
                    sb.Append("<span class=\"tok-string\">")
                      .Append(HtmlEncoder.Default.Encode(text))
                      .Append("</span>");
                }
                i = stringEnd;
                continue;
            }

            // Numbers
            if (char.IsDigit(line[i]) || (line[i] == '-' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                if (line[i] == '-') i++;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'e' || line[i] == 'E' || line[i] == '+' || line[i] == '-'))
                {
                    i++;
                }
                sb.Append("<span class=\"tok-number\">")
                  .Append(HtmlEncoder.Default.Encode(line[start..i]))
                  .Append("</span>");
                continue;
            }

            // Keywords: true, false, null
            if (char.IsLetter(line[i]))
            {
                int start = i;
                while (i < len && char.IsLetter(line[i])) i++;
                var word = line[start..i];
                if (word is "true" or "false" or "null")
                {
                    sb.Append("<span class=\"tok-keyword\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else
                {
                    sb.Append(HtmlEncoder.Default.Encode(word));
                }
                continue;
            }

            // Punctuation
            char c = line[i];
            if ("{}[],:".Contains(c))
            {
                sb.Append("<span class=\"tok-punct\">")
                  .Append(HtmlEncoder.Default.Encode(c.ToString()))
                  .Append("</span>");
                i++;
                continue;
            }

            sb.Append(HtmlEncoder.Default.Encode(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TokenizeXml(string line)
    {
        var sb = new StringBuilder(line.Length * 2);
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            // Comment <!-- ... -->
            if (i + 3 < len && line[i] == '<' && line[i + 1] == '!' && line[i + 2] == '-' && line[i + 3] == '-')
            {
                var endComment = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (endComment >= 0)
                {
                    var text = line[i..(endComment + 3)];
                    sb.Append("<span class=\"tok-comment\">")
                      .Append(HtmlEncoder.Default.Encode(text))
                      .Append("</span>");
                    i = endComment + 3;
                    continue;
                }
                else
                {
                    sb.Append("<span class=\"tok-comment\">")
                      .Append(HtmlEncoder.Default.Encode(line[i..]))
                      .Append("</span>");
                    break;
                }
            }

            // Tags <tag ... > or </tag>
            if (line[i] == '<')
            {
                int endTag = line.IndexOf('>', i + 1);
                if (endTag >= 0)
                {
                    var tagContent = line[i..(endTag + 1)];
                    // Tokenize inside tag
                    sb.Append("<span class=\"tok-punct\">&lt;</span>");
                    int insideStart = i + 1;
                    if (insideStart < endTag && line[insideStart] == '/')
                    {
                        sb.Append("<span class=\"tok-punct\">/</span>");
                        insideStart++;
                    }

                    // Tag name
                    int nameEnd = insideStart;
                    while (nameEnd < endTag && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == ':' || line[nameEnd] == '-' || line[nameEnd] == '_'))
                    {
                        nameEnd++;
                    }
                    if (nameEnd > insideStart)
                    {
                        sb.Append("<span class=\"tok-keyword\">")
                          .Append(HtmlEncoder.Default.Encode(line[insideStart..nameEnd]))
                          .Append("</span>");
                    }

                    // Remainder of tag (attributes, values)
                    int p = nameEnd;
                    while (p < endTag)
                    {
                        if (line[p] == '"' || line[p] == '\'')
                        {
                            char q = line[p];
                            int qEnd = line.IndexOf(q, p + 1);
                            if (qEnd < 0 || qEnd > endTag) qEnd = endTag;
                            else qEnd++;
                            sb.Append("<span class=\"tok-string\">")
                              .Append(HtmlEncoder.Default.Encode(line[p..qEnd]))
                              .Append("</span>");
                            p = qEnd;
                            continue;
                        }
                        if (line[p] == '=')
                        {
                            sb.Append("<span class=\"tok-punct\">=</span>");
                            p++;
                            continue;
                        }
                        if (char.IsLetter(line[p]))
                        {
                            int aStart = p;
                            while (p < endTag && (char.IsLetterOrDigit(line[p]) || line[p] == '-' || line[p] == ':')) p++;
                            sb.Append("<span class=\"tok-type\">")
                              .Append(HtmlEncoder.Default.Encode(line[aStart..p]))
                              .Append("</span>");
                            continue;
                        }
                        if (line[p] == '/' && p == endTag - 1)
                        {
                            sb.Append("<span class=\"tok-punct\">/</span>");
                            p++;
                            continue;
                        }
                        sb.Append(HtmlEncoder.Default.Encode(line[p].ToString()));
                        p++;
                    }

                    sb.Append("<span class=\"tok-punct\">&gt;</span>");
                    i = endTag + 1;
                    continue;
                }
            }

            sb.Append(HtmlEncoder.Default.Encode(line[i].ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TokenizeShell(string line)
    {
        var sb = new StringBuilder(line.Length * 2);
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            if (line[i] == '#')
            {
                sb.Append("<span class=\"tok-comment\">")
                  .Append(HtmlEncoder.Default.Encode(line[i..]))
                  .Append("</span>");
                break;
            }

            if (line[i] == '"' || line[i] == '\'')
            {
                char q = line[i];
                var stringEnd = FindStringEnd(line, i + 1, q);
                sb.Append("<span class=\"tok-string\">")
                  .Append(HtmlEncoder.Default.Encode(line[i..stringEnd]))
                  .Append("</span>");
                i = stringEnd;
                continue;
            }

            if (char.IsLetter(line[i]) || line[i] == '-')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-' || line[i] == '_')) i++;
                var word = line[start..i];
                if (ShellKeywords.Contains(word))
                {
                    sb.Append("<span class=\"tok-keyword\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else if (word.StartsWith('-'))
                {
                    sb.Append("<span class=\"tok-type\">")
                      .Append(HtmlEncoder.Default.Encode(word))
                      .Append("</span>");
                }
                else
                {
                    sb.Append(HtmlEncoder.Default.Encode(word));
                }
                continue;
            }

            char c = line[i];
            if ("|&;<>()${}".Contains(c))
            {
                sb.Append("<span class=\"tok-punct\">")
                  .Append(HtmlEncoder.Default.Encode(c.ToString()))
                  .Append("</span>");
                i++;
                continue;
            }

            sb.Append(HtmlEncoder.Default.Encode(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TokenizeMarkdown(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            return "<span class=\"tok-keyword\">" + HtmlEncoder.Default.Encode(line) + "</span>";
        }
        if (trimmed.StartsWith("```") || trimmed.StartsWith("---"))
        {
            return "<span class=\"tok-punct\">" + HtmlEncoder.Default.Encode(line) + "</span>";
        }
        return HtmlEncoder.Default.Encode(line);
    }

    private static int FindStringEnd(string line, int start, char quote)
    {
        int i = start;
        int len = line.Length;
        while (i < len)
        {
            if (line[i] == '\\')
            {
                i += 2; // skip escaped character
                continue;
            }
            if (line[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return len;
    }
}
