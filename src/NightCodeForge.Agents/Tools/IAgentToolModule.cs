using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable module that encapsulates a cohesive group of agent tools and produces their <see cref="AIFunction"/> schemas and handlers.
/// </summary>
public interface IAgentToolModule
{
    /// <summary>
    /// Error message thrown when a schema-only <see cref="AIFunction"/> created without a bound module instance is invoked.
    /// </summary>
    public const string MissingBoundModuleInstanceMessage = "CanonicalTools instance is not bound to this schema-only AIFunction.";

    /// <summary>
    /// Optional turn-interruption controller attached to this module when running in turn-unwinding mode.
    /// </summary>
    IToolTurnInterruptionController? InterruptionController
    {
        get => null;
        set { }
    }

    /// <summary>
    /// Creates the <see cref="AIFunction"/> instances exposed by this module.
    /// </summary>
    IReadOnlyList<AIFunction> CreateFunctions();
}

