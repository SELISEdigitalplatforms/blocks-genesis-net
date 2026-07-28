using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Blocks.Genesis;
using Moq;
using System.Reflection;

namespace XUnitTest.Vault;

[Collection("AzureKeyVaultEnvironment")]
public class AzureKeyVaultSuccessPathTests
{
    [Fact]
    public async Task ProcessSecretsAsync_ShouldConnectAndReturnSecrets_WhenCredentialSucceeds()
    {
        var previousUrl = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", "https://unit-test-vault.vault.azure.net/");

            var credential = new Mock<TokenCredential>();
            credential.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));

            var secretClient = new Mock<SecretClient>();
            secretClient.Setup(c => c.GetSecretAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
                        .Returns<string, string, SecretContentType?, CancellationToken>((name, _, _, _) =>
                            name == "present"
                                ? Task.FromResult(Response.FromValue(new KeyVaultSecret("present", "secret-value"), Mock.Of<Response>()))
                                : throw new RequestFailedException(404, "not found"));

            var vault = new AzureKeyVault();
            InvokeOverrideConnectionSeams(
                vault,
                [() => null, () => credential.Object],
                (_, _) => secretClient.Object);

            var captured = new List<string>();
            var previousLogger = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .WriteTo.Sink(new DelegateSink(e => captured.Add(e.Exception?.ToString() ?? e.MessageTemplate.Text)))
                .CreateLogger();
            try
            {
                var secrets = await vault.ProcessSecretsAsync(["present", "missing"]);

                Assert.Equal(2, secretClient.Invocations.Count);
                Assert.True(secrets.Count == 1,
                    "invocations: " + string.Join(" || ", secretClient.Invocations.Select(i => i.Method.ToString())) +
                    " captured: " + string.Join(" || ", captured));
                Assert.Equal("secret-value", secrets["present"]);
            }
            finally
            {
                Serilog.Log.Logger = previousLogger;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousUrl);
        }
    }

    private sealed class DelegateSink(Action<Serilog.Events.LogEvent> handler) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => handler(logEvent);
    }

    private static void InvokeOverrideConnectionSeams(
        AzureKeyVault vault,
        List<Func<TokenCredential?>> credentialFactories,
        Func<Uri, TokenCredential, SecretClient> secretClientFactory)
    {
        var method = typeof(AzureKeyVault).GetMethod("OverrideConnectionSeams", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(vault, [credentialFactories, secretClientFactory]);
    }
}
