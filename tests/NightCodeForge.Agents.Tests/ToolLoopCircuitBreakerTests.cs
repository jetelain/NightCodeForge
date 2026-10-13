using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class ToolLoopCircuitBreakerTests
{
    [Fact]
    public void DuplicateToolInvocation_OnUnchangedWorkingTree_IsBlockedWithSteeringMessage()
    {
        var breaker = new ToolLoopCircuitBreaker();
        var treeSignature = "clean-status-sha1";

        // Turn 1: Initial call
        var first = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, treeSignature);
        Assert.False(first.IsBlocked);

        // Turn 2: Duplicate call with same args on same tree status
        var second = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, treeSignature);
        Assert.True(second.IsBlocked);
        Assert.NotNull(second.CorrectiveMessage);
        Assert.Contains("⚠️ System Circuit Breaker: You executed `read` with identical parameters", second.CorrectiveMessage);
    }

    [Fact]
    public void ToolInvocation_OnChangedWorkingTree_IsNotBlocked()
    {
        var breaker = new ToolLoopCircuitBreaker();

        var first = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, "tree-1");
        Assert.False(first.IsBlocked);

        // Tree changed (e.g. file was edited or branch modified)
        var second = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, "tree-2");
        Assert.False(second.IsBlocked);
    }

    [Fact]
    public void ToolInvocation_WhenOptedOut_AllowsConsecutiveDuplicates()
    {
        var flags = new LocalOptimizationFlags { LoopCircuitBreaker = false };
        var breaker = new ToolLoopCircuitBreaker(flags);
        var tree = "tree-1";

        var first = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, tree);
        var second = breaker.EvaluateToolInvocation("read", new { path = "src/Program.cs" }, tree);

        Assert.False(first.IsBlocked);
        Assert.False(second.IsBlocked);
    }

    [Fact]
    public void DetectStreamingRepetition_DetectsRepeatingNgram()
    {
        var breaker = new ToolLoopCircuitBreaker();
        var ngram = "repeat-phrase-1234567890"; // > 16 chars
        var streamText = "Starting code... " + ngram + ngram + ngram;

        var detected = breaker.DetectStreamingRepetition(streamText);
        Assert.True(detected);
    }

    [Fact]
    public void DetectStreamingRepetition_NormalText_DoesNotTrigger()
    {
        var breaker = new ToolLoopCircuitBreaker();
        var streamText = "This is a normal streaming response with distinct words and no degenerate cycle.";

        var detected = breaker.DetectStreamingRepetition(streamText);
        Assert.False(detected);
    }
}
