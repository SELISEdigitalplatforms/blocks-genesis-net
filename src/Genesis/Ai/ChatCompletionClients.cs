#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis;

/// <summary>Azure OpenAI: the key goes in the <c>api-key</c> header and the endpoint is the full deployment URL.</summary>
public sealed class AzureOpenAiChatClient : ChatCompletionClientBase
{
    public AzureOpenAiChatClient(HttpClient httpClient, AiOptions options, Func<string?>? apiKeyProvider = null, ILogger? logger = null, TimeProvider? timeProvider = null)
        : base(httpClient, options, apiKeyProvider, logger, timeProvider)
    {
    }

    public override string ProviderName => "azure-openai";

    protected override void ApplyApiKey(HttpRequestMessage request, string apiKey) =>
        request.Headers.TryAddWithoutValidation("api-key", apiKey);

    protected override Uri BuildUri(string endpoint) => new(endpoint);
}

/// <summary>OpenAI and servers with the same API: the key goes in <c>Authorization: Bearer</c>. A base URL such as <c>https://host/v1</c> gets <c>/chat/completions</c> added.</summary>
public sealed class OpenAiCompatibleChatClient : ChatCompletionClientBase
{
    private const string CompletionsPath = "/chat/completions";

    public OpenAiCompatibleChatClient(HttpClient httpClient, AiOptions options, Func<string?>? apiKeyProvider = null, ILogger? logger = null, TimeProvider? timeProvider = null)
        : base(httpClient, options, apiKeyProvider, logger, timeProvider)
    {
    }

    public override string ProviderName => "openai-compatible";

    protected override void ApplyApiKey(HttpRequestMessage request, string apiKey) =>
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

    protected override Uri BuildUri(string endpoint)
    {
        var uri = new Uri(endpoint);
        return uri.AbsolutePath.EndsWith(CompletionsPath, StringComparison.OrdinalIgnoreCase)
            ? uri
            : new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + CompletionsPath }.Uri;
    }
}

public static class ChatCompletionClients
{
    /// <summary>Builds the client for <see cref="AiOptions.Provider"/>. <paramref name="apiKeyProvider"/> is asked for the key on every call; when null, no key is available and a completion fails with <see cref="ChatCompletionException"/>.</summary>
    public static IChatCompletionClient Create(
        HttpClient httpClient,
        AiOptions options,
        Func<string?>? apiKeyProvider = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null) =>
        options.Provider switch
        {
            AiProvider.AzureOpenAi => new AzureOpenAiChatClient(httpClient, options, apiKeyProvider, logger, timeProvider),
            AiProvider.OpenAiCompatible => new OpenAiCompatibleChatClient(httpClient, options, apiKeyProvider, logger, timeProvider),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown AI provider.")
        };

    /// <summary>Registers <see cref="IChatCompletionClient"/>, built from the <c>Ai</c> section (and the old keys) of the configuration.</summary>
    public static IServiceCollection AddBlocksChatCompletionClient(
        this IServiceCollection services,
        AiProvider legacyDefaultProvider = AiProvider.OpenAiCompatible)
    {
        services.AddHttpClient(nameof(IChatCompletionClient));
        services.AddSingleton<IChatCompletionClient>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = AiOptions.FromConfiguration(configuration, legacyDefaultProvider);
            return Create(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IChatCompletionClient)),
                options,
                () => options.ResolveApiKey(configuration),
                sp.GetService<ILoggerFactory>()?.CreateLogger(nameof(IChatCompletionClient)));
        });
        return services;
    }
}
