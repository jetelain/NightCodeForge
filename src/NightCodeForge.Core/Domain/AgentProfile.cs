using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Target pinning policy for OllamaRouter reverse proxy dispatch backends.
/// Propagated as the X-OllamaRouter-Target HTTP request header.
/// 
/// ARCHITECTURAL NOTE: Both 'Local' (Host GPU) and 'Remote' (LAN Server) are 100% on-premise,
/// private workloads. 'Remote' refers to a Local Network Area server dedicated to GPU workloads
/// with high VRAM (powering ~95% of background dev-queue tasks). 'Local' refers to the developer's
/// host machine GPU (ideal for fast tasks and concurrent Ideation Chat).
/// 'Cloud' represents external cloud overflow (e.g., ollama.com or commercial APIs).
/// </summary>
public enum OllamaRoutingTarget
{
    /// <summary>Dynamic VRAM and context-aware routing across Host GPU and LAN Server.</summary>
    Auto = 0,

    /// <summary>Host GPU (Developer Workstation) - Fast, on-premise local hardware.</summary>
    Local = 1,

    /// <summary>Dedicated LAN GPU Server - High-VRAM local network server (~95% of dev tasks).</summary>
    Remote = 2,

    /// <summary>Cloud Overflow - External zero-local-VRAM endpoint for instant complex chats.</summary>
    Cloud = 3
}

/// <summary>
/// Configuration profile specifying model, reasoning parameters, harness, and permissions for an agent.
/// </summary>
public class AgentProfile
{    
    /// <summary>
    /// Generous default turn budget ensuring full agent autonomy without premature stalls.
    /// </summary>
    public const int DefaultMaxTurns = 500;

    public string Id { get; set; } = string.Empty;
    public int? WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HarnessId { get; set; } = StandardHarnessIds.Crucible;
    public string ModelName { get; set; } = string.Empty;
    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.None;
    public int? ThinkingTokenBudget { get; set; }
    public double? Temperature { get; set; }
    public string? HarnessConfigurationJson { get; set; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }
    public IReadOnlyList<string>? PermittedTools { get; set; }
    public Dictionary<string, string>? StageDirectives { get; set; }
    public AgentMode Mode { get; set; } = AgentMode.Full;
    public SandboxSecurityTier SecurityTier { get; set; } = SandboxSecurityTier.Standard;
    public string? SystemPrompt { get; set; }
    public int? MaxTurns { get; set; } = DefaultMaxTurns;
    public bool AllowNoTests { get; set; }

    /// <summary>
    /// Model context window in tokens, used for context compaction budgeting.
    /// If null, falls back to model profile, workspace, or global settings.
    /// </summary>
    public int? ContextWindowTokens { get; set; }

    /// <summary>
    /// Model output token ceiling per turn (mapped to num_predict in Ollama or max_tokens in OpenAI).
    /// If null, falls back to model capability profile or global settings.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// OllamaRouter target pinning policy (propagated as X-OllamaRouter-Target header).
    /// </summary>
    public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;

    /// <summary>
    /// Difficulty-tiered harness, model, and reasoning overrides (for Difficult and Expert tasks).
    /// Base properties on the profile represent <see cref="TaskDifficulty.Standard"/>.
    /// </summary>
    public Dictionary<TaskDifficulty, DifficultyStageOverride>? DifficultyOverrides { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>
    /// Deep-clones <see cref="DifficultyOverrides"/> when present, returning null otherwise.
    /// </summary>
    public Dictionary<TaskDifficulty, DifficultyStageOverride>? CloneDifficultyOverrides()
    {
        if (DifficultyOverrides == null)
        {
            return null;
        }

        var clone = new Dictionary<TaskDifficulty, DifficultyStageOverride>(DifficultyOverrides.Count);
        foreach (var (tier, stageOverride) in DifficultyOverrides)
        {
            if (stageOverride != null)
            {
                clone[tier] = stageOverride.Clone();
            }
        }
        return clone;
    }

    /// <summary>
    /// Determines whether resolving this profile for <paramref name="difficulty"/> requires
    /// falling back to the architect (<c>task-review</c>) profile when no explicit override exists.
    /// </summary>
    public bool RequiresArchitectFallbackForDifficulty(TaskDifficulty difficulty)
    {
        if (difficulty == TaskDifficulty.Standard)
        {
            return false;
        }

        if (TryGetEffectiveOverride(difficulty, out _))
        {
            return false;
        }

        return IsImplementationStageProfileId(Id);
    }

    /// <summary>
    /// Determines whether this profile targets <paramref name="stageId"/> by exact ID,
    /// workspace-scoped ID (<c>ws-{id}:{stageId}</c>), stage suffix (<c>:{stageId}</c>),
    /// or <see cref="StageDirectives"/> key.
    /// </summary>
    public bool MatchesStage(string stageId, int? workspaceId = null)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return false;
        }

        if (string.Equals(Id, stageId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (workspaceId.HasValue &&
            string.Equals(Id, $"ws-{workspaceId.Value}:{stageId}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Id.EndsWith($":{stageId}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StageDirectives != null && StageDirectives.ContainsKey(stageId);
    }

    /// <summary>
    /// Resolves the effective <see cref="AgentProfile"/> for the specified <paramref name="difficulty"/>,
    /// applying configured <see cref="DifficultyOverrides"/> or legacy implementation-to-architect escalation fallback.
    /// </summary>
    public AgentProfile ResolveForDifficulty(TaskDifficulty difficulty, AgentProfile? fallbackArchitectProfile = null)
    {
        if (difficulty == TaskDifficulty.Standard)
        {
            return this;
        }

        if (TryGetEffectiveOverride(difficulty, out var ov) && ov != null)
        {
            var effectiveHarnessId = !string.IsNullOrWhiteSpace(ov.HarnessId) ? ov.HarnessId.Trim() : HarnessId;
            var sameHarness = string.Equals(effectiveHarnessId, HarnessId, StringComparison.OrdinalIgnoreCase);

            return CloneWithExecutionSettings(
                harnessId: effectiveHarnessId,
                modelName: ov.ModelName.Trim(),
                thinkingEffort: ov.ThinkingEffort,
                thinkingTokenBudget: ov.ThinkingTokenBudget,
                harnessConfigurationJson: ov.HarnessConfigurationJson ?? (sameHarness ? HarnessConfigurationJson : null),
                contextWindowTokens: ov.ContextWindowTokens ?? (sameHarness ? ContextWindowTokens : null),
                maxOutputTokens: ov.MaxOutputTokens ?? MaxOutputTokens,
                routingTarget: ov.RoutingTarget);
        }

        if (IsImplementationStageProfileId(Id) && fallbackArchitectProfile != null)
        {
            var effectiveArchitect = fallbackArchitectProfile.ResolveForDifficulty(difficulty);
            var escalatedEffort = effectiveArchitect.ThinkingEffort == ThinkingEffort.CustomBudget ||
                                  effectiveArchitect.ThinkingEffort >= ThinkingEffort.High
                ? effectiveArchitect.ThinkingEffort
                : ThinkingEffort.High;

            return CloneWithExecutionSettings(
                harnessId: effectiveArchitect.HarnessId,
                modelName: effectiveArchitect.ModelName,
                thinkingEffort: escalatedEffort,
                thinkingTokenBudget: effectiveArchitect.ThinkingTokenBudget,
                harnessConfigurationJson: effectiveArchitect.HarnessConfigurationJson,
                contextWindowTokens: effectiveArchitect.ContextWindowTokens,
                maxOutputTokens: effectiveArchitect.MaxOutputTokens,
                routingTarget: effectiveArchitect.RoutingTarget);
        }

        return this;
    }

    private AgentProfile CloneWithExecutionSettings(
        string harnessId,
        string modelName,
        ThinkingEffort thinkingEffort,
        int? thinkingTokenBudget,
        string? harnessConfigurationJson,
        int? contextWindowTokens,
        int? maxOutputTokens,
        OllamaRoutingTarget routingTarget) => new()
    {
        Id = Id,
        WorkspaceId = WorkspaceId,
        Name = Name,
        HarnessId = harnessId,
        ModelName = modelName,
        ThinkingEffort = thinkingEffort,
        ThinkingTokenBudget = thinkingTokenBudget,
        Temperature = Temperature,
        HarnessConfigurationJson = harnessConfigurationJson,
        OptimizationFlags = OptimizationFlags?.Clone(),
        PermittedTools = PermittedTools?.ToList(),
        StageDirectives = StageDirectives != null ? new Dictionary<string, string>(StageDirectives) : null,
        Mode = Mode,
        SecurityTier = SecurityTier,
        SystemPrompt = SystemPrompt,
        MaxTurns = MaxTurns,
        AllowNoTests = AllowNoTests,
        ContextWindowTokens = contextWindowTokens,
        MaxOutputTokens = maxOutputTokens,
        RoutingTarget = routingTarget,
        DifficultyOverrides = CloneDifficultyOverrides(),
        Workspace = Workspace
    };

    private bool TryGetEffectiveOverride(TaskDifficulty difficulty, out DifficultyStageOverride? stageOverride)
    {
        stageOverride = null;
        if (DifficultyOverrides == null || DifficultyOverrides.Count == 0)
        {
            return false;
        }

        if (DifficultyOverrides.TryGetValue(difficulty, out var direct) &&
            direct != null &&
            !string.IsNullOrWhiteSpace(direct.ModelName))
        {
            stageOverride = direct;
            return true;
        }

        if (difficulty == TaskDifficulty.Expert &&
            DifficultyOverrides.TryGetValue(TaskDifficulty.Difficult, out var difficultFallback) &&
            difficultFallback != null &&
            !string.IsNullOrWhiteSpace(difficultFallback.ModelName))
        {
            stageOverride = difficultFallback;
            return true;
        }

        return false;
    }

    private static bool IsImplementationStageProfileId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        return string.Equals(profileId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase) ||
               profileId.EndsWith($":{StandardStageIds.Implementation}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Base persona of the default ideation profile (also the fallback persona of the ideation service).
    /// </summary>
    public const string DefaultIdeationSystemPrompt = @"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.
Each proposed task represents a full Pull Request / Merge Request unit of work with autonomous build, test, and code review gates.
Avoid over-decomposing into micro-tasks; structure cohesive PR-sized tasks with concrete functional scope. Focus on functional and business requirements: solve all user-facing, behavioral, and acceptance criteria questions with the maintainer in chat before formulating tasks so that tasks enter the execution queue fully determined. You are not mandated to identify all technical impacts (e.g. files); downstream review stages handle technical scoping.";

    /// <summary>
    /// Persona text shipped before Step 12.13A; reconciled onto existing stored profiles when still unmodified.
    /// </summary>
    public const string LegacyIdeationSystemPrompt = @"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.
Each proposed task represents a full Pull Request / Merge Request unit of work with autonomous build, test, and code review gates.
Avoid over-decomposing into micro-tasks; structure cohesive PR-sized tasks with sub-tasks and open questions to be answered in the task review process.";

    /// <summary>
    /// Default agent profile used for interactive ideation and codebase discovery.
    /// Not scheduled like other pipeline workloads.
    /// </summary>
    public static AgentProfile CreateDefaultIdeationProfile(int? workspaceId = null, string defaultModelName = "") => new()
    {
        Id = StandardStageIds.Ideation,
        WorkspaceId = workspaceId,
        Name = "Ideation & Codebase Discovery Agent",
        HarnessId = StandardHarnessIds.Crucible,
        ModelName = defaultModelName,
        Mode = AgentMode.ReadOnly,
        PermittedTools = DefaultAgentTools.Ideation,
        OptimizationFlags = null,
        RoutingTarget = OllamaRoutingTarget.Auto,
        SystemPrompt = DefaultIdeationSystemPrompt,
        ThinkingEffort = ThinkingEffort.Medium,
        MaxTurns = 300
    };

    /// <summary>
    /// Creates the baseline set of 5 standard global agent profiles (WorkspaceId == null)
    /// configured with the designated default model or differentiated coder/architect roles.
    /// OptimizationFlags is null by default to inherit global optimizations.
    /// </summary>
    public static IReadOnlyList<AgentProfile> CreateStandardGlobalProfiles(ILlmProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var defaultModel = !string.IsNullOrWhiteSpace(options.DefaultModelId) 
            ? options.DefaultModelId.Trim() 
            : KnownDefaultModels.DefaultBaseline;
        var coderModel = !string.IsNullOrWhiteSpace(options.CoderModelId) ? options.CoderModelId.Trim() : defaultModel;
        var coderEffort = options.CoderThinkingEffort ?? options.DefaultThinkingEffort;

        var archModel = !string.IsNullOrWhiteSpace(options.ArchitectModelId) ? options.ArchitectModelId.Trim() : defaultModel;
        var archEffort = options.ArchitectThinkingEffort ?? (options.DefaultThinkingEffort == ThinkingEffort.None ? ThinkingEffort.None : ThinkingEffort.High);

        var autoOrLocal = OllamaRoutingTarget.Auto;
        var triageTarget = options.ProviderType == LlmProviderType.Ollama ? OllamaRoutingTarget.Auto : OllamaRoutingTarget.Local;

        var ideationProfile = CreateDefaultIdeationProfile(null, archModel);
        ideationProfile.ThinkingEffort = archEffort;

        DifficultyStageOverride CreateDifficultOverride() => new()
        {
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = archModel,
            ThinkingEffort = ThinkingEffort.High,
            RoutingTarget = autoOrLocal
        };

        DifficultyStageOverride CreateExpertOverride()
        {
            if (!string.IsNullOrWhiteSpace(options.ExpertHarnessId) || !string.IsNullOrWhiteSpace(options.ExpertModelId))
            {
                var expertHarness = !string.IsNullOrWhiteSpace(options.ExpertHarnessId)
                    ? options.ExpertHarnessId.Trim()
                    : StandardHarnessIds.Crucible;
                var isAcp = string.Equals(expertHarness, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);
                var expertModel = !string.IsNullOrWhiteSpace(options.ExpertModelId)
                    ? options.ExpertModelId.Trim()
                    : (isAcp ? "gemini-4-argon" : archModel);
                var expertEffort = options.ExpertThinkingEffort ?? ThinkingEffort.High;

                return new DifficultyStageOverride
                {
                    HarnessId = expertHarness,
                    ModelName = expertModel,
                    ThinkingEffort = expertEffort,
                    RoutingTarget = isAcp ? OllamaRoutingTarget.Auto : autoOrLocal
                };
            }

            return CreateDifficultOverride();
        }

        Dictionary<TaskDifficulty, DifficultyStageOverride> CreateTaskPipelineOverrides() => new()
        {
            [TaskDifficulty.Difficult] = CreateDifficultOverride(),
            [TaskDifficulty.Expert] = CreateExpertOverride()
        };

        return new List<AgentProfile>
        {
            new()
            {
                Id = StandardStageIds.Triage,
                WorkspaceId = null,
                Name = "Stage 0 Untrusted Intake Triage Agent",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = archModel,
                Mode = AgentMode.ReadOnly,
                SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
                RoutingTarget = triageTarget,
                PermittedTools = DefaultAgentTools.Triage,
                OptimizationFlags = null,
                ThinkingEffort = archEffort,
                MaxTurns = 250
            },
            ideationProfile,
            new()
            {
                Id = StandardStageIds.TaskReview,
                WorkspaceId = null,
                Name = "ForgeTask Review Agent",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = archModel,
                Mode = AgentMode.Full,
                SecurityTier = SandboxSecurityTier.Standard,
                RoutingTarget = autoOrLocal,
                PermittedTools = DefaultAgentTools.Full,
                OptimizationFlags = null,
                ThinkingEffort = archEffort,
                MaxTurns = 500,
                DifficultyOverrides = CreateTaskPipelineOverrides()
            },
            new()
            {
                Id = StandardStageIds.Implementation,
                WorkspaceId = null,
                Name = "Implementation Agent",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = coderModel,
                Mode = AgentMode.Full,
                SecurityTier = SandboxSecurityTier.Standard,
                RoutingTarget = autoOrLocal,
                PermittedTools = DefaultAgentTools.Full,
                OptimizationFlags = null,
                ThinkingEffort = coderEffort,
                MaxTurns = 500,
                DifficultyOverrides = CreateTaskPipelineOverrides()
            },
            new()
            {
                Id = StandardStageIds.CodeReviewAndFix,
                WorkspaceId = null,
                Name = "Code Review & Targeted Fix Agent",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = archModel,
                Mode = AgentMode.Review,
                SecurityTier = SandboxSecurityTier.Standard,
                RoutingTarget = autoOrLocal,
                PermittedTools = DefaultAgentTools.Review,
                OptimizationFlags = null,
                ThinkingEffort = archEffort,
                MaxTurns = 500,
                DifficultyOverrides = CreateTaskPipelineOverrides()
            }
        };
    }

    /// <summary>
    /// Helper overload for convenience in tests and workspace provisioning using a default model identifier.
    /// </summary>
    public static IReadOnlyList<AgentProfile> CreateStandardGlobalProfiles(string defaultModelName)
        => CreateStandardGlobalProfiles(new DefaultLlmProviderOptions(DefaultModelId: defaultModelName));
}
