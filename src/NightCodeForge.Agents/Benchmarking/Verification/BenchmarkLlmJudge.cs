using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Inference;
using OllamaSharp;
using OpenAI;

namespace NightCodeForge.Agents.Benchmarking.Verification;

/// <summary>
/// Creates an <see cref="IChatClient"/> configured for an optional benchmark LLM-as-judge endpoint.
/// </summary>
public interface IBenchmarkJudgeClientFactory
{
    IChatClient CreateJudgeClient(BenchmarkJudgeConfig config);
}

/// <summary>
/// Default <see cref="IBenchmarkJudgeClientFactory"/> supporting Ollama, OllamaRouter, and OpenAI-compatible judge endpoints.
/// </summary>
public sealed class DefaultBenchmarkJudgeClientFactory : IBenchmarkJudgeClientFactory
{
    private const string DefaultOllamaEndpoint = "http://localhost:11434";

    public IChatClient CreateJudgeClient(BenchmarkJudgeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.ModelName))
        {
            throw new BenchmarkVerificationException("BenchmarkJudgeConfig.ModelName cannot be empty.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(10, config.TimeoutSeconds));
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? DefaultOllamaEndpoint
            : config.Endpoint.Trim();

        if (config.ProviderType is LlmProviderType.Ollama or LlmProviderType.OllamaRouter)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new BenchmarkVerificationException($"Invalid judge endpoint URI '{endpoint}'.");
            }

            var httpClient = new HttpClient
            {
                BaseAddress = endpointUri,
                Timeout = timeout
            };
            return new OwnedHttpClientChatClient(new OllamaApiClient(httpClient, config.ModelName.Trim()), httpClient);
        }

        if (config.ProviderType == LlmProviderType.OpenAiCompatible)
        {
            if (string.IsNullOrWhiteSpace(config.Endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                throw new BenchmarkVerificationException($"Valid judge endpoint URL is required for OpenAiCompatible judge provider (got '{config.Endpoint ?? "<null>"}').");
            }

            var apiKey = !string.IsNullOrWhiteSpace(config.ApiKey)
                ? config.ApiKey.Trim()
                : (Environment.GetEnvironmentVariable("NCF_BENCHMARK_API_KEY")
                    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? KnownDefaultModels.LocalAuthBypassKey);

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint.TrimEnd('/') + "/"),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = timeout
            };

            var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            return openAiClient.GetChatClient(config.ModelName.Trim()).AsIChatClient();
        }

        throw new BenchmarkVerificationException($"Unsupported judge provider type '{config.ProviderType}'.");
    }

    private sealed class OwnedHttpClientChatClient : DelegatingChatClient
    {
        private readonly HttpClient _httpClient;

        public OwnedHttpClientChatClient(IChatClient innerClient, HttpClient httpClient)
            : base(innerClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _httpClient.Dispose();
            }
        }
    }
}

/// <summary>
/// Optional qualitative LLM-as-judge evaluator for Code Review and ForgeTask Review benchmark runs.
/// </summary>
public sealed class BenchmarkLlmJudge : IBenchmarkLlmJudge
{
    private readonly IBenchmarkJudgeClientFactory _clientFactory;
    private readonly ILogger<BenchmarkLlmJudge> _logger;

    public BenchmarkLlmJudge(
        IBenchmarkJudgeClientFactory clientFactory,
        ILogger<BenchmarkLlmJudge> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkJudgeResult> EvaluateCodeReviewAsync(
        BenchmarkInstance instance,
        BenchmarkAgentOutcome outcome,
        BenchmarkJudgeConfig judgeConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(judgeConfig);

        var prompt = BuildCodeReviewJudgePrompt(instance, outcome);
        return await EvaluatePromptAsync(instance.InstanceId, prompt, judgeConfig, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkJudgeResult> EvaluateTaskReviewAsync(
        BenchmarkInstance instance,
        BenchmarkAgentOutcome outcome,
        BenchmarkJudgeConfig judgeConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(judgeConfig);

        var prompt = BuildTaskReviewJudgePrompt(instance, outcome);
        return await EvaluatePromptAsync(instance.InstanceId, prompt, judgeConfig, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BenchmarkJudgeResult> EvaluatePromptAsync(
        string instanceId,
        string userPrompt,
        BenchmarkJudgeConfig judgeConfig,
        CancellationToken cancellationToken)
    {
        using var client = _clientFactory.CreateJudgeClient(judgeConfig);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, judgeConfig.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a strict engineering benchmark evaluator. Return ONLY valid JSON with exact fields {\"score\": <number 0.0 to 1.0>, \"rationale\": \"<concise explanation>\"}."),
            new(ChatRole.User, userPrompt)
        };

        var options = new ChatOptions
        {
            ModelId = judgeConfig.ModelName,
            Temperature = (float)judgeConfig.Temperature
        };

        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, options, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM judge invocation failed for benchmark instance '{InstanceId}' using model '{Model}'.",
                instanceId, judgeConfig.ModelName);
            throw new BenchmarkVerificationException(
                $"LLM judge invocation failed for '{instanceId}' (model '{judgeConfig.ModelName}'): {ex.Message}",
                ex,
                instanceId);
        }

        var responseText = response.Text;
        return ParseJudgeResponse(responseText, instanceId);
    }

    internal static BenchmarkJudgeResult ParseJudgeResponse(string? responseText, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new BenchmarkVerificationException(
                $"LLM judge returned an empty response for instance '{instanceId}'.",
                instanceId);
        }

        var jsonText = ExtractJsonObject(responseText);
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BenchmarkVerificationException(
                    $"LLM judge response for '{instanceId}' is not a JSON object.",
                    instanceId);
            }

            if (!doc.RootElement.TryGetProperty("score", out var scoreProp) ||
                scoreProp.ValueKind != JsonValueKind.Number ||
                !scoreProp.TryGetDouble(out var score))
            {
                throw new BenchmarkVerificationException(
                    $"LLM judge response for '{instanceId}' is missing numeric 'score'.",
                    instanceId);
            }

            if (double.IsNaN(score) || double.IsInfinity(score) || score < 0.0 || score > 1.0)
            {
                throw new BenchmarkVerificationException(
                    $"LLM judge score {score} for '{instanceId}' is outside [0.0, 1.0].",
                    instanceId);
            }

            if (!doc.RootElement.TryGetProperty("rationale", out var rationaleProp) ||
                rationaleProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(rationaleProp.GetString()))
            {
                throw new BenchmarkVerificationException(
                    $"LLM judge response for '{instanceId}' is missing non-empty 'rationale'.",
                    instanceId);
            }

            return new BenchmarkJudgeResult(score, rationaleProp.GetString()!.Trim());
        }
        catch (JsonException ex)
        {
            throw new BenchmarkVerificationException(
                $"LLM judge returned malformed JSON for '{instanceId}': {ex.Message}",
                ex,
                instanceId);
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }

            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    private static string BuildCodeReviewJudgePrompt(BenchmarkInstance instance, BenchmarkAgentOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Evaluate the quality of a Code Review & Fix agent run on a 0.0 to 1.0 scale.");
        sb.AppendLine("Criteria: review accuracy against expected verdict, defect coverage vs expected findings, absence of false positives, and quality of any minor fix patch.");
        sb.AppendLine();
        sb.AppendLine($"Problem Statement:\n{instance.ProblemStatement}");
        if (!string.IsNullOrWhiteSpace(instance.TaskSpecification))
        {
            sb.AppendLine($"\nTask Specification:\n{instance.TaskSpecification}");
        }
        sb.AppendLine($"\nCandidate Patch Reviewed:\n{instance.CandidatePatch}");
        sb.AppendLine($"\nExpected Verdict: {instance.ExpectedVerdict}");
        AppendExpectedFindings(sb, instance.ExpectedFindings);
        sb.AppendLine($"\nActual Agent Summary:\n{outcome.Summary ?? "<none>"}");
        if (!string.IsNullOrWhiteSpace(outcome.ReviewerFeedback))
        {
            sb.AppendLine($"\nActual Reviewer Feedback (request_changes):\n{outcome.ReviewerFeedback}");
        }
        if (!string.IsNullOrWhiteSpace(outcome.ModelPatch))
        {
            sb.AppendLine($"\nActual Minor Fix Patch Produced:\n{outcome.ModelPatch}");
        }
        return sb.ToString();
    }

    private static string BuildTaskReviewJudgePrompt(BenchmarkInstance instance, BenchmarkAgentOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Evaluate the quality of a ForgeTask Review (Staff Architect) agent run on a 0.0 to 1.0 scale.");
        sb.AppendLine("Criteria: specification completeness, architectural accuracy, preservation of existing requirements, and relevance/precision of clarification questions (if asked).");
        sb.AppendLine();
        sb.AppendLine($"Problem Statement:\n{instance.ProblemStatement}");
        sb.AppendLine($"\nOriginal Task Specification:\n{instance.TaskSpecification}");
        sb.AppendLine($"\nExpected Verdict: {instance.ExpectedVerdict}");
        if (!string.IsNullOrWhiteSpace(instance.ExpectedDifficulty))
        {
            sb.AppendLine($"Expected Difficulty: {instance.ExpectedDifficulty}");
        }
        AppendExpectedFindings(sb, instance.ExpectedFindings);
        sb.AppendLine($"\nActual Run Status: {outcome.RunStatus}");
        if (outcome.PendingQuestions.Count > 0)
        {
            sb.AppendLine("\nQuestions Asked via 'ask':");
            foreach (var q in outcome.PendingQuestions)
            {
                sb.AppendLine($"- {q.Prompt} (Rationale: {q.Rationale})");
            }
        }
        if (!string.IsNullOrWhiteSpace(outcome.UpdatedTaskMarkdown))
        {
            sb.AppendLine($"\nUpdated Task Specification:\n{outcome.UpdatedTaskMarkdown}");
        }
        sb.AppendLine($"\nAgent Summary:\n{outcome.Summary ?? "<none>"}");
        return sb.ToString();
    }

    private static void AppendExpectedFindings(StringBuilder sb, IReadOnlyList<BenchmarkExpectedFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        sb.AppendLine("\nExpected Findings / Anchors:");
        foreach (var f in findings)
        {
            sb.AppendLine($"- file={f.FilePath ?? "*"}; symbol/line={f.SymbolOrLineHint ?? "*"}; severity={f.Severity ?? "*"}; summary={f.Summary ?? ""}; keywords=[{string.Join(", ", f.Keywords)}]");
        }
    }
}
