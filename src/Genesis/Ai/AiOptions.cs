#nullable enable
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace Blocks.Genesis;

public enum AiProvider
{
    /// <summary>Azure OpenAI: the key goes in the <c>api-key</c> header.</summary>
    AzureOpenAi,

    /// <summary>OpenAI or any server with the same Chat Completions API: the key goes in <c>Authorization: Bearer</c>.</summary>
    OpenAiCompatible
}

/// <summary>
/// The <c>Ai</c> configuration section: <c>Provider</c> (<c>AzureOpenAi</c> or <c>OpenAiCompatible</c>), <c>Endpoint</c>,
/// <c>Model</c>, <c>ApiKeyReference</c> and <c>Temperature</c>.
/// The old keys are still read when the section leaves a value out: <c>AzureAIEndpoint</c> and <c>AiCompletionUrl</c>
/// (endpoint) and <c>ChatGptTemperature</c> (temperature). The old encrypted key, <c>AzureOpenAIEncryptedSecret</c>, stays with the caller,
/// which decrypts it and hands the plain key to the client.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";
    public const string LegacyAzureEndpointKey = "AzureAIEndpoint";
    public const string LegacyEndpointKey = "AiCompletionUrl";
    public const string LegacyTemperatureKey = "ChatGptTemperature";

    public AiProvider Provider { get; set; } = AiProvider.OpenAiCompatible;

    /// <summary>The Chat Completions URL. For Azure, the full deployment URL with <c>api-version</c>. For OpenAI-compatible servers, either the full URL or a base URL (<c>https://host/v1</c>), to which <c>/chat/completions</c> is added.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name sent in the request. Left out of the request when empty: Azure takes the deployment from the URL, and an OpenAI-compatible server then answers with its own error or default.</summary>
    public string? Model { get; set; }

    /// <summary>The name of the configuration key (or vault secret surfaced as configuration) whose value is the API key. The key itself never goes in the <c>Ai</c> section.</summary>
    public string? ApiKeyReference { get; set; }

    public double? Temperature { get; set; }

    /// <summary>
    /// Reads the <c>Ai</c> section, then the old keys. <paramref name="legacyDefaultProvider"/> applies when the section names no provider
    /// and no Azure endpoint is set. <paramref name="extraAzureEndpoint"/> is an Azure endpoint the caller holds outside configuration (a vault secret),
    /// used after <c>AzureAIEndpoint</c> and before <c>AiCompletionUrl</c>.
    /// </summary>
    public static AiOptions FromConfiguration(
        IConfiguration configuration,
        AiProvider legacyDefaultProvider = AiProvider.OpenAiCompatible,
        string? extraAzureEndpoint = null)
    {
        var legacyAzureEndpoint = FirstNonEmpty(configuration[LegacyAzureEndpointKey], extraAzureEndpoint);

        var options = new AiOptions
        {
            Endpoint = FirstNonEmpty(configuration["Ai:Endpoint"], legacyAzureEndpoint, configuration[LegacyEndpointKey]),
            Model = FirstNonEmpty(configuration["Ai:Model"]),
            ApiKeyReference = FirstNonEmpty(configuration["Ai:ApiKeyReference"]),
            Temperature = ParseDouble(FirstNonEmpty(configuration["Ai:Temperature"], configuration[LegacyTemperatureKey]))
        };

        var provider = FirstNonEmpty(configuration["Ai:Provider"]);
        options.Provider = provider != null
            ? ParseProvider(provider)
            : legacyAzureEndpoint != null ? AiProvider.AzureOpenAi : legacyDefaultProvider;
        return options;
    }

    /// <summary>The API key named by <see cref="ApiKeyReference"/>, or null when no reference is set or it has no value.</summary>
    public string? ResolveApiKey(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(ApiKeyReference) ? null : FirstNonEmpty(configuration[ApiKeyReference]);

    public static AiProvider ParseProvider(string value) =>
        value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "azure" or "azureopenai" => AiProvider.AzureOpenAi,
            "openai" or "openaicompatible" or "compatible" => AiProvider.OpenAiCompatible,
            _ => throw new ArgumentException($"Ai:Provider '{value}' is not supported. Use AzureOpenAi or OpenAiCompatible.")
        };

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
