using Blocks.Genesis;

namespace XUnitTest.Ai;

/// <summary>
/// One test per provider style against a real endpoint (rule 13, D4). Excluded from ordinary runs by
/// <c>--filter "Category!=RealModel"</c>. Run on its own without the <c>TEST_MODEL_*</c> variables, it FAILs and names D4: it never skips.
/// It sends one synthetic prompt and prints neither the key nor the endpoint.
/// </summary>
public class ChatCompletionRealModelTests
{
    private static (string Endpoint, string Key, string Name, string? ApiVersion) Required(string style)
    {
        var actualStyle = Environment.GetEnvironmentVariable("TEST_MODEL_STYLE");
        var endpoint = Environment.GetEnvironmentVariable("TEST_MODEL_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("TEST_MODEL_KEY");
        var name = Environment.GetEnvironmentVariable("TEST_MODEL_NAME");
        if (!string.Equals(actualStyle, style, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
        {
            Assert.Fail($"D4 pending: this test needs a real '{style}' endpoint. The owner writes TEST_MODEL_STYLE, TEST_MODEL_ENDPOINT, TEST_MODEL_KEY and TEST_MODEL_NAME " +
                        "into blocks-infra\\model-endpoint.env (runbook 13B), and the run is approved first (rule 13).");
        }
        return (endpoint!.TrimEnd('/'), key!, name!, Environment.GetEnvironmentVariable("TEST_MODEL_API_VERSION"));
    }

    private static readonly ChatMessage[] Prompt = { new("user", "Reply with the single word: pong") };

    [Fact]
    [Trait("Category", "RealModel")]
    public async Task AzureOpenAi_answers_and_reports_token_usage()
    {
        var (endpoint, key, name, apiVersion) = Required("azure-openai");
        var client = new AzureOpenAiChatClient(new HttpClient(), new AiOptions
        {
            Provider = AiProvider.AzureOpenAi,
            Endpoint = $"{endpoint}/openai/deployments/{name}/chat/completions?api-version={apiVersion}"
        }, () => key);

        var result = await client.CompleteAsync(Prompt, new ChatCompletionOptions { MaxTokens = 16 });

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(result.Usage!.PromptTokens > 0);
    }

    [Fact]
    [Trait("Category", "RealModel")]
    public async Task OpenAiCompatible_answers_and_reports_token_usage()
    {
        var (endpoint, key, name, _) = Required("openai");
        var client = new OpenAiCompatibleChatClient(new HttpClient(), new AiOptions
        {
            Provider = AiProvider.OpenAiCompatible,
            Endpoint = endpoint,
            Model = name
        }, () => key);

        var result = await client.CompleteAsync(Prompt, new ChatCompletionOptions { MaxTokens = 16 });

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(result.Usage!.PromptTokens > 0);
    }
}
