using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Hosting;

/// <summary>
/// Configuration parameters used to assemble the DB-free DI container for <c>ncf-bench</c>.
/// </summary>
public sealed record BenchmarkCliHostConfig(
    LlmProviderType ProviderType,
    string Endpoint,
    string? ApiKey,
    IReadOnlyList<string> AllowedOpenAiModels,
    string? BenchmarkRootDirectory);
