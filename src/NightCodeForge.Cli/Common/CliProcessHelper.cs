namespace NightCodeForge.Cli.Common;

using NightCodeForge.Core.Interfaces;

public static class CliProcessHelper
{
    public static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (string.Empty, string.Empty);
        }

        if (trimmed.StartsWith('\"') || trimmed.StartsWith('\''))
        {
            var quote = trimmed[0];
            var closingQuote = trimmed.IndexOf(quote, 1);
            if (closingQuote > 0)
            {
                var fileName = trimmed.Substring(1, closingQuote - 1);
                var arguments = trimmed.Substring(closingQuote + 1).Trim();
                return (fileName, arguments);
            }
        }

        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed.Substring(0, spaceIndex), trimmed.Substring(spaceIndex + 1).Trim());
    }

    public static async Task<ProcessExecutionResult> RunCommandAsync(
        IProcessRunner runner,
        string commandLine,
        string workingDirectory,
        int timeoutSeconds = Constants.DefaultCliCommandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var (fileName, arguments) = SplitCommandLine(commandLine);
        var spec = new ProcessStartSpec
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        };

        return await runner.RunAsync(spec, cancellationToken);
    }
}
