#nullable enable
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// The Chat Completions request and response shared by both providers. A provider differs only in how the key is sent
/// (<see cref="ApplyApiKey"/>) and how the endpoint is turned into a URL (<see cref="BuildUri"/>).
/// </summary>
public abstract class ChatCompletionClientBase : IChatCompletionClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    protected ChatCompletionClientBase(
        HttpClient httpClient,
        AiOptions options,
        Func<string?>? apiKeyProvider = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _apiKeyProvider = apiKeyProvider ?? (() => null);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected AiOptions Options { get; }

    /// <summary>Tag value for the <c>provider</c> metric dimension.</summary>
    public abstract string ProviderName { get; }

    protected abstract void ApplyApiKey(HttpRequestMessage request, string apiKey);

    protected abstract Uri BuildUri(string endpoint);

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var model = FirstNonEmpty(options?.Model, Options.Model);
        var metricModel = model ?? "unspecified";
        try
        {
            var result = await SendAsync(messages, options, model, cancellationToken).ConfigureAwait(false);
            AiMetrics.RecordRequest(ProviderName, result.Model ?? metricModel, ok: true);
            if (result.Usage is { } usage)
                AiMetrics.RecordUsage(ProviderName, result.Model ?? metricModel, usage);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            AiMetrics.RecordRequest(ProviderName, metricModel, ok: false);
            throw;
        }
    }

    private async Task<ChatCompletionResult> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions? options,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Options.Endpoint))
            throw new ChatCompletionException("Ai:Endpoint is not configured (the old keys AzureAIEndpoint and AiCompletionUrl are also read).");
        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ChatCompletionException("The AI API key is not available (Ai:ApiKeyReference has no value).");

        var payload = new Dictionary<string, object?>();
        if (model != null)
            payload["model"] = model;
        payload["messages"] = messages.Select(m => new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content }).ToList();
        if ((options?.Temperature ?? Options.Temperature) is { } temperature)
            payload["temperature"] = temperature;
        if (options?.MaxTokens is { } maxTokens)
            payload["max_tokens"] = maxTokens;

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(Options.Endpoint))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Remove("Authorization");
        request.Headers.Remove("api-key");
        ApplyApiKey(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
            _logger?.LogWarning("AI completion failed: provider {Provider}, host {EndpointHost}, status {StatusCode}",
                ProviderName, request.RequestUri?.Host, (int)response.StatusCode);
            throw new ChatCompletionException(
                $"The AI provider answered {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode, retryAfter);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body, model);
    }

    private ChatCompletionResult Parse(string body, string? requestedModel)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            string? text = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }

            ChatTokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
                && TryReadInt(usageElement, "prompt_tokens", out var promptTokens)
                && TryReadInt(usageElement, "completion_tokens", out var completionTokens))
            {
                usage = new ChatTokenUsage(promptTokens, completionTokens);
            }

            var responseModel = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : requestedModel;
            return new ChatCompletionResult(text, usage, responseModel);
        }
        catch (JsonException ex)
        {
            throw new ChatCompletionException("The AI provider's response was not valid JSON.", HttpStatusCode.OK, inner: ex);
        }
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    /// <summary>Azure's <c>retry-after-ms</c>, else the standard Retry-After (seconds or a date); null when absent.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date && date > now)
            return date - now;
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
