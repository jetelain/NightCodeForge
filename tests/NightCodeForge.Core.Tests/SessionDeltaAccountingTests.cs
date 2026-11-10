using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class SessionDeltaAccountingTests
{
    [Fact]
    public void Compute_WhenFinalIsNull_ReturnsNull()
    {
        var delta = SessionUsageDelta.Compute(null, null);
        Assert.Null(delta);
    }

    [Fact]
    public void Compute_WithNullOrZeroBaseline_ReturnsFullFinalUsage()
    {
        var finalUsage = new RouterSessionUsage(
            SessionId: "session-1",
            Requests: 4,
            InputTokens: 1000,
            CachedInputTokens: 600,
            OutputTokens: 250,
            ElapsedMs: 1500.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.05,
            ElectricityCost: 0.002,
            Savings: 0.048,
            ActualCloudCost: 0.0,
            Cost: 0.002,
            Currency: "$",
            PerTarget: null);

        var delta = SessionUsageDelta.Compute(finalUsage, null);

        Assert.NotNull(delta);
        Assert.Equal(1000, delta.InputTokens);
        Assert.Equal(600, delta.CachedInputTokens);
        Assert.Equal(250, delta.OutputTokens);
        Assert.Equal(0.002, delta.Cost);
        Assert.Equal(0.0, delta.ActualCloudCost);
        Assert.Equal(0.002, delta.ElectricityCost);
        Assert.Equal(0.048, delta.CloudSavings);
        Assert.Equal("$", delta.Currency);
    }

    [Fact]
    public void Compute_WithExistingBaseline_SubtractsBaselineValuesAccurately()
    {
        var baseline = new RouterSessionUsage(
            SessionId: "session-1",
            Requests: 2,
            InputTokens: 500,
            CachedInputTokens: 300,
            OutputTokens: 100,
            ElapsedMs: 800.0,
            LastSeenAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            CloudValue: 0.02,
            ElectricityCost: 0.001,
            Savings: 0.019,
            ActualCloudCost: 0.0,
            Cost: 0.001,
            Currency: "$",
            PerTarget: null);

        var finalUsage = new RouterSessionUsage(
            SessionId: "session-1",
            Requests: 5,
            InputTokens: 1500,
            CachedInputTokens: 900,
            OutputTokens: 350,
            ElapsedMs: 2500.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.07,
            ElectricityCost: 0.0035,
            Savings: 0.0665,
            ActualCloudCost: 0.0005,
            Cost: 0.004,
            Currency: "$",
            PerTarget: null);

        var delta = SessionUsageDelta.Compute(finalUsage, baseline);

        Assert.NotNull(delta);
        Assert.Equal(1000, delta.InputTokens);
        Assert.Equal(600, delta.CachedInputTokens);
        Assert.Equal(250, delta.OutputTokens);
        Assert.Equal(0.003, delta.Cost!.Value, precision: 6);
        Assert.Equal(0.0005, delta.ActualCloudCost!.Value, precision: 6);
        Assert.Equal(0.0025, delta.ElectricityCost!.Value, precision: 6);
        Assert.Equal(0.0475, delta.CloudSavings!.Value, precision: 6);
        Assert.Equal("$", delta.Currency);
    }

    [Fact]
    public void Compute_WhenRouterPricingUnconfigured_PreservesNullOnCostFields()
    {
        var baseline = new RouterSessionUsage(
            SessionId: "session-unpriced",
            Requests: 1,
            InputTokens: 100,
            CachedInputTokens: 0,
            OutputTokens: 50,
            ElapsedMs: 200.0,
            LastSeenAt: null,
            CloudValue: null,
            ElectricityCost: null,
            Savings: null,
            ActualCloudCost: null,
            Cost: null,
            Currency: null,
            PerTarget: null);

        var finalUsage = new RouterSessionUsage(
            SessionId: "session-unpriced",
            Requests: 3,
            InputTokens: 600,
            CachedInputTokens: 200,
            OutputTokens: 150,
            ElapsedMs: 600.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: null,
            ElectricityCost: null,
            Savings: null,
            ActualCloudCost: null,
            Cost: null,
            Currency: null,
            PerTarget: null);

        var delta = SessionUsageDelta.Compute(finalUsage, baseline);

        Assert.NotNull(delta);
        Assert.Equal(500, delta.InputTokens);
        Assert.Equal(200, delta.CachedInputTokens);
        Assert.Equal(100, delta.OutputTokens);

        // Required: must remain null, never coerced to 0.0!
        Assert.Null(delta.Cost);
        Assert.Null(delta.ActualCloudCost);
        Assert.Null(delta.ElectricityCost);
        Assert.Null(delta.CloudSavings);
        Assert.Null(delta.Currency);
    }

    [Fact]
    public void Compute_ClampsNegativeDifferencesToZero()
    {
        var baseline = new RouterSessionUsage(
            SessionId: "session-1",
            Requests: 10,
            InputTokens: 5000,
            CachedInputTokens: 2000,
            OutputTokens: 1000,
            ElapsedMs: 8000.0,
            LastSeenAt: null,
            CloudValue: 0.1,
            ElectricityCost: 0.02,
            Savings: 0.08,
            ActualCloudCost: 0.01,
            Cost: 0.03,
            Currency: "$",
            PerTarget: null);

        // Simulating a router LRU reset where counters restarted
        var finalUsage = new RouterSessionUsage(
            SessionId: "session-1",
            Requests: 1,
            InputTokens: 500,
            CachedInputTokens: 200,
            OutputTokens: 100,
            ElapsedMs: 800.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.01,
            ElectricityCost: 0.002,
            Savings: 0.008,
            ActualCloudCost: 0.0,
            Cost: 0.002,
            Currency: "$",
            PerTarget: null);

        var delta = SessionUsageDelta.Compute(finalUsage, baseline);

        Assert.NotNull(delta);
        Assert.Equal(0, delta.InputTokens);
        Assert.Equal(0, delta.CachedInputTokens);
        Assert.Equal(0, delta.OutputTokens);
        Assert.Equal(0.0, delta.Cost);
        Assert.Equal(0.0, delta.ActualCloudCost);
        Assert.Equal(0.0, delta.ElectricityCost);
        Assert.Equal(0.0, delta.CloudSavings);
    }

    [Fact]
    public void ApplyTo_AccumulatesDeltasOntoConversationCorrectly()
    {
        var conv = new Conversation
        {
            Id = 42,
            InputTokens = 100,
            OutputTokens = 50,
            CachedInputTokens = 30,
            Cost = 0.01,
            ActualCloudCost = 0.005,
            ElectricityCost = 0.005,
            CloudSavings = 0.02,
            Currency = "$"
        };

        var delta = new SessionUsageDelta(
            InputTokens: 200,
            OutputTokens: 80,
            CachedInputTokens: 50,
            Cost: 0.02,
            ActualCloudCost: 0.01,
            ElectricityCost: 0.01,
            CloudSavings: 0.04,
            Currency: "$");

        delta.ApplyTo(conv);

        Assert.Equal(300, conv.InputTokens);
        Assert.Equal(130, conv.OutputTokens);
        Assert.Equal(80, conv.CachedInputTokens);
        Assert.Equal(0.03, conv.Cost!.Value, precision: 6);
        Assert.Equal(0.015, conv.ActualCloudCost!.Value, precision: 6);
        Assert.Equal(0.015, conv.ElectricityCost!.Value, precision: 6);
        Assert.Equal(0.06, conv.CloudSavings!.Value, precision: 6);
        Assert.Equal("$", conv.Currency);
    }

    [Fact]
    public void ApplyTo_WhenCostIsNull_PreservesNullOnConversationCosts()
    {
        var conv = new Conversation
        {
            Id = 43,
            InputTokens = null,
            OutputTokens = null,
            Cost = null
        };

        var delta = new SessionUsageDelta(
            InputTokens: 150,
            OutputTokens: 75,
            CachedInputTokens: 0,
            Cost: null,
            ActualCloudCost: null,
            ElectricityCost: null,
            CloudSavings: null,
            Currency: null);

        delta.ApplyTo(conv);

        Assert.Equal(150, conv.InputTokens);
        Assert.Equal(75, conv.OutputTokens);
        Assert.Null(conv.Cost);
        Assert.Null(conv.ActualCloudCost);
        Assert.Null(conv.ElectricityCost);
        Assert.Null(conv.CloudSavings);
    }
}
