using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Crucible;

/// <summary>
/// Ambient <see cref="AsyncLocal{T}"/> scope allowing stateless runners to attach a per-instance
/// <see cref="LenientDispatchTelemetry"/> collector to <see cref="CrucibleAgentHarness"/> without
/// expanding the harness constructor beyond the AGENTS.md parameter ceiling.
/// </summary>
public static class CrucibleDispatchTelemetryScope
{
    private static readonly AsyncLocal<LenientDispatchTelemetry?> _current = new();

    public static LenientDispatchTelemetry? Current => _current.Value;

    public static IDisposable Begin(LenientDispatchTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var previous = _current.Value;
        _current.Value = telemetry;
        return new ScopeRestore(previous);
    }

    private sealed class ScopeRestore(LenientDispatchTelemetry? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
