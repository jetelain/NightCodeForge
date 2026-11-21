using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Endpoints;
using NightCodeForge.Web.Tests.Fixtures;
using Pmad.LocalApi.Security;

namespace NightCodeForge.Web.Tests;

public sealed class CompanionEndpointsTests : IDisposable
{
    private readonly string _tempRoot;

    public CompanionEndpointsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-companion-web-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp directory on test teardown.
            }
        }
    }

    [Fact]
    public async Task CompanionEndpoints_WhenUnauthenticatedOrInvalidKey_Returns401JsonWithoutHtmlRewrite()
    {
        await using var host = await CreateHostAsync();
        using var client = CreateClient(host);

        // Missing credentials
        var missingResponse = await client.GetAsync("/api/companion/config");
        await AssertUnauthorizedJsonAsync(missingResponse);

        // Invalid Bearer token
        using var badBearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/workspaces");
        badBearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token-value");
        var badBearerResponse = await client.SendAsync(badBearerRequest);
        await AssertUnauthorizedJsonAsync(badBearerResponse);

        // Invalid X-Api-Key header
        using var badHeaderRequest = new HttpRequestMessage(HttpMethod.Post, "/api/companion/workspaces/1/triage")
        {
            Content = JsonContent.Create(new { reportMarkdown = "# Bug" })
        };
        badHeaderRequest.Headers.Add(CompanionApiKeyEndpointFilter.ApiKeyHeaderName, "wrong-key");
        var badHeaderResponse = await client.SendAsync(badHeaderRequest);
        await AssertUnauthorizedJsonAsync(badHeaderResponse);

        // Unauthenticated POST with empty body and no Content-Type -> must still return 401 JSON (not 415 HTML)
        using var emptyPostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/companion/workspaces/1/triage");
        var emptyPostResponse = await client.SendAsync(emptyPostRequest);
        await AssertUnauthorizedJsonAsync(emptyPostResponse);

        // Unauthenticated POST with malformed JSON -> must still return 401 JSON (not 400 HTML)
        using var malformedPostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/companion/workspaces/1/triage")
        {
            Content = new StringContent("{bad", Encoding.UTF8, "application/json")
        };
        var malformedPostResponse = await client.SendAsync(malformedPostRequest);
        await AssertUnauthorizedJsonAsync(malformedPostResponse);

        // Unauthenticated POST to ideation endpoint -> must return 401 JSON
        var unauthIdeationResponse = await client.PostAsJsonAsync(
            "/api/companion/workspaces/1/ideation",
            new { reportMarkdown = "# Bug" });
        await AssertUnauthorizedJsonAsync(unauthIdeationResponse);

        var expectedKeyPath = Path.Combine(_tempRoot, "config", "default-key.dat");
        Assert.True(File.Exists(expectedKeyPath));
    }

    [Fact]
    public async Task CompanionEndpoints_SupportsBearerAndXApiKey_WithBearerPrecedence()
    {
        await using var host = await CreateHostAsync();
        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();

        // Standard Bearer token
        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/config");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validKey);
        var bearerResponse = await client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);

        // Case-insensitive Bearer scheme
        using var lowerBearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/config");
        lowerBearerRequest.Headers.TryAddWithoutValidation("Authorization", $"bearer {validKey}");
        var lowerBearerResponse = await client.SendAsync(lowerBearerRequest);
        Assert.Equal(HttpStatusCode.OK, lowerBearerResponse.StatusCode);

        // X-Api-Key fallback
        using var xApiKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/config");
        xApiKeyRequest.Headers.Add(CompanionApiKeyEndpointFilter.ApiKeyHeaderName, validKey);
        var xApiKeyResponse = await client.SendAsync(xApiKeyRequest);
        Assert.Equal(HttpStatusCode.OK, xApiKeyResponse.StatusCode);

        // Valid Bearer overrides invalid X-Api-Key
        using var validBearerBadXKey = new HttpRequestMessage(HttpMethod.Get, "/api/companion/config");
        validBearerBadXKey.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validKey);
        validBearerBadXKey.Headers.Add(CompanionApiKeyEndpointFilter.ApiKeyHeaderName, "wrong-key");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(validBearerBadXKey)).StatusCode);

        // Invalid Bearer takes precedence over valid X-Api-Key -> 401
        using var badBearerValidXKey = new HttpRequestMessage(HttpMethod.Get, "/api/companion/config");
        badBearerValidXKey.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        badBearerValidXKey.Headers.Add(CompanionApiKeyEndpointFilter.ApiKeyHeaderName, validKey);
        await AssertUnauthorizedJsonAsync(await client.SendAsync(badBearerValidXKey));
    }

    [Fact]
    public async Task GetConfig_ReturnsProviderTypeStringEffectiveEndpointFallbacksAndOpenAiOptions()
    {
        var secretStore = new FakeApiKeySecretStore();
        secretStore.SeedOpenAiKey("https://openrouter.ai/api/v1", "  sk-test-123  ");

        await using var host = await CreateHostAsync(
            configureLlm: opts =>
            {
                opts.ProviderType = LlmProviderType.OpenAiCompatible;
                opts.Endpoint = "http://localhost:11434";
                opts.DefaultModelId = "  gpt-4o-mini  ";
                opts.ArchitectModelId = "  gpt-4o ";
                opts.CoderModelId = "   ";
                opts.OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = "  https://openrouter.ai/api/v1  ",
                    OrganizationId = "  org-456  "
                };
            },
            secretStore: secretStore);

        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        var response = await client.GetAsync("/api/companion/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("OpenAiCompatible", root.GetProperty("providerType").GetString());
        Assert.Equal("https://openrouter.ai/api/v1", root.GetProperty("endpoint").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("defaultModelId").GetString());
        Assert.Equal("gpt-4o", root.GetProperty("architectModelId").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("coderModelId").GetString());

        var openAi = root.GetProperty("openAiCompatible");
        Assert.Equal("https://openrouter.ai/api/v1", openAi.GetProperty("endpoint").GetString());
        Assert.Equal("sk-test-123", openAi.GetProperty("apiKey").GetString());
        Assert.Equal("org-456", openAi.GetProperty("organizationId").GetString());
        Assert.Equal(1, secretStore.OpenAiResolveCalls);

        // Absent key and whitespace-only org ID normalize to null
        var blankSecretStore = new FakeApiKeySecretStore { OpenAiEnvFallback = "   " };
        await using var blankSecretsHost = await CreateHostAsync(
            configureLlm: opts =>
            {
                opts.ProviderType = LlmProviderType.OpenAiCompatible;
                opts.OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = "  https://api.openai.com/v1  ",
                    OrganizationId = "\t "
                };
            },
            secretStore: blankSecretStore);
        using var blankSecretsClient = CreateClient(blankSecretsHost);
        var blankSecretsKey = blankSecretsHost.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        blankSecretsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", blankSecretsKey);

        var blankResponse = await blankSecretsClient.GetAsync("/api/companion/config");
        Assert.Equal(HttpStatusCode.OK, blankResponse.StatusCode);

        using var blankDoc = JsonDocument.Parse(await blankResponse.Content.ReadAsStringAsync());
        var blankOpenAi = blankDoc.RootElement.GetProperty("openAiCompatible");
        Assert.Equal("https://api.openai.com/v1", blankOpenAi.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Null, blankOpenAi.GetProperty("apiKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, blankOpenAi.GetProperty("organizationId").ValueKind);
    }

    [Theory]
    [InlineData(LlmProviderType.Ollama)]
    [InlineData(LlmProviderType.OllamaRouter)]
    public async Task GetConfig_WhenProviderIsOllamaOrOllamaRouter_ReturnsNullApiKeyWithoutTouchingSecretStore(LlmProviderType providerType)
    {
        var secretStore = new FakeApiKeySecretStore();
        secretStore.SeedOpenAiKey("https://api.openai.com/v1", "sk-dormant-cloud-secret");

        await using var host = await CreateHostAsync(
            configureLlm: opts =>
            {
                opts.ProviderType = providerType;
                opts.Endpoint = "http://localhost:11434";
                opts.OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = "https://api.openai.com/v1"
                };
            },
            secretStore: secretStore);

        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        var response = await client.GetAsync("/api/companion/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var openAi = doc.RootElement.GetProperty("openAiCompatible");
        Assert.Equal(JsonValueKind.Null, openAi.GetProperty("apiKey").ValueKind);
        Assert.Equal(0, secretStore.OpenAiResolveCalls);
        Assert.Equal(0, secretStore.OpenAiHasCalls);
    }

    [Fact]
    public async Task GetWorkspaces_ReturnsOrderedWorkspacesWithPendingHumanReviewCounts()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();

        var wsBeta = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "beta-app",
            Emoji = "🧪",
            ColorCode = "#10b981",
            RepositoryPath = @"C:\repos\beta",
            DefaultBaseBranch = "develop"
        });
        var wsAlpha = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Alpha-Core",
            Emoji = "🔥",
            ColorCode = "#6366f1",
            RepositoryPath = @"C:\repos\alpha",
            DefaultBaseBranch = "main"
        });

        // Two Ready tasks with execution runs (pending human review) in Alpha, plus one Ready without execution run
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = wsAlpha.Id,
            Title = "Reviewable 1",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = wsAlpha.Id,
            Title = "Reviewable 2",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = wsAlpha.Id,
            Title = "Ready without execution run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = false
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = wsBeta.Id,
            Title = "Closed task",
            Status = TaskOverallStatus.Closed,
            HasExecutionRun = true
        });

        await using var host = await CreateHostAsync(workspaceRepo: workspaceRepo, taskRepo: taskRepo);
        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        var response = await client.GetAsync("/api/companion/workspaces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var workspaces = doc.RootElement.GetProperty("workspaces");
        Assert.Equal(2, workspaces.GetArrayLength());

        var first = workspaces[0];
        Assert.Equal(wsAlpha.Id, first.GetProperty("id").GetInt32());
        Assert.Equal("Alpha-Core", first.GetProperty("name").GetString());
        Assert.Equal("🔥", first.GetProperty("emoji").GetString());
        Assert.Equal("#6366f1", first.GetProperty("colorCode").GetString());
        Assert.Equal(@"C:\repos\alpha", first.GetProperty("repositoryPath").GetString());
        Assert.Equal("main", first.GetProperty("defaultBaseBranch").GetString());
        Assert.Equal(2, first.GetProperty("pendingReviewTaskCount").GetInt32());

        var second = workspaces[1];
        Assert.Equal(wsBeta.Id, second.GetProperty("id").GetInt32());
        Assert.Equal("beta-app", second.GetProperty("name").GetString());
        Assert.Equal(0, second.GetProperty("pendingReviewTaskCount").GetInt32());
    }

    [Fact]
    public async Task PostTriage_ValidatesWorkspaceAndPayload_AndStagesCompanionDraft()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "NightCodeForge",
            RepositoryPath = @"C:\repos\ncf",
            DefaultBaseBranch = "release/v2"
        });
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2026, 11, 15, 14, 35, 0, TimeSpan.Zero));
        var triageService = new RecordingTriageService();

        await using var host = await CreateHostAsync(
            workspaceRepo: workspaceRepo,
            timeProvider: fixedTime,
            triageService: triageService);
        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        // 1. Unknown workspace -> 404 JSON (not HTML re-execute)
        var notFoundResp = await client.PostAsJsonAsync(
            "/api/companion/workspaces/999/triage",
            new { title = "Bug", reportMarkdown = "# Bug" });
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
        Assert.Equal("application/json", notFoundResp.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await notFoundResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Workspace not found.", doc.RootElement.GetProperty("error").GetString());
        }

        // 2. Blank reportMarkdown -> 400 JSON (not HTML re-execute)
        var badReqResp = await client.PostAsJsonAsync(
            $"/api/companion/workspaces/{workspace.Id}/triage",
            new { title = "Missing markdown", reportMarkdown = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, badReqResp.StatusCode);
        Assert.Equal("application/json", badReqResp.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await badReqResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(CompanionEndpoints.ReportMarkdownRequiredErrorMessage, doc.RootElement.GetProperty("error").GetString());
        }

        // 2b. Malformed JSON body -> 400 JSON (not empty-body HTML re-execute)
        var malformedJsonResp = await client.PostAsync(
            $"/api/companion/workspaces/{workspace.Id}/triage",
            new StringContent("{invalid-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformedJsonResp.StatusCode);
        Assert.Equal("application/json", malformedJsonResp.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await malformedJsonResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(CompanionEndpoints.InvalidJsonPayloadErrorMessage, doc.RootElement.GetProperty("error").GetString());
        }

        // 2c. Empty body with no Content-Type -> 400 JSON (not 415 HTML re-execute)
        using var noBodyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/companion/workspaces/{workspace.Id}/triage");
        var noBodyResp = await client.SendAsync(noBodyReq);
        Assert.Equal(HttpStatusCode.BadRequest, noBodyResp.StatusCode);
        Assert.Equal("application/json", noBodyResp.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await noBodyResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(CompanionEndpoints.ReportMarkdownRequiredErrorMessage, doc.RootElement.GetProperty("error").GetString());
        }

        // 3. Explicit fields -> 201 Created
        var createdResp = await client.PostAsJsonAsync(
            $"/api/companion/workspaces/{workspace.Id}/triage",
            new
            {
                title = "  Assay Exploratory Report — 2026-11-15  ",
                reportMarkdown = "# Exploratory Test Report\n\nObserved validation issue.",
                baseBranch = "  main ",
                sessionDirectoryPath = @"  C:\sessions\2026-11-15_14-35  "
            });
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);

        using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync());
        var draftId = createdDoc.RootElement.GetProperty("draftId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(draftId));
        Assert.Equal(workspace.Id, createdDoc.RootElement.GetProperty("workspaceId").GetInt32());
        Assert.Equal("Assay Exploratory Report — 2026-11-15", createdDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal($"/w/{workspace.Id}/triage?draftId={draftId}", createdDoc.RootElement.GetProperty("triageUrl").GetString());
        Assert.Equal($"/w/{workspace.Id}/triage?draftId={draftId}", createdResp.Headers.Location?.ToString());

        var draftStore = host.Services.GetRequiredService<ICompanionTriageDraftStore>();
        var storedDraft = draftStore.GetDraft(draftId);
        Assert.NotNull(storedDraft);
        Assert.Equal("main", storedDraft.BaseBranch);
        Assert.Equal(@"C:\sessions\2026-11-15_14-35", storedDraft.SessionDirectoryPath);
        Assert.NotNull(storedDraft.RunHandle);
        Assert.NotNull(storedDraft.SessionExecutionTask);
        Assert.NotNull(storedDraft.ExecutionTask);
        var sessionResult = await storedDraft.SessionExecutionTask;
        Assert.Equal(RecordingTriageService.SimulatedTriageTaskId, sessionResult.TriageTaskId);
        Assert.Equal(RecordingTriageService.SimulatedTriageTaskId, storedDraft.TriageTaskId);
        await storedDraft.ExecutionTask;

        // 4. Omitted title & baseBranch -> generated fallback title and workspace.DefaultBaseBranch
        var fallbackResp = await client.PostAsync(
            $"/api/companion/workspaces/{workspace.Id}/triage",
            new StringContent("{\"reportMarkdown\":\"# Minimal Report\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, fallbackResp.StatusCode);

        using var fallbackDoc = JsonDocument.Parse(await fallbackResp.Content.ReadAsStringAsync());
        var fallbackDraftId = fallbackDoc.RootElement.GetProperty("draftId").GetString();
        Assert.Equal("Assay Report #1115-1435", fallbackDoc.RootElement.GetProperty("title").GetString());

        var fallbackDraft = draftStore.GetDraft(fallbackDraftId);
        Assert.NotNull(fallbackDraft);
        Assert.Equal("release/v2", fallbackDraft.BaseBranch);
        Assert.Null(fallbackDraft.SessionDirectoryPath);
        Assert.NotNull(fallbackDraft.ExecutionTask);
        await fallbackDraft.ExecutionTask;

        Assert.Equal(2, triageService.Calls.Count);
        Assert.Contains(triageService.Calls, c =>
            c.Workspace.Id == workspace.Id &&
            c.Request.Title == "Assay Exploratory Report — 2026-11-15" &&
            c.Request.BaseBranch == "main" &&
            c.Request.Description == "# Exploratory Test Report\n\nObserved validation issue." &&
            c.Request.Origin == TaskOrigin.UntrustedIntake);
        Assert.Contains(triageService.Calls, c =>
            c.Workspace.Id == workspace.Id &&
            c.Request.Title == "Assay Report #1115-1435" &&
            c.Request.BaseBranch == "release/v2" &&
            c.Request.Description == "# Minimal Report" &&
            c.Request.Origin == TaskOrigin.UntrustedIntake);
    }

    [Fact]
    public async Task PostIdeation_ValidatesWorkspaceAndPayload_AndStagesCompanionIdeationDraft()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "NightCodeForge",
            RepositoryPath = @"C:\repos\ncf",
            DefaultBaseBranch = "release/v2"
        });
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2026, 11, 15, 16, 45, 0, TimeSpan.Zero));
        var triageService = new RecordingTriageService();

        await using var host = await CreateHostAsync(
            workspaceRepo: workspaceRepo,
            timeProvider: fixedTime,
            triageService: triageService);
        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        // 1. Unknown workspace -> 404 JSON
        var notFoundResp = await client.PostAsJsonAsync(
            "/api/companion/workspaces/999/ideation",
            new { title = "Bug", reportMarkdown = "# Bug" });
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
        Assert.Equal("application/json", notFoundResp.Content.Headers.ContentType?.MediaType);

        // 2. Blank reportMarkdown -> 400 JSON
        var badReqResp = await client.PostAsJsonAsync(
            $"/api/companion/workspaces/{workspace.Id}/ideation",
            new { title = "Blank", reportMarkdown = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, badReqResp.StatusCode);
        Assert.Equal("application/json", badReqResp.Content.Headers.ContentType?.MediaType);

        // 3. Malformed JSON -> 400 JSON
        var malformedResp = await client.PostAsync(
            $"/api/companion/workspaces/{workspace.Id}/ideation",
            new StringContent("{bad", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformedResp.StatusCode);
        Assert.Equal("application/json", malformedResp.Content.Headers.ContentType?.MediaType);

        // 4. Valid payload -> 201 Created with ideationUrl and no automatic triage trigger
        var createdResp = await client.PostAsJsonAsync(
            $"/api/companion/workspaces/{workspace.Id}/ideation",
            new
            {
                title = "  Assay Ideation Session — Cart UX  ",
                reportMarkdown = "# Exploratory Report\n\nCart discount behavior needs design discussion.",
                baseBranch = "  develop ",
                sessionDirectoryPath = @"  C:\sessions\2026-11-15_16-45  "
            });
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);

        using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync());
        var draftId = createdDoc.RootElement.GetProperty("draftId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(draftId));
        Assert.Equal(workspace.Id, createdDoc.RootElement.GetProperty("workspaceId").GetInt32());
        Assert.Equal("Assay Ideation Session — Cart UX", createdDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal($"/w/{workspace.Id}/ideation?draftId={draftId}", createdDoc.RootElement.GetProperty("ideationUrl").GetString());
        Assert.Equal($"/w/{workspace.Id}/ideation?draftId={draftId}", createdResp.Headers.Location?.ToString());

        var draftStore = host.Services.GetRequiredService<ICompanionTriageDraftStore>();
        var storedDraft = draftStore.GetDraft(draftId);
        Assert.NotNull(storedDraft);
        Assert.Equal("develop", storedDraft.BaseBranch);
        Assert.Equal(@"C:\sessions\2026-11-15_16-45", storedDraft.SessionDirectoryPath);
        Assert.Null(storedDraft.ExecutionTask);

        // 5. Fallback title & baseBranch
        var fallbackResp = await client.PostAsync(
            $"/api/companion/workspaces/{workspace.Id}/ideation",
            new StringContent("{\"reportMarkdown\":\"# Minimal Ideation\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, fallbackResp.StatusCode);
        using var fallbackDoc = JsonDocument.Parse(await fallbackResp.Content.ReadAsStringAsync());
        Assert.Equal("Assay Report #1115-1645", fallbackDoc.RootElement.GetProperty("title").GetString());

        Assert.Empty(triageService.Calls);
    }

    [Fact]
    public async Task PostTriage_WhenBackgroundTriageFaults_CompletesRunHandleWithZero_AndFaultsBothTasksCleanly()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Faulting Repo",
            RepositoryPath = @"C:\repos\fault",
            DefaultBaseBranch = "main"
        });

        var failingTriageService = new FaultingTriageService("Simulated companion triage failure");
        await using var host = await CreateHostAsync(
            workspaceRepo: workspaceRepo,
            triageService: failingTriageService);
        using var client = CreateClient(host);
        var validKey = host.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);

        bool unobservedObserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, args) =>
        {
            unobservedObserved = true;
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += unobservedHandler;

        try
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/companion/workspaces/{workspace.Id}/triage",
                new { title = "Fault case", reportMarkdown = "# Crash\nDetails" });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var draftId = doc.RootElement.GetProperty("draftId").GetString();
            var draftStore = host.Services.GetRequiredService<ICompanionTriageDraftStore>();
            var storedDraft = draftStore.GetDraft(draftId);

            Assert.NotNull(storedDraft);
            Assert.NotNull(storedDraft.RunHandle);
            Assert.NotNull(storedDraft.SessionExecutionTask);
            Assert.NotNull(storedDraft.ExecutionTask);

            var resolvedTaskId = await storedDraft.RunHandle.TriageTaskIdTask;
            Assert.Equal(0, resolvedTaskId);

            var sessionEx = await Assert.ThrowsAsync<InvalidOperationException>(() => storedDraft.SessionExecutionTask);
            Assert.Equal("Simulated companion triage failure", sessionEx.Message);

            var execEx = await Assert.ThrowsAsync<InvalidOperationException>(() => storedDraft.ExecutionTask);
            Assert.Equal("Simulated companion triage failure", execEx.Message);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(unobservedObserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= unobservedHandler;
        }
    }

    private async Task<WebApplication> CreateHostAsync(
        InMemoryWorkspaceRepository? workspaceRepo = null,
        InMemoryTaskRepository? taskRepo = null,
        TimeProvider? timeProvider = null,
        Action<LlmProviderOptions>? configureLlm = null,
        ITriageService? triageService = null,
        IApiKeySecretStore? secretStore = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        builder.Services.AddSingleton(effectiveTimeProvider);
        builder.Services.AddSingleton<IKeyProtector>(_ => KeyProtectorFactory.CreatePlatformDefault());
        builder.Services.AddSingleton<IApiKeyStore>(sp => new ApiKeyStore(
            Path.Combine(_tempRoot, "config"),
            sp.GetRequiredService<IKeyProtector>(),
            sp.GetRequiredService<ILogger<ApiKeyStore>>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ICompanionTriageDraftStore>(sp =>
            new CompanionTriageDraftStore(sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IApiKeySecretStore>(secretStore ?? new FakeApiKeySecretStore());
        builder.Services.AddSingleton<IWorkspaceRepository>(workspaceRepo ?? new InMemoryWorkspaceRepository());
        builder.Services.AddSingleton<ITaskRepository>(taskRepo ?? new InMemoryTaskRepository());
        builder.Services.AddSingleton<ITriageService>(triageService ?? new RecordingTriageService());
        builder.Services.Configure<LlmProviderOptions>(opts =>
        {
            configureLlm?.Invoke(opts);
        });

        var app = builder.Build();
        app.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();

        // Register StatusCodePagesMiddleware just like Program.cs to prove error JSON bodies are never rewritten to HTML.
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.MapGet("/not-found", () => Results.Content("<html>not-found</html>", "text/html"));
        app.MapCompanionEndpoints();

        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task AssertUnauthorizedJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(h => h.Scheme));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            CompanionApiKeyEndpointFilter.UnauthorizedErrorMessage,
            doc.RootElement.GetProperty("error").GetString());
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class RecordingTriageService : ITriageService
    {
        public const int SimulatedTriageTaskId = 42;

        public ConcurrentQueue<(Workspace Workspace, TriageReportRequest Request)> Calls { get; } = new();

        public async Task<IReadOnlyList<ForgeTask>> TriageExternalReportAsync(
            Workspace workspace,
            TriageReportRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await RunTriageSessionAsync(workspace, request, onTriageTaskCreated: null, cancellationToken);
            return result.Tasks;
        }

        public Task<TriageSessionResult> RunTriageSessionAsync(
            Workspace workspace,
            TriageReportRequest request,
            Action<int>? onTriageTaskCreated = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((workspace, request));
            onTriageTaskCreated?.Invoke(SimulatedTriageTaskId);
            return Task.FromResult(new TriageSessionResult(
                TriageTaskId: SimulatedTriageTaskId,
                Tasks: Array.Empty<ForgeTask>(),
                CreatedTaskIds: new HashSet<int>(),
                UpdatedTaskIds: new HashSet<int>(),
                RemovedTaskIds: new HashSet<int>()));
        }
    }

    private sealed class FaultingTriageService : ITriageService
    {
        private readonly string _message;

        public FaultingTriageService(string message) => _message = message;

        public Task<IReadOnlyList<ForgeTask>> TriageExternalReportAsync(
            Workspace workspace,
            TriageReportRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<ForgeTask>>(new InvalidOperationException(_message));

        public Task<TriageSessionResult> RunTriageSessionAsync(
            Workspace workspace,
            TriageReportRequest request,
            Action<int>? onTriageTaskCreated = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TriageSessionResult>(new InvalidOperationException(_message));
    }
}
