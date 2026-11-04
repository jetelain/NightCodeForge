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
/// Zero-exception enforcement after completing Steps 11.38B–11.38D.
/// </summary>
public class ConstructorParameterCeilingTests
{
    public const int MaxConstructorParameters = 10;

    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(TaskOrchestrator).Assembly,
        typeof(ExecutionEnvironmentProvider).Assembly,
        typeof(CrucibleAgentHarness).Assembly,
        typeof(DevQueueService).Assembly
    ];

    [Fact]
    public void AllInstanceConstructors_DoNotExceedParameterCeiling()
    {
        var violations = FindViolations(GetCandidateProductionTypes());

        Assert.True(
            violations.Count == 0,
            $"Constructor parameter ceiling ({MaxConstructorParameters}) exceeded:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void FindViolations_DetectsPublicAndInternalConstructorsExceedingCeiling()
    {
        var sampleTypes = new[]
        {
            typeof(CompliantTenParamService),
            typeof(ViolatingElevenParamPublicService),
            typeof(ViolatingElevenParamInternalService)
        };

        var violations = FindViolations(sampleTypes);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains(nameof(ViolatingElevenParamPublicService), StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains(nameof(ViolatingElevenParamInternalService), StringComparison.Ordinal));
    }

    private static List<string> FindViolations(IEnumerable<Type> candidateTypes)
    {
        var violations = new List<string>();

        foreach (var type in candidateTypes)
        {
            var instanceConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var ctor in instanceConstructors)
            {
                var paramCount = ctor.GetParameters().Length;
                if (paramCount > MaxConstructorParameters)
                {
                    violations.Add(
                        $"{type.FullName} has a constructor with {paramCount} parameters (max allowed: {MaxConstructorParameters}).");
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
