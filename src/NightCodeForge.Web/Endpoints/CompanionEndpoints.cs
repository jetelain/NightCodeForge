using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Endpoints;

/// <summary>
/// JSON error payload returned by companion HTTP endpoints.
/// </summary>
public sealed record CompanionErrorResponse(
    [property: JsonPropertyName("error")] string Error);

/// <summary>
/// Companion discovery payload describing the active NightCodeForge LLM inference configuration.
/// </summary>
public sealed record CompanionConfigResponse(
    [property: JsonPropertyName("providerType")] string ProviderType,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("defaultModelId")] string? DefaultModelId,
    [property: JsonPropertyName("architectModelId")] string? ArchitectModelId,
    [property: JsonPropertyName("coderModelId")] string? CoderModelId,
    [property: JsonPropertyName("openAiCompatible")] CompanionOpenAiCompatibleConfig OpenAiCompatible);

/// <summary>
/// OpenAI-compatible provider details surfaced to local companion tools.
/// </summary>
public sealed record CompanionOpenAiCompatibleConfig(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("organizationId")] string? OrganizationId);

/// <summary>
/// Companion discovery payload listing registered workspaces and pending review counts.
/// </summary>
public sealed record CompanionWorkspacesResponse(
    [property: JsonPropertyName("workspaces")] IReadOnlyList<CompanionWorkspaceDto> Workspaces);

/// <summary>
/// Workspace metadata summary returned to companion applications.
/// </summary>
public sealed record CompanionWorkspaceDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("emoji")] string? Emoji,
    [property: JsonPropertyName("colorCode")] string? ColorCode,
    [property: JsonPropertyName("repositoryPath")] string RepositoryPath,
    [property: JsonPropertyName("defaultBaseBranch")] string DefaultBaseBranch,
    [property: JsonPropertyName("pendingReviewTaskCount")] int PendingReviewTaskCount);

/// <summary>
/// Request payload for staging a companion exploratory report into a workspace triage draft.
/// </summary>
public sealed record CreateCompanionTriageRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("reportMarkdown")] string? ReportMarkdown,
    [property: JsonPropertyName("baseBranch")] string? BaseBranch,
    [property: JsonPropertyName("sessionDirectoryPath")] string? SessionDirectoryPath);

/// <summary>
/// Response payload returned after triggering an automatic companion triage run.
/// </summary>
public sealed record CreateCompanionTriageResponse(
    [property: JsonPropertyName("draftId")] string DraftId,
    [property: JsonPropertyName("workspaceId")] int WorkspaceId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("triageUrl")] string TriageUrl);

/// <summary>
/// Response payload returned after staging a companion report for interactive ideation.
/// </summary>
public sealed record CreateCompanionIdeationResponse(
    [property: JsonPropertyName("draftId")] string DraftId,
    [property: JsonPropertyName("workspaceId")] int WorkspaceId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("ideationUrl")] string IdeationUrl);

/// <summary>
/// Maps authenticated <c>/api/companion/*</c> Minimal API endpoints for local companion tools.
/// </summary>
public static class CompanionEndpoints
{
    public const string RoutePrefix = "/api/companion";
    public const string DefaultBaseBranchFallback = "main";
    public const string WorkspaceNotFoundErrorMessage = "Workspace not found.";
    public const string ReportMarkdownRequiredErrorMessage = "reportMarkdown is required.";
    public const string InvalidJsonPayloadErrorMessage = "Request body must be a valid JSON object.";

    /// <summary>
    /// Registers companion discovery and triage handoff endpoints under <c>/api/companion</c>.
    /// </summary>
    public static RouteGroupBuilder MapCompanionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix)
            .AddEndpointFilter<CompanionApiKeyEndpointFilter>()
            .DisableAntiforgery();

        group.MapGet("/config", GetConfig);
        group.MapGet("/workspaces", GetWorkspacesAsync);
        group.MapPost("/workspaces/{workspaceId:int}/triage", TriggerAutomaticTriageAsync);
        group.MapPost("/workspaces/{workspaceId:int}/ideation", CreateIdeationDraftAsync);

        return group;
    }

    private static IResult GetConfig(
        IOptionsMonitor<LlmProviderOptions> llmOptionsMonitor,
        IApiKeySecretStore secretStore)
    {
        var options = llmOptionsMonitor.CurrentValue;
        var defaultModelId = NormalizeOptionalText(options.DefaultModelId);
        var architectModelId = NormalizeOptionalText(options.ArchitectModelId) ?? defaultModelId;
        var coderModelId = NormalizeOptionalText(options.CoderModelId) ?? defaultModelId;

        string? openAiApiKey = options.ProviderType == LlmProviderType.OpenAiCompatible
            ? NormalizeOptionalText(secretStore.ResolveEffectiveOpenAiApiKey(options.EffectiveEndpoint))
            : null;

        var openAiCompatible = new CompanionOpenAiCompatibleConfig(
            Endpoint: options.OpenAiCompatible?.Endpoint?.Trim() ?? string.Empty,
            ApiKey: openAiApiKey,
            OrganizationId: NormalizeOptionalText(options.OpenAiCompatible?.OrganizationId));

        var response = new CompanionConfigResponse(
            ProviderType: options.ProviderType.ToString(),
            Endpoint: options.EffectiveEndpoint,
            DefaultModelId: defaultModelId,
            ArchitectModelId: architectModelId,
            CoderModelId: coderModelId,
            OpenAiCompatible: openAiCompatible);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetWorkspacesAsync(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        CancellationToken cancellationToken)
    {
        var workspaces = await workspaceRepository.GetAllAsync(cancellationToken);
        var readyTasks = await taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken);

        var pendingReviewCountsByWorkspace = readyTasks
            .Where(TaskBadgeCounter.IsPendingHumanReview)
            .GroupBy(t => t.WorkspaceId)
            .ToDictionary(g => g.Key, g => g.Count());

        var items = workspaces
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Id)
            .Select(w => new CompanionWorkspaceDto(
                Id: w.Id,
                Name: w.Name,
                Emoji: w.Emoji,
                ColorCode: w.ColorCode,
                RepositoryPath: w.RepositoryPath,
                DefaultBaseBranch: w.DefaultBaseBranch,
                PendingReviewTaskCount: pendingReviewCountsByWorkspace.GetValueOrDefault(w.Id, 0)))
            .ToList();

        return Results.Ok(new CompanionWorkspacesResponse(items));
    }

    private sealed record PreparedCompanionDraftInput(
        Workspace Workspace,
        string Title,
        string ReportMarkdown,
        string BaseBranch,
        string? SessionDirectoryPath);

    private static async Task<IResult> TriggerAutomaticTriageAsync(
        int workspaceId,
        HttpRequest httpRequest,
        IWorkspaceRepository workspaceRepository,
        ICompanionTriageDraftStore draftStore,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime appLifetime,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (input, errorResult) = await TryPrepareDraftInputAsync(
            workspaceId,
            httpRequest,
            workspaceRepository,
            timeProvider,
            cancellationToken);
        if (errorResult is not null || input is null)
        {
            return errorResult!;
        }

        var triageRequest = new TriageReportRequest(
            Title: input.Title,
            Description: input.ReportMarkdown,
            BaseBranch: input.BaseBranch,
            Origin: TaskOrigin.UntrustedIntake);

        var runHandle = new TriageRunHandle();
        var logger = loggerFactory.CreateLogger(typeof(CompanionEndpoints));
        var sessionExecutionTask = StartBackgroundTriageAsync(
            scopeFactory,
            input.Workspace,
            triageRequest,
            runHandle,
            logger,
            appLifetime.ApplicationStopping);
        var executionTask = ExtractSurvivingTasksAsync(sessionExecutionTask);

        var draft = draftStore.CreateDraft(
            input.Workspace.Id,
            input.Title,
            input.ReportMarkdown,
            input.BaseBranch,
            input.SessionDirectoryPath,
            executionTask,
            runHandle,
            sessionExecutionTask);

        var triageUrl = TriageSubmissionModel.BuildCompanionDraftUrl(input.Workspace.Id, draft.DraftId);
        var response = new CreateCompanionTriageResponse(
            DraftId: draft.DraftId,
            WorkspaceId: input.Workspace.Id,
            Title: draft.Title,
            TriageUrl: triageUrl);

        return Results.Created(triageUrl, response);
    }

    private static async Task<IResult> CreateIdeationDraftAsync(
        int workspaceId,
        HttpRequest httpRequest,
        IWorkspaceRepository workspaceRepository,
        ICompanionTriageDraftStore draftStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (input, errorResult) = await TryPrepareDraftInputAsync(
            workspaceId,
            httpRequest,
            workspaceRepository,
            timeProvider,
            cancellationToken);
        if (errorResult is not null || input is null)
        {
            return errorResult!;
        }

        var draft = draftStore.CreateDraft(
            input.Workspace.Id,
            input.Title,
            input.ReportMarkdown,
            input.BaseBranch,
            input.SessionDirectoryPath,
            executionTask: null);

        var ideationUrl = IdeationRefinementHelper.BuildCompanionDraftUrl(input.Workspace.Id, draft.DraftId);
        var response = new CreateCompanionIdeationResponse(
            DraftId: draft.DraftId,
            WorkspaceId: input.Workspace.Id,
            Title: draft.Title,
            IdeationUrl: ideationUrl);

        return Results.Created(ideationUrl, response);
    }

    private static async Task<(PreparedCompanionDraftInput? Input, IResult? ErrorResult)> TryPrepareDraftInputAsync(
        int workspaceId,
        HttpRequest httpRequest,
        IWorkspaceRepository workspaceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            return (null, Results.Json(
                new CompanionErrorResponse(WorkspaceNotFoundErrorMessage),
                statusCode: StatusCodes.Status404NotFound));
        }

        CreateCompanionTriageRequest? request = null;
        if (httpRequest.HasJsonContentType() && httpRequest.ContentLength is null or > 0)
        {
            try
            {
                request = await httpRequest.ReadFromJsonAsync<CreateCompanionTriageRequest>(cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return (null, Results.Json(
                    new CompanionErrorResponse(InvalidJsonPayloadErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest));
            }
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ReportMarkdown))
        {
            return (null, Results.Json(
                new CompanionErrorResponse(ReportMarkdownRequiredErrorMessage),
                statusCode: StatusCodes.Status400BadRequest));
        }

        var title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : $"Assay Report #{timeProvider.GetUtcNow():MMdd-HHmm}";

        var baseBranch = !string.IsNullOrWhiteSpace(request.BaseBranch)
            ? request.BaseBranch.Trim()
            : (!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch)
                ? workspace.DefaultBaseBranch
                : DefaultBaseBranchFallback);

        var sessionDirectoryPath = NormalizeOptionalText(request.SessionDirectoryPath);

        return (new PreparedCompanionDraftInput(
            workspace,
            title,
            request.ReportMarkdown,
            baseBranch,
            sessionDirectoryPath), null);
    }

    private static Task<TriageSessionResult> StartBackgroundTriageAsync(
        IServiceScopeFactory scopeFactory,
        Workspace workspace,
        TriageReportRequest request,
        TriageRunHandle runHandle,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sessionTask = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var triageService = scope.ServiceProvider.GetRequiredService<ITriageService>();
                return await triageService.RunTriageSessionAsync(
                    workspace,
                    request,
                    runHandle.NotifyTriageTaskCreated,
                    cancellationToken);
            }
            finally
            {
                runHandle.NotifyFailedWithoutTask();
            }
        }, cancellationToken);

        _ = sessionTask.ContinueWith(
            t => logger.LogError(t.Exception, "Background companion triage execution failed for workspace #{WorkspaceId}.", workspace.Id),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return sessionTask;
    }

    private static Task<IReadOnlyList<ForgeTask>> ExtractSurvivingTasksAsync(Task<TriageSessionResult> sessionTask)
    {
        var derivedTask = ExtractCoreAsync(sessionTask);
        _ = derivedTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return derivedTask;

        static async Task<IReadOnlyList<ForgeTask>> ExtractCoreAsync(Task<TriageSessionResult> innerTask)
        {
            var result = await innerTask;
            return result.Tasks;
        }
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
