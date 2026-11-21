namespace NightCodeForge.Installer.Core.Constants;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Centralized constants for installer repositories, paths, binary names, OS registration,
/// provisioning commands, and timeouts.
/// </summary>
public static class InstallerConstants
{
    public const string NightCodeForgeRepository = "jetelain/NightCodeForge";
    public const string OllamaRouterRepository = "jetelain/OllamaRouter";
    public const string GitHubLatestReleaseUrlFormat = "https://api.github.com/repos/{0}/releases/latest";
    public const string HttpUserAgent = "NightCodeForge-Installer/1.0";

    public const string ComponentNightCodeForgeWeb = "NightCodeForge-Web";
    public const string ComponentOllamaRouter = "OllamaRouter";
    public const string Sha256SumsFileName = "SHA256SUMS.txt";
    public const string VersionFileName = "VERSION";
    public const string DefaultFallbackVersion = "1.0.0";
    public const string BundleDirectoryName = "bundle";
    public const string BackupDirectoryPrefix = ".backup-";
    public const string PartFileExtension = ".part";
    public const string LauncherConfigFileName = "launcher.conf";
    public const string UninstallManifestFileName = "uninstall-manifest.json";
    public const string UserConfigFileName = "config.json";

    public const string DefaultOllamaRouterEndpoint = "http://localhost:11434";
    public const string OllamaRouterRecommendationsApiPath = "/api/router/recommendations";
    public const int OllamaRouterRecommendationsTimeoutSeconds = 10;
    public const string LlmProviderSectionName = "LlmProvider";
    public const string LlmProviderTypeKey = "ProviderType";
    public const string LlmEndpointKey = "Endpoint";
    public const string LlmArchitectModelIdKey = "ArchitectModelId";
    public const string LlmCoderModelIdKey = "CoderModelId";
    public const string LlmDefaultModelIdKey = "DefaultModelId";
    public const string LlmLegacyModelIdKey = "ModelId";
    public const string LlmContextWindowTokensKey = "ContextWindowTokens";
    public const string OllamaRouterProviderTypeName = "OllamaRouter";
    public const int DefaultContextWindowTokens = 131072;

    public const string RoleBasic = "basic";
    public const string RoleAgenticChat = "agentic_chat";
    public const string RoleSoftwareArchitect = "software_architect";
    public const string RoleSeniorDeveloper = "senior_developer";
    public const string RoleJuniorDeveloper = "junior_developer";

    public const string OllamaRouterSetupCommand = "setup";
    public const string OllamaRouterWizardFlag = "--wizard";
    public const string OllamaRouterWaitFlag = "--wait";
    public const string OllamaRouterProtocolOpenUri = "ollamarouter://open";
    public const string OllamaRouterProtocolWizardUri = "ollamarouter://wizard";

    public const string ConfigDirectoryName = "config";
    public const string DataDirectoryName = "data";
    public const string LogsDirectoryName = "logs";
    public const string BinDirectoryName = "bin";
    public const string SkillsDirectoryName = "skills";
    public const string StateDirectoryName = "state";
    public const string WorkspacesDirectoryName = "wks";

    public const string WebExeWindows = "NightCodeForge.Web.exe";
    public const string WebExeUnix = "NightCodeForge.Web";
    public const string WebDll = "NightCodeForge.Web.dll";
    public const string LauncherShortWindows = "ncf-web.exe";
    public const string LauncherShortUnix = "ncf-web";
    public const string LauncherLongWindows = "nightcodeforge-web.exe";
    public const string LauncherLongUnix = "nightcodeforge-web";
    public const string InstallerExeWindows = "NightCodeForge.Installer.exe";
    public const string InstallerExeUnix = "nightcodeforge-installer";

    public const string OllamaRouterServerWindows = "ollamarouter-server.exe";
    public const string OllamaRouterServerUnix = "ollamarouter-server";
    public const string OllamaRouterCliWindows = "ollamarouter.exe";
    public const string OllamaRouterCliUnix = "ollamarouter";
    public const string OllamaRouterAppWindows = "ollamarouter-app.exe";
    public const string OllamaRouterAppUnix = "ollamarouter-app";

    public const string AppSettingsFileName = "appsettings.json";
    public const string AppSettingsProductionFileName = "appsettings.Production.json";
    public const string DefaultWebUrls = "http://localhost:5270";
    public const string MainDatabaseFileName = "nightcodeforge.db";
    public const string HangfireDatabaseFileName = "nightcodeforge_hangfire.db";

    public const string EnvNightCodeForgeHome = "NIGHTCODEFORGE_HOME";
    public const string EnvOllamaRouterHome = "OLLAMAROUTER_HOME";

    public const string PathBlockLabelNightCodeForge = "NightCodeForge";
    public const string PathBlockLabelOllamaRouter = "OllamaRouter";

    public const string WslExecutable = "wsl.exe";
    public const string DotNetExecutable = "dotnet";
    public const string CmdExecutable = "cmd.exe";
    public const string PowerShellExecutable = "powershell.exe";
    public const string DotNetCliUiLanguageEnvVar = "DOTNET_CLI_UI_LANGUAGE";
    public const string DotNetCliUiLanguageEnglish = "en";
    public const string WslUtf8EnvVar = "WSL_UTF8";
    public const string WslEnvVar = "WSLENV";

    public const string UnixNightCodeForgeDirName = ".nightcodeforge";
    public const string UnixOllamaRouterDirName = ".ollamarouter";
    public const string DownloadCacheDirectoryName = "ncf-installer-downloads";

    public const string WindowsUninstallRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NightCodeForge";
    public const string WindowsOllamaRouterProtocolSubKey = @"Software\Classes\ollamarouter";
    public const string ApplicationDisplayName = "NightCodeForge";
    public const string PublisherName = "Pmad";
    public const string OllamaRouterProtocolDisplayName = "URL:OllamaRouter Protocol";
    public const string OllamaRouterDesktopEntryFileName = "ollamarouter.desktop";
    public const string OllamaRouterMimeType = "x-scheme-handler/ollamarouter";
    public const string NightCodeForgeDesktopEntryFileName = "nightcodeforge.desktop";
    public const string NightCodeForgeInstallerDesktopEntryFileName = "nightcodeforge-installer.desktop";
    public const string MacOsAppBundleName = "NightCodeForge.app";
    public const string WindowsShortcutFileName = "NightCodeForge.lnk";

    public const string UnixPathBlockStartPrefix = "# >>> NightCodeForge Installer PATH (";
    public const string UnixPathBlockEndPrefix = "# <<< NightCodeForge Installer PATH (";
    public const string UnixPathBlockSuffix = ") >>>";
    public const string UnixPathBlockEndSuffix = ") <<<";

    public const string DefaultWslDistro = "Ubuntu";
    public const string DotNet10VersionPrefix = "10.";
    public const string WingetDotNet10PackageId = "Microsoft.DotNet.SDK.10";
    public const string DotNetInstallScriptUrl = "https://dot.net/v1/dotnet-install.ps1";

    public const int ExitCodeSuccess = 0;
    public const int ExitCodeError = 1;
    public const int ExitCodeRebootRequired = 3010;

    public const int DefaultWebPort = 5270;
    public const int DoctorProbeTimeoutSeconds = 5;
    public const string SqliteFileHeaderMagic = "SQLite format 3\0";
    public const int WizardStepCount = 5;

    public const string DoctorCheckWsl2 = "WSL2 Subsystem";
    public const string DoctorCheckDocker = "Docker Daemon";
    public const string DoctorCheckOllamaRouter = "OllamaRouter Endpoint";
    public const string DoctorCheckWebPort = "NightCodeForge Web (Port 5270)";
    public const string DoctorCheckCompanionTools = "Companion Global Tools";
    public const string DoctorCheckDatabase = "Database Health";

    public const string UiStatusWebRunning = "Running on port 5270";
    public const string UiStatusWebStopped = "Stopped";

    public const int OllamaRouterStopTimeoutSeconds = 30;
    public const int LauncherStopDelayMs = 300;
    public const int WebStopDelayMs = 500;
    public const int ProcessStopTimeoutSeconds = 5;
    public const int DeleteRetryAttempts = 6;
    public const int DeleteRetryDelayMs = 250;
    public const int XdgMimeTimeoutMs = 5000;
    public const int DefaultHttpTimeoutSeconds = 300;
    public const int DownloadBufferSizeBytes = 81920;
    public const int DeferredCleanupPingCount = 2;
    public const int Sha256HexLength = 64;
    public const double DownloadMaxProgressPercent = 38.0;

    public const string RuntimeWinX64 = "win-x64";
    public const string RuntimeWinArm64 = "win-arm64";
    public const string RuntimeLinuxX64 = "linux-x64";
    public const string RuntimeLinuxArm64 = "linux-arm64";
    public const string RuntimeOsxX64 = "osx-x64";
    public const string RuntimeOsxArm64 = "osx-arm64";

    public const string ZipExtension = ".zip";
    public const string TarGzExtension = ".tar.gz";
    public const string NupkgExtension = ".nupkg";

    public const string AlreadyInstalledMarker = "is already installed";
    public const string NotInstalledMarker = "is not installed";
    public const string NotFoundMarker = "not found";
    public const string WslDistroNotFoundMarker = "no distribution with the supplied name";
    public const string WslNoDistrosMarker = "no installed distributions";
    public const string WslDistroNotFoundErrorCode = "WSL_E_DISTRO_NOT_FOUND";
    public const string DotNetCommandNotFoundMarker = "dotnet: command not found";

    public static readonly string[] NightCodeForgeLauncherProcesses = ["ncf-web", "nightcodeforge-web"];
    public static readonly string[] NightCodeForgeWebProcesses = ["NightCodeForge.Web"];
    public static readonly string[] OllamaRouterProcesses = ["ollamarouter-server", "OllamaRouter", "ollamarouter-app"];

    public static readonly string[] NightCodeForgePreservedInstallItems =
    [
        ConfigDirectoryName,
        DataDirectoryName,
        LogsDirectoryName,
        WorkspacesDirectoryName,
        LauncherConfigFileName
    ];

    public static readonly string[] NightCodeForgeKeepDataItems =
    [
        ConfigDirectoryName,
        DataDirectoryName,
        LogsDirectoryName
    ];

    public static readonly string[] OllamaRouterPreservedItems =
    [
        ConfigDirectoryName,
        StateDirectoryName,
        LogsDirectoryName
    ];

    public static readonly string[] NightCodeForgeUnixExecutables =
    [
        WebExeUnix,
        LauncherLongUnix,
        InstallerExeUnix
    ];

    public static readonly string[] OllamaRouterUnixExecutables =
    [
        OllamaRouterServerUnix,
        OllamaRouterCliUnix,
        OllamaRouterAppUnix
    ];
}

/// <summary>
/// Canonical catalog of companion .NET global tools pre-installed by the NightCodeForge installer.
/// </summary>
public static class CompanionToolCatalog
{
    public const string CommandNcf = "ncf";
    public const string PackageNcf = "NightCodeForge.Cli";

    public const string CommandNightCodeAssay = "nightcodeassay";
    public const string PackageNightCodeAssay = "NightCodeForge.Assay";

    public const string CommandCSharpNav = "csnav";
    public const string PackageCSharpNav = "Pmad.CSharpNav";

    public const string CommandMarkdownFetch = "mdfetch";
    public const string PackageMarkdownFetch = "Pmad.MarkdownFetch";

    public const string CommandCodeCondenser = "codecondenser";
    public const string PackageCodeCondenser = "Pmad.CodeCondenser";

    public static readonly IReadOnlyList<CompanionNuGetToolSpec> All =
    [
        new CompanionNuGetToolSpec(
            CommandNcf,
            PackageNcf,
            InstallOnHost: true,
            InstallInWsl: true,
            Description: "Deterministic repository preparation, onboarding (ncf init), and readiness audit (ncf audit)."),
        new CompanionNuGetToolSpec(
            CommandNightCodeAssay,
            PackageNightCodeAssay,
            InstallOnHost: true,
            InstallInWsl: false,
            Description: "Zero-VRAM Avalonia exploratory testing companion desktop app."),
        new CompanionNuGetToolSpec(
            CommandCSharpNav,
            PackageCSharpNav,
            InstallOnHost: true,
            InstallInWsl: true,
            Description: "C# symbol navigation and BCL/NuGet assembly decompiler for agents."),
        new CompanionNuGetToolSpec(
            CommandMarkdownFetch,
            PackageMarkdownFetch,
            InstallOnHost: true,
            InstallInWsl: true,
            Description: "Web documentation to LLM-ready Markdown converter for agents."),
        new CompanionNuGetToolSpec(
            CommandCodeCondenser,
            PackageCodeCondenser,
            InstallOnHost: true,
            InstallInWsl: true,
            Description: "C# structural clone detection and comment condensation CLI for agents.")
    ];
}
