using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

public record LoopCheckResult(bool IsBlocked, string? CorrectiveMessage = null);

/// <summary>
/// Circuit breaker protecting against runaway token generation loops (n-gram repetitions)
/// and duplicate tool invocation ping-pong on unchanged working trees.
/// </summary>
public class ToolLoopCircuitBreaker
{
    private readonly LocalOptimizationFlags _flags;
    private string? _lastToolHash;
    private string? _lastWorkingTreeSignature;
    private int _consecutiveIdenticalCount = 0;

    public ToolLoopCircuitBreaker()
        : this(new LocalOptimizationFlags())
    {
    }

    public ToolLoopCircuitBreaker(LocalOptimizationFlags flags)
    {
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
    }

    /// <summary>
    /// Checks if a tool call is a duplicate invocation on an unchanged working tree.
    /// </summary>
    public LoopCheckResult EvaluateToolInvocation(string toolName, object? arguments, string? workingTreeSignature)
    {
        if (!_flags.LoopCircuitBreaker)
        {
            return new LoopCheckResult(false);
        }

        var argsJson = arguments != null ? JsonSerializer.Serialize(arguments) : "{}";
        var currentHash = ComputeHash($"{toolName}:{argsJson}");

        bool treeUnchanged = string.Equals(_lastWorkingTreeSignature, workingTreeSignature, StringComparison.Ordinal);
        bool sameCall = string.Equals(_lastToolHash, currentHash, StringComparison.Ordinal);

        if (sameCall && treeUnchanged && !string.IsNullOrEmpty(_lastToolHash))
        {
            _consecutiveIdenticalCount++;
            if (_consecutiveIdenticalCount >= 1)
            {
                var warning = $"⚠️ System Circuit Breaker: You executed `{toolName}` with identical parameters on an unchanged working tree. Cease repetition. Formulate your technical plan or apply your edits now.";
                return new LoopCheckResult(true, warning);
            }
        }
        else
        {
            _consecutiveIdenticalCount = 0;
        }

        _lastToolHash = currentHash;
        _lastWorkingTreeSignature = workingTreeSignature;
        return new LoopCheckResult(false);
    }

    /// <summary>
    /// Scans a text buffer for repeating n-grams (default: >= 16 chars repeated >= 3 times).
    /// </summary>
    public bool DetectStreamingRepetition(string text, int minNgramLength = 16, int minRepetitions = 3)
    {
        if (!_flags.LoopCircuitBreaker || string.IsNullOrEmpty(text) || text.Length < minNgramLength * minRepetitions)
        {
            return false;
        }

        int maxLen = Math.Min(text.Length / minRepetitions, 128);
        for (int len = minNgramLength; len <= maxLen; len++)
        {
            var pattern = text.Substring(text.Length - len);
            int count = 1;
            int checkIndex = text.Length - (2 * len);

            while (checkIndex >= 0 && text.Substring(checkIndex, len) == pattern)
            {
                count++;
                checkIndex -= len;
                if (count >= minRepetitions)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Reset()
    {
        _lastToolHash = null;
        _lastWorkingTreeSignature = null;
        _consecutiveIdenticalCount = 0;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
