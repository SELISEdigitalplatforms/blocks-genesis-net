using Blocks.Genesis;

namespace XUnitTest.Ai;

/// <summary>
/// One test per provider style against a real endpoint (rule 13, D4). Excluded from ordinary runs by
/// <c>--filter "Category!=RealModel"</c>. Without the <c>TEST_MODEL_*</c> variables of its style, a test is skipped, so a CI run with no endpoint stays green (D4).
/// It sends one synthetic prompt and prints neither the key nor the endpoint.
/// </summary>
public class ChatCompletionRealModelTests
{
    private static (string Endpoint, string Key, string Name, string? ApiVersion) Required(string style)
    {
        Assert.True(RealModelFactAttribute.TryGetEndpoint(style, out var endpoint), $"No real '{style}' endpoint is configured.");
        return endpoint;
    }

    private static readonly ChatMessage[] Prompt = { new("user", "Reply with the single word: pong") };

    [RealModelFact("azure-openai")]
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

    [RealModelFact("openai")]
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
