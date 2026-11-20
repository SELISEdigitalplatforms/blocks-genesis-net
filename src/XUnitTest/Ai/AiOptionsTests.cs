using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XUnitTest.TestDoubles;

namespace XUnitTest.Ai;

public class AiOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void The_Ai_section_is_read()
    {
        var options = AiOptions.FromConfiguration(Config(
            ("Ai:Provider", "azure-openai"), ("Ai:Endpoint", "https://e.example/x"), ("Ai:Model", "m1"),
            ("Ai:ApiKeyReference", "MyKey"), ("Ai:Temperature", "0.3")));

        Assert.Equal(AiProvider.AzureOpenAi, options.Provider);
        Assert.Equal("https://e.example/x", options.Endpoint);
        Assert.Equal("m1", options.Model);
        Assert.Equal("MyKey", options.ApiKeyReference);
        Assert.Equal(0.3, options.Temperature);
    }

    [Theory]
    [InlineData("AzureOpenAi", AiProvider.AzureOpenAi)]
    [InlineData("azure", AiProvider.AzureOpenAi)]
    [InlineData("OpenAiCompatible", AiProvider.OpenAiCompatible)]
    [InlineData("openai", AiProvider.OpenAiCompatible)]
    public void Provider_names_are_forgiving(string name, AiProvider expected) =>
        Assert.Equal(expected, AiOptions.ParseProvider(name));

    [Fact]
    public void An_unknown_provider_is_an_error() =>
        Assert.Throws<ArgumentException>(() => AiOptions.FromConfiguration(Config(("Ai:Provider", "gemini"))));

    [Fact]
    public void Old_keys_are_the_fallback_and_an_azure_endpoint_selects_azure()
    {
        var options = AiOptions.FromConfiguration(Config(("AzureAIEndpoint", "https://old.example/azure"), ("AiCompletionUrl", "https://old.example/url"), ("ChatGptTemperature", "0.25")));

        Assert.Equal(AiProvider.AzureOpenAi, options.Provider);
        Assert.Equal("https://old.example/azure", options.Endpoint);
        Assert.Equal(0.25, options.Temperature);
        Assert.Null(options.Model);
    }

    [Fact]
    public void The_Ai_section_wins_over_the_old_keys()
    {
        var options = AiOptions.FromConfiguration(Config(("Ai:Endpoint", "https://new.example"), ("Ai:Temperature", "0.5"),
            ("AzureAIEndpoint", "https://old.example/azure"), ("ChatGptTemperature", "0.9")));

        Assert.Equal("https://new.example", options.Endpoint);
        Assert.Equal(0.5, options.Temperature);
    }

    [Fact]
    public void A_vault_endpoint_comes_after_AzureAIEndpoint_and_before_AiCompletionUrl()
    {
        var vaultOnly = AiOptions.FromConfiguration(Config(("AiCompletionUrl", "https://url.example")), extraAzureEndpoint: "https://vault.example");
        Assert.Equal("https://vault.example", vaultOnly.Endpoint);
        Assert.Equal(AiProvider.AzureOpenAi, vaultOnly.Provider);

        var config = AiOptions.FromConfiguration(Config(("AzureAIEndpoint", "https://config.example")), extraAzureEndpoint: "https://vault.example");
        Assert.Equal("https://config.example", config.Endpoint);
    }

    [Theory]
    [InlineData(AiProvider.AzureOpenAi)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public void With_only_AiCompletionUrl_the_callers_default_provider_applies(AiProvider legacyDefault)
    {
        var options = AiOptions.FromConfiguration(Config(("AiCompletionUrl", "https://url.example")), legacyDefault);

        Assert.Equal(legacyDefault, options.Provider);
        Assert.Equal("https://url.example", options.Endpoint);
    }

    [Fact]
    public void The_key_comes_from_the_referenced_configuration_key()
    {
        var config = Config(("Ai:ApiKeyReference", "Vault:AiKey"), ("Vault:AiKey", "fake-key"));
        var options = AiOptions.FromConfiguration(config);

        Assert.Equal("fake-key", options.ResolveApiKey(config));
        Assert.Null(new AiOptions().ResolveApiKey(config));
    }

    [Fact]
    public async Task AddBlocksChatCompletionClient_resolves_a_client_that_calls_the_configured_endpoint()
    {
        using var stub = new ScriptedChatStub(ScriptedChatStub.Completion("wired"));
        var config = Config(("Ai:Provider", "OpenAiCompatible"), ("Ai:Endpoint", stub.BaseUrl + "/v1"), ("Ai:Model", "stub-model"),
            ("Ai:ApiKeyReference", "AiKey"), ("AiKey", "fake-key"));
        var services = new ServiceCollection().AddSingleton(config).AddLogging().AddBlocksChatCompletionClient();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IChatCompletionClient>().CompleteAsync(new[] { new ChatMessage("user", "hi") });

        Assert.Equal("wired", result.Text);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer fake-key", request.Headers["authorization"]);
    }
}
