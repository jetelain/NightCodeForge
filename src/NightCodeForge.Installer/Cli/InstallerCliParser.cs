namespace NightCodeForge.Installer.Cli;

using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

public enum InstallerLaunchMode
{
    DefaultAuto,
    Wizard,
    Upgrade,
    Uninstall,
    Help
}

public sealed record InstallerCliOptions(
    InstallerLaunchMode Mode,
    UninstallOptions UninstallOptions,
    bool AirGappedRequested,
    string? BundleDirectory,
    string? InstallDirectory,
    string WslDistroName);

/// <summary>
/// Deterministic command-line parser for <c>NightCodeForge.Installer</c> (<c>uninstall</c>,
/// <c>upgrade</c>, <c>--wizard</c>, <c>--air-gapped</c>, <c>--bundle</c>, and uninstall flags).
/// </summary>
public static class InstallerCliParser
{
    public const string VerbUninstall = "uninstall";
    public const string FlagUninstall = "--uninstall";
    public const string VerbUpgrade = "upgrade";
    public const string FlagUpgrade = "--upgrade";
    public const string FlagWizard = "--wizard";
    public const string FlagKeepData = "--keep-data";
    public const string FlagRemoveTools = "--remove-tools";
    public const string FlagRemoveOllamaRouter = "--remove-ollamarouter";
    public const string FlagQuiet = "--quiet";
    public const string FlagAirGapped = "--air-gapped";
    public const string FlagBundle = "--bundle";
    public const string FlagInstallDir = "--install-dir";
    public const string FlagWslDistro = "--wsl-distro";
    public const string FlagHelp = "--help";
    public const string FlagHelpShort = "-h";

    public static InstallerCliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = InstallerLaunchMode.DefaultAuto;
        var keepData = false;
        var removeTools = false;
        var removeOllamaRouter = false;
        var quiet = false;
        var airGapped = false;
        string? bundleDir = null;
        string? installDir = null;
        var wslDistro = InstallerConstants.DefaultWslDistro;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (EqualsIgnoreCase(arg, FlagHelp) || EqualsIgnoreCase(arg, FlagHelpShort))
            {
                mode = InstallerLaunchMode.Help;
            }
            else if (EqualsIgnoreCase(arg, VerbUninstall) || EqualsIgnoreCase(arg, FlagUninstall))
            {
                mode = InstallerLaunchMode.Uninstall;
            }
            else if (EqualsIgnoreCase(arg, VerbUpgrade) || EqualsIgnoreCase(arg, FlagUpgrade))
            {
                mode = InstallerLaunchMode.Upgrade;
            }
            else if (EqualsIgnoreCase(arg, FlagWizard))
            {
                mode = InstallerLaunchMode.Wizard;
            }
            else if (EqualsIgnoreCase(arg, FlagKeepData))
            {
                keepData = true;
            }
            else if (EqualsIgnoreCase(arg, FlagRemoveTools))
            {
                removeTools = true;
            }
            else if (EqualsIgnoreCase(arg, FlagRemoveOllamaRouter))
            {
                removeOllamaRouter = true;
            }
            else if (EqualsIgnoreCase(arg, FlagQuiet))
            {
                quiet = true;
            }
            else if (EqualsIgnoreCase(arg, FlagAirGapped))
            {
                airGapped = true;
            }
            else if (EqualsIgnoreCase(arg, FlagBundle))
            {
                bundleDir = ReadOptionValue(args, ref i, FlagBundle);
                airGapped = true;
            }
            else if (arg.StartsWith(FlagBundle + "=", StringComparison.OrdinalIgnoreCase))
            {
                bundleDir = ReadInlineOptionValue(arg, FlagBundle);
                airGapped = true;
            }
            else if (EqualsIgnoreCase(arg, FlagInstallDir))
            {
                installDir = ReadOptionValue(args, ref i, FlagInstallDir);
            }
            else if (arg.StartsWith(FlagInstallDir + "=", StringComparison.OrdinalIgnoreCase))
            {
                installDir = ReadInlineOptionValue(arg, FlagInstallDir);
            }
            else if (EqualsIgnoreCase(arg, FlagWslDistro))
            {
                wslDistro = ReadOptionValue(args, ref i, FlagWslDistro);
            }
            else if (arg.StartsWith(FlagWslDistro + "=", StringComparison.OrdinalIgnoreCase))
            {
                wslDistro = ReadInlineOptionValue(arg, FlagWslDistro);
            }
            else
            {
                throw new ArgumentException($"Unknown argument '{arg}'. Run with '--help' for usage.");
            }
        }

        return new InstallerCliOptions(
            Mode: mode,
            UninstallOptions: new UninstallOptions(keepData, removeTools, removeOllamaRouter, quiet),
            AirGappedRequested: airGapped,
            BundleDirectory: string.IsNullOrWhiteSpace(bundleDir) ? null : bundleDir.Trim(),
            InstallDirectory: string.IsNullOrWhiteSpace(installDir) ? null : installDir.Trim(),
            WslDistroName: string.IsNullOrWhiteSpace(wslDistro) ? InstallerConstants.DefaultWslDistro : wslDistro.Trim());
    }

    private static string ReadOptionValue(string[] args, ref int index, string flagName)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Option '{flagName}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ReadInlineOptionValue(string arg, string flagName)
    {
        var value = arg[(flagName.Length + 1)..];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '{flagName}' requires a value.");
        }

        return value;
    }

    private static bool EqualsIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
