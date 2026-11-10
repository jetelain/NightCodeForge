using System.Globalization;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation and formatting helper for task and conversation estimated cost telemetry.
/// Keeps Razor components purely focused on layout and user interaction.
/// </summary>
public static class TaskCostPresentationHelper
{
    private const double MinimumDisplayableCost = 0.001;

    /// <summary>
    /// Computes the authoritative task cost summary across all execution records and retried attempts.
    /// </summary>
    public static TaskCostSummary ComputeTaskCost(ForgeTask? task)
    {
        if (task?.ExecutionRecords == null || task.ExecutionRecords.Count == 0)
        {
            return TaskCostSummary.Empty;
        }

        bool hasCostData = false;
        double totalCost = 0;
        double actualCloudCost = 0;
        double electricityCost = 0;
        double cloudSavings = 0;
        string? currency = null;

        foreach (var r in task.ExecutionRecords)
        {
            if (r.Cost.HasValue)
            {
                hasCostData = true;
                totalCost += Math.Max(0.0, r.Cost.Value);
            }
            else if (r.ActualCloudCost.HasValue || r.ElectricityCost.HasValue)
            {
                hasCostData = true;
                totalCost += Math.Max(0.0, (r.ActualCloudCost ?? 0) + (r.ElectricityCost ?? 0));
            }

            if (r.ActualCloudCost.HasValue)
            {
                actualCloudCost += Math.Max(0.0, r.ActualCloudCost.Value);
            }

            if (r.ElectricityCost.HasValue)
            {
                electricityCost += Math.Max(0.0, r.ElectricityCost.Value);
            }

            if (r.CloudSavings.HasValue)
            {
                cloudSavings += Math.Max(0.0, r.CloudSavings.Value);
            }

            if (string.IsNullOrWhiteSpace(currency) && !string.IsNullOrWhiteSpace(r.Currency))
            {
                currency = r.Currency;
            }
        }

        if (!hasCostData)
        {
            return TaskCostSummary.Empty;
        }

        return new TaskCostSummary(
            TotalCost: Math.Round(totalCost, 6),
            ActualCloudCost: Math.Round(actualCloudCost, 6),
            ElectricityCost: Math.Round(electricityCost, 6),
            CloudSavings: Math.Round(cloudSavings, 6),
            Currency: currency ?? TaskCostSummary.DefaultCurrency,
            HasCostData: true);
    }

    /// <summary>
    /// Maps a conversation's accumulated cost telemetry into a TaskCostSummary.
    /// </summary>
    public static TaskCostSummary ComputeConversationCost(Conversation? conversation)
    {
        if (conversation == null || (!conversation.Cost.HasValue && !conversation.ActualCloudCost.HasValue && !conversation.ElectricityCost.HasValue))
        {
            return TaskCostSummary.Empty;
        }

        var total = conversation.Cost ?? ((conversation.ActualCloudCost ?? 0) + (conversation.ElectricityCost ?? 0));

        return new TaskCostSummary(
            TotalCost: Math.Round(Math.Max(0.0, total), 6),
            ActualCloudCost: Math.Round(Math.Max(0.0, conversation.ActualCloudCost ?? 0), 6),
            ElectricityCost: Math.Round(Math.Max(0.0, conversation.ElectricityCost ?? 0), 6),
            CloudSavings: Math.Round(Math.Max(0.0, conversation.CloudSavings ?? 0), 6),
            Currency: string.IsNullOrWhiteSpace(conversation.Currency) ? TaskCostSummary.DefaultCurrency : conversation.Currency,
            HasCostData: true);
    }

    /// <summary>
    /// Maps a session usage delta into a TaskCostSummary.
    /// </summary>
    public static TaskCostSummary ComputeUsageDeltaCost(SessionUsageDelta? delta)
    {
        if (delta == null || (!delta.Cost.HasValue && !delta.ActualCloudCost.HasValue && !delta.ElectricityCost.HasValue))
        {
            return TaskCostSummary.Empty;
        }

        var total = delta.Cost ?? ((delta.ActualCloudCost ?? 0) + (delta.ElectricityCost ?? 0));

        return new TaskCostSummary(
            TotalCost: Math.Round(Math.Max(0.0, total), 6),
            ActualCloudCost: Math.Round(Math.Max(0.0, delta.ActualCloudCost ?? 0), 6),
            ElectricityCost: Math.Round(Math.Max(0.0, delta.ElectricityCost ?? 0), 6),
            CloudSavings: Math.Round(Math.Max(0.0, delta.CloudSavings ?? 0), 6),
            Currency: string.IsNullOrWhiteSpace(delta.Currency) ? TaskCostSummary.DefaultCurrency : delta.Currency,
            HasCostData: true);
    }

    /// <summary>
    /// Accumulates an incremental session usage delta into an existing TaskCostSummary.
    /// Avoids UI telemetry regression to turn-level deltas during multi-turn streaming completion.
    /// </summary>
    public static TaskCostSummary AccumulateCost(TaskCostSummary current, SessionUsageDelta? delta)
    {
        if (delta == null || (!delta.Cost.HasValue && !delta.ActualCloudCost.HasValue && !delta.ElectricityCost.HasValue))
        {
            return current;
        }

        if (!current.HasCostData)
        {
            return ComputeUsageDeltaCost(delta);
        }

        var deltaTotal = delta.Cost ?? ((delta.ActualCloudCost ?? 0) + (delta.ElectricityCost ?? 0));

        return new TaskCostSummary(
            TotalCost: Math.Round(current.TotalCost + Math.Max(0.0, deltaTotal), 6),
            ActualCloudCost: Math.Round(current.ActualCloudCost + Math.Max(0.0, delta.ActualCloudCost ?? 0), 6),
            ElectricityCost: Math.Round(current.ElectricityCost + Math.Max(0.0, delta.ElectricityCost ?? 0), 6),
            CloudSavings: Math.Round(current.CloudSavings + Math.Max(0.0, delta.CloudSavings ?? 0), 6),
            Currency: string.IsNullOrWhiteSpace(delta.Currency) ? current.Currency : delta.Currency,
            HasCostData: true);
    }

    /// <summary>
    /// Formats a cost number with currency and decimal precision (e.g. "$0.042" or "&lt; $0.001" if positive but under 0.001).
    /// </summary>
    public static string FormatCost(double? cost, string? currency = TaskCostSummary.DefaultCurrency)
    {
        if (!cost.HasValue || double.IsNaN(cost.Value) || double.IsInfinity(cost.Value))
        {
            return string.Empty;
        }

        var sym = string.IsNullOrWhiteSpace(currency) ? TaskCostSummary.DefaultCurrency : currency;
        var val = cost.Value;

        if (val > 0 && val < MinimumDisplayableCost)
        {
            return $"< {sym}0.001";
        }

        return $"{sym}{val.ToString("F3", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Builds a user-friendly tooltip breakdown of estimated costs.
    /// </summary>
    public static string BuildCostBreakdownTooltip(TaskCostSummary summary)
    {
        if (!summary.HasCostData)
        {
            return string.Empty;
        }

        var total = FormatCost(summary.TotalCost, summary.Currency);
        var cloud = FormatCost(summary.ActualCloudCost, summary.Currency);
        var elec = FormatCost(summary.ElectricityCost, summary.Currency);
        var savings = FormatCost(summary.CloudSavings, summary.Currency);

        return $"Estimated Spend: {total} (Cloud: {cloud} | Electricity: {elec} | Cloud Savings: {savings})";
    }

    /// <summary>
    /// Formats an individual stage execution record's cost for timeline pills.
    /// </summary>
    public static string FormatStageCost(StageExecutionRecord? record)
    {
        if (record?.Cost == null)
        {
            return string.Empty;
        }

        return FormatCost(record.Cost, record.Currency);
    }

    /// <summary>
    /// Builds a user-friendly tooltip breakdown for an individual stage execution record.
    /// </summary>
    public static string BuildStageCostTooltip(StageExecutionRecord? record)
    {
        if (record?.Cost == null)
        {
            return string.Empty;
        }

        var total = FormatCost(record.Cost, record.Currency);
        var cloud = FormatCost(record.ActualCloudCost ?? 0, record.Currency);
        var elec = FormatCost(record.ElectricityCost ?? 0, record.Currency);
        var savings = FormatCost(record.CloudSavings ?? 0, record.Currency);

        return $"Stage Spend: {total} (Cloud: {cloud} | Electricity: {elec} | Cloud Savings: {savings})";
    }
}
