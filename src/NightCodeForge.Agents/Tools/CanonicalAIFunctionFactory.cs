using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Assembles <see cref="AITool"/> instances from the modular <see cref="IAgentToolModule"/> implementations
/// for seamless integration with <see cref="FunctionInvokingChatClient"/> and <see cref="LenientToolDispatcher"/>.
/// </summary>
public static class CanonicalAIFunctionFactory
{
    /// <summary>
    /// Creates <see cref="AIFunction"/> instances for the canonical toolset and any additional <see cref="IAgentToolModule"/> modules,
    /// optionally filtered by a set of permitted tool names and registered with a <see cref="LenientToolDispatcher"/>.
    /// </summary>
    public static IList<AIFunction> CreateTools(
        CanonicalTools tools,
        IEnumerable<string>? permittedTools = null,
        LenientToolDispatcher? dispatcher = null,
        IReadOnlyList<IAgentToolModule>? additionalModules = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var allCanonicalFunctions = CreateAllCanonicalFunctions(tools);
        dispatcher?.RegisterTools(allCanonicalFunctions);

        HashSet<string>? permittedSet = permittedTools != null
            ? new HashSet<string>(permittedTools, StringComparer.OrdinalIgnoreCase)
            : null;

        var advertisedTools = new List<AIFunction>(allCanonicalFunctions.Count);
        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fn in allCanonicalFunctions)
        {
            if (string.Equals(fn.Name, CanonicalToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) &&
                !tools.IsWebSearchEnabled)
            {
                continue;
            }

            if (IsBacklogTool(fn.Name) && !tools.IsBacklogMutationAvailable)
            {
                continue;
            }

            if (permittedSet != null && !permittedSet.Contains(fn.Name))
            {
                continue;
            }

            if (addedNames.Add(fn.Name))
            {
                advertisedTools.Add(fn);
            }
        }

        if (additionalModules is { Count: > 0 })
        {
            foreach (var module in additionalModules)
            {
                if (module == null)
                {
                    continue;
                }

                var moduleFunctions = module.CreateFunctions();
                dispatcher?.RegisterTools(moduleFunctions);

                foreach (var fn in moduleFunctions)
                {
                    if (addedNames.Add(fn.Name))
                    {
                        advertisedTools.Add(fn);
                    }
                }
            }
        }

        return advertisedTools;
    }

    /// <summary>
    /// Creates all 14 canonical <see cref="AIFunction"/> instances with their canonical JSON schemas
    /// by delegating to the five core <see cref="IAgentToolModule"/> implementations.
    /// When <paramref name="tools"/> is <c>null</c>, creates schema-equivalent <see cref="AIFunction"/> definitions
    /// suitable for <see cref="LenientToolDispatcher"/> schema inspection and validation.
    /// </summary>
    public static IReadOnlyList<AIFunction> CreateAllCanonicalFunctions(CanonicalTools? tools = null)
    {
        int maxReadBytes = tools?.MaxReadFileBytes ?? WorkflowDefaults.MaxReadFileBytes;
        var functions = new List<AIFunction>(14);
        functions.AddRange(WorkspaceFileToolModule.CreateFunctions(tools?.FileModule, maxReadBytes));
        functions.AddRange(SandboxBashToolModule.CreateFunctions(tools?.BashModule));
        functions.AddRange(ResearchAndSkillsToolModule.CreateFunctions(tools?.ResearchAndSkillsModule));
        functions.AddRange(WorkflowInteractionToolModule.CreateFunctions(tools?.InteractionModule));
        functions.AddRange(BacklogManagementToolModule.CreateFunctions(tools?.BacklogModule));
        return functions;
    }

    private static bool IsBacklogTool(string toolName) =>
        CanonicalToolNames.BacklogMutationTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
