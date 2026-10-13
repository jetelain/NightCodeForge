using System.Collections.Concurrent;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Thread-safe in-memory registry of available agent harnesses.
/// </summary>
public class HarnessRegistry : IHarnessRegistry
{
    private readonly ConcurrentDictionary<string, IAgentHarness> _harnesses = new(StringComparer.OrdinalIgnoreCase);

    public HarnessRegistry(IEnumerable<IAgentHarness>? initialHarnesses = null)
    {
        if (initialHarnesses != null)
        {
            foreach (var harness in initialHarnesses)
            {
                RegisterHarness(harness);
            }
        }
    }

    public void RegisterHarness(IAgentHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        _harnesses[harness.HarnessId] = harness;
    }

    public IAgentHarness GetHarness(string harnessId)
    {
        if (TryGetHarness(harnessId, out var harness) && harness != null)
        {
            return harness;
        }

        throw new KeyNotFoundException($"No agent harness registered with ID '{harnessId}'. Available harnesses: {string.Join(", ", _harnesses.Keys)}");
    }

    public bool TryGetHarness(string harnessId, out IAgentHarness? harness)
    {
        return _harnesses.TryGetValue(harnessId, out harness);
    }

    public IEnumerable<IAgentHarness> GetAllHarnesses()
    {
        return _harnesses.Values;
    }
}
