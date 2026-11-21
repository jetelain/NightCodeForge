namespace NightCodeForge.Installer.Tests;

using System.Reflection;
using System.Runtime.CompilerServices;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class InstallerCliParserTests
{
    [Fact]
    public void Parse_UninstallFlags_MapsOptionsAccurately()
    {
        var opts = InstallerCliParser.Parse(
        [
            "uninstall",
            "--keep-data",
            "--remove-tools",
            "--remove-ollamarouter",
            "--quiet",
            "--install-dir",
            @"C:\Custom\NCF",
            "--wsl-distro=Ubuntu-24.04"
        ]);

        Assert.Equal(InstallerLaunchMode.Uninstall, opts.Mode);
        Assert.Equal(new UninstallOptions(KeepData: true, RemoveCompanionTools: true, RemoveOllamaRouter: true, Quiet: true), opts.UninstallOptions);
        Assert.Equal(@"C:\Custom\NCF", opts.InstallDirectory);
        Assert.Equal("Ubuntu-24.04", opts.WslDistroName);
    }

    [Fact]
    public void Parse_BundleAndAliases_AreHandled()
    {
        var bundleOpts = InstallerCliParser.Parse(["--bundle", @"D:\offline\bundle", "--wizard"]);
        Assert.Equal(InstallerLaunchMode.Wizard, bundleOpts.Mode);
        Assert.True(bundleOpts.AirGappedRequested);
        Assert.Equal(@"D:\offline\bundle", bundleOpts.BundleDirectory);

        var uninstallAlias = InstallerCliParser.Parse(["--uninstall"]);
        Assert.Equal(InstallerLaunchMode.Uninstall, uninstallAlias.Mode);

        var upgradeVerb = InstallerCliParser.Parse(["upgrade", "--air-gapped"]);
        Assert.Equal(InstallerLaunchMode.Upgrade, upgradeVerb.Mode);
        Assert.True(upgradeVerb.AirGappedRequested);
    }

    [Fact]
    public void Parse_UnknownOrIncompleteFlag_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--bundle"]));
    }

    [Fact]
    public void Parse_EmptyInlineOptionValues_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--bundle="]));
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--bundle=   "]));
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--install-dir="]));
        Assert.Throws<ArgumentException>(() => InstallerCliParser.Parse(["--wsl-distro="]));
    }
}

public sealed class InstallerArchitectureTests
{
    private const int MaxConstructorParameters = 10;

    [Fact]
    public void InstallerServices_DoNotExceedConstructorCeilingOrUseOptionalConstructorParameters()
    {
        var assembly = typeof(EnvironmentProvisioner).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                continue;
            }

            if (type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null)
            {
                continue;
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length > MaxConstructorParameters)
                {
                    violations.Add($"{type.FullName} constructor has {parameters.Length} parameters (max {MaxConstructorParameters}).");
                }

                foreach (var param in parameters)
                {
                    if (param.IsOptional)
                    {
                        violations.Add($"{type.FullName} constructor parameter '{param.Name}' must not be optional.");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void InstallerAssembly_DoesNotReferenceInfrastructureCoreOrNewtonsoft_AndContainerConstructsHandoffServices()
    {
        var assembly = typeof(EnvironmentProvisioner).Assembly;
        var referencedNames = assembly.GetReferencedAssemblies()
            .Select(static a => a.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("NightCodeForge.Infrastructure", referencedNames);
        Assert.DoesNotContain("NightCodeForge.Core", referencedNames);
        Assert.DoesNotContain("Newtonsoft.Json", referencedNames);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", referencedNames);
        Assert.DoesNotContain("Hangfire.Core", referencedNames);

        using var container = InstallerServiceContainer.CreateDefault();
        Assert.NotNull(container.OllamaRouterRecommendationSync);
        Assert.NotNull(container.OllamaRouterSetupHandoff);
    }
}
