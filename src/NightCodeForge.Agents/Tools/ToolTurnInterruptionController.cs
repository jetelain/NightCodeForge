using System.Diagnostics.CodeAnalysis;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Extensible base record representing a structured terminal tool interruption that immediately unwinds
/// the active LLM tool-invoking loop without burning an extra model inference turn.
/// </summary>
public abstract record ToolTurnInterruption;

/// <summary>
/// Terminal tool interruption raised when an agent calls <c>ask</c> in a non-interactive pipeline stage.
/// </summary>
public sealed record HumanClarificationInterruption(IReadOnlyList<Question> Questions) : ToolTurnInterruption;

/// <summary>
/// Terminal tool interruption raised when a reviewer agent calls <c>request_changes</c>.
/// </summary>
public sealed record ReviewerChangesRequestedInterruption(
    string Feedback,
    string? Reason = null,
    string TargetStageId = StandardStageIds.Implementation) : ToolTurnInterruption;

/// <summary>
/// Coordinates structured terminal tool interruptions (<c>ask</c>, <c>request_changes</c>, Nightwatch terminal tools)
/// by capturing the strongly-typed <see cref="ToolTurnInterruption"/> payload and cancelling the linked turn token.
/// </summary>
public interface IToolTurnInterruptionController : IDisposable
{
    /// <summary>
    /// Token passed to the underlying agent streaming/execution loop that cancels immediately upon a terminal tool interruption.
    /// </summary>
    CancellationToken InterruptionToken { get; }

    /// <summary>
    /// The captured terminal tool interruption, or <c>null</c> if no terminal tool interruption occurred.
    /// </summary>
    ToolTurnInterruption? CapturedInterruption { get; }

    /// <summary>
    /// Returns <c>true</c> when a terminal tool interruption has been captured.
    /// </summary>
    bool HasInterruption { get; }

    /// <summary>
    /// Records <paramref name="interruption"/>, cancels <see cref="InterruptionToken"/>, and throws
    /// <see cref="OperationCanceledException"/> so <c>FunctionInvokingChatClient</c> unwinds immediately.
    /// </summary>
    [DoesNotReturn]
    void TriggerTerminalInterruption(ToolTurnInterruption interruption);

    /// <summary>
    /// Cancels <see cref="InterruptionToken"/> without recording a tool interruption (e.g. on host pause or abort).
    /// </summary>
    void Cancel();
}

/// <inheritdoc />
public sealed class ToolTurnInterruptionController : IToolTurnInterruptionController
{
    private readonly CancellationTokenSource _interruptionCts;
    private readonly CancellationToken _interruptionToken;
    private readonly Action? _onDispose;
    private bool _disposed;

    public ToolTurnInterruptionController()
    {
        _interruptionCts = new CancellationTokenSource();
        _interruptionToken = _interruptionCts.Token;
    }

    public ToolTurnInterruptionController(CancellationToken externalCancellationToken)
        : this(externalCancellationToken, onDispose: null)
    {
    }

    internal ToolTurnInterruptionController(CancellationToken externalCancellationToken, Action? onDispose)
    {
        _interruptionCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        _interruptionToken = _interruptionCts.Token;
        _onDispose = onDispose;
    }

    public CancellationToken InterruptionToken => _interruptionToken;

    public ToolTurnInterruption? CapturedInterruption { get; private set; }

    public bool HasInterruption => CapturedInterruption != null;

    [DoesNotReturn]
    public void TriggerTerminalInterruption(ToolTurnInterruption interruption)
    {
        ArgumentNullException.ThrowIfNull(interruption);
        CapturedInterruption ??= interruption;
        Cancel();
        throw new OperationCanceledException(_interruptionToken);
    }

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _interruptionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Justification: CTS may already be disposed if cancellation races with session teardown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _interruptionCts.Dispose();
        }
        finally
        {
            _onDispose?.Invoke();
        }
    }
}

