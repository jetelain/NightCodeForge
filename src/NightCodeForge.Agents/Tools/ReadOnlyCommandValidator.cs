using System.Text;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Validates shell commands executed by agents in ReadOnly mode (e.g., Ideation and Untrusted Triage).
/// Allows read-only discovery, inspection, and searching while strictly blocking modifications, file writes,
/// redirects, execution wrappers, and mutating git commands.
/// </summary>
public static class ReadOnlyCommandValidator
{
    private static readonly HashSet<string> AllowedBaseCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Directory & File discovery
        "find", "fd", "locate", "which", "where", "whereis", "tree", "ls", "dir", "pwd",
        
        // Content searching & extraction
        "rg", "ripgrep", "grep", "egrep", "fgrep", "awk",
        "select-string", "get-childitem", "gci",
        
        // Inspection & Viewing
        "cat", "head", "tail", "wc", "file", "stat", "nl", "sort", "uniq", "cut", "tr", "diff", "cmp", "less", "more",
        "echo", "printf", "true", "false", "test",
        
        // Git (subcommands validated separately)
        "git"
    };

    private static readonly HashSet<string> AllowedGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "tag", "rev-parse",
        "ls-files", "ls-tree", "grep", "describe", "shortlog", "blame", "remote", "cat-file", "check-ref-format"
    };

    private static readonly HashSet<string> MutatingGitBranchOrTagFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy"
    };

    public static (bool IsAllowed, string? ErrorMessage) ValidateCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (true, null);
        }

        // Check for disallowed characters outside quotes
        // 1. Redirections: > or >>
        // 2. Subshell / command substitution: $( or `
        var validationError = CheckUnquotedDisallowedPatterns(command);
        if (validationError != null)
        {
            return (false, validationError);
        }

        // Split into pipeline / command segments separated by |, &&, ||, ;
        var segments = SplitCommandSegments(command);
        if (segments.Count == 0)
        {
            return (true, null);
        }

        foreach (var segment in segments)
        {
            var trimmedSegment = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmedSegment)) continue;

            var tokens = Tokenize(trimmedSegment);
            if (tokens.Count == 0) continue;

            var rawCmd = tokens[0];
            // Normalize command name (e.g. "/usr/bin/find" -> "find", "git.exe" -> "git")
            var cmdName = GetExecutableName(rawCmd);

            if (!AllowedBaseCommands.Contains(cmdName))
            {
                return (false, $"Command '{cmdName}' is not permitted in read-only exploration mode. Only read-only exploration commands (such as find, grep, rg, ls, tree, pwd, cat, head, tail, wc, git status/log/diff/show, etc.) are allowed.");
            }

            if (string.Equals(cmdName, "git", StringComparison.OrdinalIgnoreCase))
            {
                var (isGitAllowed, gitError) = ValidateGitCommand(tokens);
                if (!isGitAllowed)
                {
                    return (false, gitError);
                }
            }
        }

        return (true, null);
    }

    private static (bool IsAllowed, string? ErrorMessage) ValidateGitCommand(IReadOnlyList<string> tokens)
    {
        int i = 1;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token == "-C" || token == "-c" || token == "--exec-path")
            {
                i += 2; // skip flag and argument
                continue;
            }
            if (token.StartsWith("-"))
            {
                i++; // skip other global flags like --no-pager
                continue;
            }
            break;
        }

        if (i >= tokens.Count)
        {
            // e.g. "git" or "git --version" or "git --help"
            return (true, null);
        }

        var subcommand = tokens[i].ToLowerInvariant();
        if (!AllowedGitSubcommands.Contains(subcommand))
        {
            return (false, $"Git subcommand '{subcommand}' is not permitted in read-only exploration mode. Allowed git subcommands: status, log, diff, show, branch, tag, rev-parse, ls-files, ls-tree, grep, describe, shortlog, blame, remote, cat-file.");
        }

        if (subcommand == "branch" || subcommand == "tag")
        {
            // Disallow mutation flags: -d, -D, -m, -M, etc.
            for (int j = i + 1; j < tokens.Count; j++)
            {
                if (MutatingGitBranchOrTagFlags.Contains(tokens[j]))
                {
                    return (false, $"Git '{subcommand}' flag '{tokens[j]}' is not permitted in read-only exploration mode.");
                }
            }
        }

        return (true, null);
    }

    private static string? CheckUnquotedDisallowedPatterns(string command)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool isEscaped = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (c == '\\' && !inSingleQuote)
            {
                isEscaped = !isEscaped;
                continue;
            }

            if (!isEscaped)
            {
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                }
                else if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                }
                else if (!inSingleQuote && !inDoubleQuote)
                {
                    if (c == '>')
                    {
                        return "File redirection ('>' or '>>') is not permitted in read-only exploration mode.";
                    }
                    if (c == '`')
                    {
                        return "Command substitution ('`') is not permitted in read-only exploration mode.";
                    }
                    if (c == '$' && i + 1 < command.Length && command[i + 1] == '(')
                    {
                        return "Command substitution ('$(') is not permitted in read-only exploration mode.";
                    }
                }
            }

            isEscaped = false;
        }

        return null;
    }

    private static List<string> SplitCommandSegments(string command)
    {
        var segments = new List<string>();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool isEscaped = false;
        int start = 0;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (c == '\\' && !inSingleQuote)
            {
                isEscaped = !isEscaped;
                continue;
            }

            if (!isEscaped)
            {
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                }
                else if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                }
                else if (!inSingleQuote && !inDoubleQuote)
                {
                    if (c == ';')
                    {
                        segments.Add(command.Substring(start, i - start));
                        start = i + 1;
                    }
                    else if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                    {
                        segments.Add(command.Substring(start, i - start));
                        start = i + 2;
                        i++;
                    }
                    else if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
                    {
                        segments.Add(command.Substring(start, i - start));
                        start = i + 2;
                        i++;
                    }
                    else if (c == '|')
                    {
                        segments.Add(command.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }

            isEscaped = false;
        }

        if (start < command.Length)
        {
            segments.Add(command.Substring(start));
        }

        return segments;
    }

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool isEscaped = false;

        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];

            if (c == '\\' && !inSingleQuote)
            {
                isEscaped = !isEscaped;
                continue;
            }

            if (!isEscaped)
            {
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }
                if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }
            }

            sb.Append(c);
            isEscaped = false;
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    private static string GetExecutableName(string raw)
    {
        var clean = raw.Replace('\\', '/');
        var idx = clean.LastIndexOf('/');
        var name = idx >= 0 ? clean[(idx + 1)..] : clean;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name;
    }
}
