using Blocks.Genesis;
using System.Diagnostics.Metrics;
using System.Net;
using XUnitTest.TestDoubles;

namespace XUnitTest.Ai;

/// <summary>I02-WP6: both providers against the scripted OpenAI / Azure-format stub. No real model is called (rule 13).</summary>
public class ChatCompletionClientTests
{
    private const string FakeKey = "fake-key-not-a-secret";

    private static readonly ChatMessage[] Messages =
    {
        new("system", "Reply with one word."),
        new("user", "Say hello.")
    };

    private static AzureOpenAiChatClient Azure(ScriptedChatStub stub, string? model = null, double? temperature = null, string? key = FakeKey) =>
        new(new HttpClient(), new AiOptions
        {
            Provider = AiProvider.AzureOpenAi,
            Endpoint = stub.BaseUrl + "/openai/deployments/demo/chat/completions?api-version=2024-10-21",
            Model = model,
            Temperature = temperature
        }, () => key);

    private static OpenAiCompatibleChatClient Compatible(ScriptedChatStub stub, string? endpointPath = "/v1/chat/completions", string? model = "stub-model", double? temperature = null, string? key = FakeKey) =>
        new(new HttpClient(), new AiOptions
        {
            Provider = AiProvider.OpenAiCompatible,
            Endpoint = stub.BaseUrl + endpointPath,
            Model = model,
            Temperature = temperature
        }, () => key);

    [Fact]
    public async Task Azure_sends_api_key_header_and_no_bearer_and_reads_text_and_usage()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("Hello", promptTokens: 21, completionTokens: 4));

        var result = await Azure(stub, temperature: 0.2).CompleteAsync(Messages);

        Assert.Equal("Hello", result.Text);
        Assert.Equal(new ChatTokenUsage(21, 4), result.Usage);
        Assert.Equal(25, result.Usage!.TotalTokens);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/openai/deployments/demo/chat/completions", request.Path);
        Assert.Equal("?api-version=2024-10-21", request.Query);
        Assert.Equal(FakeKey, request.Headers["api-key"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.Equal(0.2, request.Json.GetProperty("temperature").GetDouble());
        Assert.False(request.Json.TryGetProperty("model", out _));   // optional for Azure: the URL names the deployment
        var sent = request.Json.GetProperty("messages");
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Equal("Say hello.", sent[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Azure_response_with_content_filter_fields_is_read_like_the_openai_format()
    {
        // Azure adds prompt_filter_results and per-choice content_filter_results to the OpenAI shape, and may leave out "model".
        const string azureBody = "{\"id\":\"chatcmpl-az\",\"object\":\"chat.completion\",\"created\":1700000000,"
            + "\"prompt_filter_results\":[{\"prompt_index\":0,\"content_filter_results\":{\"hate\":{\"filtered\":false,\"severity\":\"safe\"}}}],"
            + "\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Guten Tag\"},"
            + "\"content_filter_results\":{\"hate\":{\"filtered\":false,\"severity\":\"safe\"}}}],"
            + "\"usage\":{\"prompt_tokens\":14,\"completion_tokens\":3,\"total_tokens\":17}}";
        using var stub = new ScriptedChatStub(new ScriptedChatStub.Reply(200, azureBody));

        var result = await Azure(stub, model: "my-deployment").CompleteAsync(Messages);

        Assert.Equal("Guten Tag", result.Text);
        Assert.Equal(new ChatTokenUsage(14, 3), result.Usage);
        Assert.Equal("my-deployment", result.Model);
    }

    [Fact]
    public async Task OpenAiCompatible_sends_bearer_header_and_no_api_key_and_the_model()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("Hi", promptTokens: 9, completionTokens: 2));

        var result = await Compatible(stub).CompleteAsync(Messages);

        Assert.Equal("Hi", result.Text);
        Assert.Equal(new ChatTokenUsage(9, 2), result.Usage);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer " + FakeKey, request.Headers["authorization"]);
        Assert.False(request.Headers.ContainsKey("api-key"));
        Assert.Equal("stub-model", request.Json.GetProperty("model").GetString());
        Assert.False(request.Json.TryGetProperty("temperature", out _));   // none configured: provider default
    }

    [Fact]
    public async Task No_model_configured_means_no_model_in_the_request()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("ok"));

        await Compatible(stub, model: null).CompleteAsync(Messages);

        Assert.False(Assert.Single(stub.Requests).Json.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task OpenAiCompatible_adds_the_completions_path_to_a_base_url()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("ok"));

        await Compatible(stub, endpointPath: "/v1/").CompleteAsync(Messages);

        Assert.Equal("/v1/chat/completions", Assert.Single(stub.Requests).Path);
    }

    [Fact]
    public async Task Call_options_override_the_configured_model_and_temperature()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("ok"));

        await Compatible(stub, temperature: 0.9).CompleteAsync(Messages, new ChatCompletionOptions { Model = "other-model", Temperature = 0.1, MaxTokens = 50 });

        var json = Assert.Single(stub.Requests).Json;
        Assert.Equal("other-model", json.GetProperty("model").GetString());
        Assert.Equal(0.1, json.GetProperty("temperature").GetDouble());
        Assert.Equal(50, json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task A_tool_call_reply_has_no_text_but_keeps_its_usage()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.ToolCall("lookup", "{\"q\":\"x\"}"));

        var result = await Compatible(stub).CompleteAsync(Messages);

        Assert.Null(result.Text);
        Assert.Equal(new ChatTokenUsage(5, 3), result.Usage);
    }

    [Fact]
    public async Task An_empty_answer_is_text_not_null()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion(""));

        Assert.Equal("", (await Azure(stub).CompleteAsync(Messages)).Text);
    }

    [Fact]
    public async Task A_reply_without_a_usage_block_has_null_usage()
    {
        using var stub = new ScriptedChatStub(new ScriptedChatStub.Reply(200, "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}"));

        var result = await Compatible(stub).CompleteAsync(Messages);

        Assert.Equal("x", result.Text);
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_error_response_throws_with_status_and_never_the_body(bool azure)
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Error(401, "secret-detail-from-provider"));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(() =>
            (azure ? (IChatCompletionClient)Azure(stub) : Compatible(stub)).CompleteAsync(Messages));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Null(ex.RetryAfter);
        Assert.DoesNotContain("secret-detail-from-provider", ex.Message);
    }

    [Fact]
    public async Task A_429_carries_azures_retry_after_ms()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Error(429, headers: new Dictionary<string, string> { ["retry-after-ms"] = "1500" }));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(() => Azure(stub).CompleteAsync(Messages));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ex.RetryAfter);
    }

    [Fact]
    public async Task A_429_carries_the_standard_retry_after_seconds()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Error(429, headers: new Dictionary<string, string> { ["Retry-After"] = "7" }));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(() => Compatible(stub).CompleteAsync(Messages));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_throws_a_completion_exception()
    {
        using var stub = new ScriptedChatStub(new ScriptedChatStub.Reply(200, "<html>gateway</html>"));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(() => Compatible(stub).CompleteAsync(Messages));

        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
    }

    [Fact]
    public async Task Missing_endpoint_or_key_fail_before_any_request()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("never"));

        await Assert.ThrowsAsync<ChatCompletionException>(() =>
            new AzureOpenAiChatClient(new HttpClient(), new AiOptions { Endpoint = null }, () => FakeKey).CompleteAsync(Messages));
        await Assert.ThrowsAsync<ChatCompletionException>(() => Azure(stub, key: null).CompleteAsync(Messages));

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_cancelled_call_throws_operation_cancelled()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Azure(stub).CompleteAsync(Messages, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Token_counts_are_recorded_as_a_metric_per_provider_and_type()
    {
        var recorded = new List<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AiMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (recorded)
                recorded.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();

        // A unique model name keeps this test's measurements apart from other tests running in parallel.
        var model = "metric-model-" + Guid.NewGuid().ToString("N");
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("a", 30, 12, model), ScriptedChatStub.Error(500));
        await Azure(stub, model: model).CompleteAsync(Messages);
        await Assert.ThrowsAsync<ChatCompletionException>(() => Azure(stub, model: model).CompleteAsync(Messages));

        List<(string Instrument, long Value, Dictionary<string, object?> Tags)> mine;
        lock (recorded)
            mine = recorded.Where(r => (string?)r.Tags["model"] == model).ToList();

        long Tokens(string type) => mine
            .Where(r => r.Instrument == AiMetrics.TokensInstrument && (string?)r.Tags["token_type"] == type && (string?)r.Tags["provider"] == "azure-openai")
            .Sum(r => r.Value);
        Assert.Equal(30, Tokens("prompt"));
        Assert.Equal(12, Tokens("completion"));
        Assert.Equal(1, mine.Count(r => r.Instrument == AiMetrics.RequestsInstrument && (string?)r.Tags["outcome"] == "ok"));
        Assert.Equal(1, mine.Count(r => r.Instrument == AiMetrics.RequestsInstrument && (string?)r.Tags["outcome"] == "error"));
    }

    [Fact]
    public void The_factory_builds_the_client_for_the_provider()
    {
        Assert.IsType<AzureOpenAiChatClient>(ChatCompletionClients.Create(new HttpClient(), new AiOptions { Provider = AiProvider.AzureOpenAi }));
        Assert.IsType<OpenAiCompatibleChatClient>(ChatCompletionClients.Create(new HttpClient(), new AiOptions { Provider = AiProvider.OpenAiCompatible }));
    }
}
