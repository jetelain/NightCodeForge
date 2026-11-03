using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests.Architecture;

/// <summary>
/// Architecture guard enforcing the AGENTS.md rule that service constructors never exceed 10 parameters.
/// Tracks the 6 legacy classes scheduled for decomposition in Steps 11.38B–11.38D via a shrinking allowlist.
/// </summary>
public class ConstructorParameterCeilingTests
{
    public const int MaxConstructorParameters = 10;

    /// <summary>
    /// Shrinking allowlist of pre-existing classes exceeding <see cref="MaxConstructorParameters"/>.
    /// Steps 11.38B–11.38D must remove entries as each class is refactored down to &lt;= 10 parameters.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> LegacyConstructorDebtAllowlist =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TaskOrchestrator"] = 19,
            ["StartupRecoveryService"] = 11
        };

    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(TaskOrchestrator).Assembly,
        typeof(ExecutionEnvironmentProvider).Assembly,
        typeof(CrucibleAgentHarness).Assembly,
        typeof(DevQueueService).Assembly
    ];

    [Fact]
    public void AllInstanceConstructors_DoNotExceedParameterCeiling_OrAllowlistUpperBound()
    {
        var violations = FindViolations(GetCandidateProductionTypes(), LegacyConstructorDebtAllowlist);

        Assert.True(
            violations.Count == 0,
            $"Constructor parameter ceiling ({MaxConstructorParameters}) exceeded:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void LegacyConstructorDebtAllowlist_ContainsNoStaleOrAmbiguousEntries()
    {
        var candidateTypesByName = GetCandidateProductionTypes()
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var staleEntries = new List<string>();

        foreach (var (typeName, legacyMax) in LegacyConstructorDebtAllowlist)
        {
            if (!candidateTypesByName.TryGetValue(typeName, out var matchingTypes))
            {
                staleEntries.Add($"'{typeName}' no longer exists in production assemblies and must be removed from LegacyConstructorDebtAllowlist.");
                continue;
            }

            if (matchingTypes.Count > 1)
            {
                var fullNames = string.Join(", ", matchingTypes.Select(t => t.FullName));
                staleEntries.Add($"'{typeName}' matches multiple production types ({fullNames}); disambiguate or rename.");
                continue;
            }

            var actualMaxParams = matchingTypes[0]
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(c => c.GetParameters().Length)
                .DefaultIfEmpty(0)
                .Max();

            if (actualMaxParams <= MaxConstructorParameters)
            {
                staleEntries.Add(
                    $"'{typeName}' now has at most {actualMaxParams} constructor parameters (<= {MaxConstructorParameters}) and must be removed from LegacyConstructorDebtAllowlist.");
            }
            else if (actualMaxParams < legacyMax)
            {
                staleEntries.Add(
                    $"'{typeName}' reduced its constructor parameter count from {legacyMax} to {actualMaxParams}; tighten or remove its LegacyConstructorDebtAllowlist entry.");
            }
        }

        Assert.True(
            staleEntries.Count == 0,
            "LegacyConstructorDebtAllowlist has stale entries:\n" + string.Join("\n", staleEntries));
    }

    [Fact]
    public void FindViolations_DetectsPublicAndInternalConstructorsExceedingCeiling()
    {
        var emptyAllowlist = new Dictionary<string, int>(StringComparer.Ordinal);
        var sampleTypes = new[]
        {
            typeof(CompliantTenParamService),
            typeof(ViolatingElevenParamPublicService),
            typeof(ViolatingElevenParamInternalService)
        };

        var violations = FindViolations(sampleTypes, emptyAllowlist);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains(nameof(ViolatingElevenParamPublicService), StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains(nameof(ViolatingElevenParamInternalService), StringComparison.Ordinal));
    }

    private static List<string> FindViolations(
        IEnumerable<Type> candidateTypes,
        IReadOnlyDictionary<string, int> allowlist)
    {
        var violations = new List<string>();

        foreach (var type in candidateTypes)
        {
            var instanceConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var ctor in instanceConstructors)
            {
                var paramCount = ctor.GetParameters().Length;
                var allowedMax = allowlist.TryGetValue(type.Name, out var legacyMax)
                    ? legacyMax
                    : MaxConstructorParameters;

                if (paramCount > allowedMax)
                {
                    violations.Add(
                        $"{type.FullName} has a constructor with {paramCount} parameters (max allowed: {allowedMax}).");
                }
            }
        }

        return violations;
    }

    private static IEnumerable<Type> GetCandidateProductionTypes()
    {
        foreach (var assembly in ProductionAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                if (IsCompilerGenerated(type) || IsEfCoreMigrationOrSnapshot(type) || IsPositionalRecord(type))
                {
                    continue;
                }

                yield return type;
            }
        }
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
                current.Name.Contains('<', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEfCoreMigrationOrSnapshot(Type type) =>
        typeof(Migration).IsAssignableFrom(type) ||
        typeof(ModelSnapshot).IsAssignableFrom(type) ||
        (type.Namespace?.Contains(".Migrations", StringComparison.Ordinal) ?? false);

    private static bool IsPositionalRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) != null;

    private sealed class CompliantTenParamService
    {
        public CompliantTenParamService(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10)
        {
            _ = p1 + p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9 + p10;
        }
    }

    private sealed class ViolatingElevenParamPublicService
    {
        public ViolatingElevenParamPublicService(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11)
        {
            _ = p1 + p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9 + p10 + p11;
        }
    }

    private sealed class ViolatingElevenParamInternalService
    {
        internal ViolatingElevenParamInternalService(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11)
        {
            _ = p1 + p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9 + p10 + p11;
        }
    }
}
