namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Authoritative roll-up cost metrics for a task or conversation.
/// </summary>
public sealed record TaskCostSummary(
    double TotalCost,
    double ActualCloudCost,
    double ElectricityCost,
    double CloudSavings,
    string Currency,
    bool HasCostData)
{
    public const string DefaultCurrency = "$";

    public static readonly TaskCostSummary Empty = new(0, 0, 0, 0, DefaultCurrency, HasCostData: false);
}

