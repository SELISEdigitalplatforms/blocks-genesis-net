using Azure;
using Azure.Security.KeyVault.Secrets;
using Blocks.Genesis;
using Moq;
using System.Reflection;

namespace XUnitTest.Vault;

public class AzureKeyVaultTests
{
    [Fact]
    public void GetVaultConfig_ShouldReadValuesFromEnvironment()
    {
        var previousUrl = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        var previousTenantId = Environment.GetEnvironmentVariable("KeyVault__TenantId");
        var previousClientId = Environment.GetEnvironmentVariable("KeyVault__ClientId");
        var previousClientSecret = Environment.GetEnvironmentVariable("KeyVault__ClientSecret");

        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", "https://unit-test-vault.vault.azure.net/");
            Environment.SetEnvironmentVariable("KeyVault__TenantId", "tenant-id");
            Environment.SetEnvironmentVariable("KeyVault__ClientId", "client-id");
            Environment.SetEnvironmentVariable("KeyVault__ClientSecret", "client-secret");

            var config = AzureKeyVault.GetVaultConfig();

            Assert.Equal("https://unit-test-vault.vault.azure.net/", config["KeyVaultUrl"]);
            Assert.Equal("tenant-id", config["TenantId"]);
            Assert.Equal("client-id", config["ClientId"]);
            Assert.Equal("client-secret", config["ClientSecret"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousUrl);
            Environment.SetEnvironmentVariable("KeyVault__TenantId", previousTenantId);
            Environment.SetEnvironmentVariable("KeyVault__ClientId", previousClientId);
            Environment.SetEnvironmentVariable("KeyVault__ClientSecret", previousClientSecret);
        }
    }

    [Fact]
    public void ExtractValuesFromGlobalConfig_ShouldSetPrivateFields_WhenValuesExist()
    {
        var sut = new AzureKeyVault();
        var method = GetPrivateMethod("ExtractValuesFromGlobalConfig");

        method.Invoke(sut, [new Dictionary<string, string>
        {
            ["KeyVaultUrl"] = "https://unit-test-vault.vault.azure.net/",
            ["TenantId"] = "tenant-1",
            ["ClientId"] = "client-1",
            ["ClientSecret"] = "secret-1"
        }]);

        Assert.Equal("https://unit-test-vault.vault.azure.net/", GetPrivateField<string>(sut, "_keyVaultUrl"));
        Assert.Equal("tenant-1", GetPrivateField<string>(sut, "_tenantId"));
        Assert.Equal("client-1", GetPrivateField<string>(sut, "_clientId"));
        Assert.Equal("secret-1", GetPrivateField<string>(sut, "_clientSecret"));
    }

    [Fact]
    public void ExtractValuesFromGlobalConfig_ShouldThrow_WhenAnyRequiredValueMissing()
    {
        var sut = new AzureKeyVault();
        var method = GetPrivateMethod("ExtractValuesFromGlobalConfig");

        var exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(sut, [new Dictionary<string, string>
            {
                ["TenantId"] = "tenant-1",
                ["ClientId"] = "client-1"
            }]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void ConnectToAzureKeyVaultSecret_ShouldCreateSecretClient_WhenFieldsAreSet()
    {
        var sut = new AzureKeyVault();
        SetPrivateField(sut, "_keyVaultUrl", "https://unit-test-vault.vault.azure.net/");
        SetPrivateField(sut, "_tenantId", "tenant-1");
        SetPrivateField(sut, "_clientId", "client-1");
        SetPrivateField(sut, "_clientSecret", "secret-1");

        var method = GetPrivateMethod("ConnectToAzureKeyVaultSecret");

        method.Invoke(sut, null);

        var client = GetPrivateField<SecretClient>(sut, "_secretClient");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task GetSecretFromKeyVaultAsync_ShouldReturnSecretValue_WhenClientReturnsSecret()
    {
        var sut = new AzureKeyVault();
        var keyVaultSecret = new KeyVaultSecret("ApiKey", "ApiValue");
        var response = Response.FromValue(keyVaultSecret, Mock.Of<Response>());

        var clientMock = new Mock<SecretClient>();
        clientMock
            .Setup(c => c.GetSecretAsync("ApiKey", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        SetPrivateField(sut, "_secretClient", clientMock.Object);

        var result = await InvokePrivateAsync<string>(sut, "GetSecretFromKeyVaultAsync", "ApiKey");

        Assert.Equal("ApiValue", result);
    }

    [Fact]
    public async Task GetSecretFromKeyVaultAsync_ShouldReturnEmpty_WhenClientThrows()
    {
        var sut = new AzureKeyVault();

        var clientMock = new Mock<SecretClient>();
        clientMock
            .Setup(c => c.GetSecretAsync("MissingKey", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("failed"));

        SetPrivateField(sut, "_secretClient", clientMock.Object);

        var result = await InvokePrivateAsync<string>(sut, "GetSecretFromKeyVaultAsync", "MissingKey");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GetSecretsFromVaultAsync_ShouldOnlyAddNonEmptySecrets()
    {
        var sut = new AzureKeyVault();

        var okResponse = Response.FromValue(new KeyVaultSecret("Key1", "Value1"), Mock.Of<Response>());
        var clientMock = new Mock<SecretClient>();
        clientMock
            .Setup(c => c.GetSecretAsync("Key1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(okResponse);
        clientMock
            .Setup(c => c.GetSecretAsync("Key2", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("missing"));

        SetPrivateField(sut, "_secretClient", clientMock.Object);

        var result = await InvokePrivateAsync<Dictionary<string, string>>(sut, "GetSecretsFromVaultAsync", new List<string> { "Key1", "Key2" });

        Assert.Single(result);
        Assert.Equal("Value1", result["Key1"]);
        Assert.False(result.ContainsKey("Key2"));
    }

    [Fact]
    public async Task ProcessSecretsAsync_ShouldThrow_WhenRequiredConfigIsMissing()
    {
        var previousUrl = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        var previousTenantId = Environment.GetEnvironmentVariable("KeyVault__TenantId");
        var previousClientId = Environment.GetEnvironmentVariable("KeyVault__ClientId");
        var previousClientSecret = Environment.GetEnvironmentVariable("KeyVault__ClientSecret");

        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", null);
            Environment.SetEnvironmentVariable("KeyVault__TenantId", "tenant-1");
            Environment.SetEnvironmentVariable("KeyVault__ClientId", "client-1");
            Environment.SetEnvironmentVariable("KeyVault__ClientSecret", "secret-1");

            var sut = new AzureKeyVault();

            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessSecretsAsync(new List<string> { "Key1" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousUrl);
            Environment.SetEnvironmentVariable("KeyVault__TenantId", previousTenantId);
            Environment.SetEnvironmentVariable("KeyVault__ClientId", previousClientId);
            Environment.SetEnvironmentVariable("KeyVault__ClientSecret", previousClientSecret);
        }
    }

    private static MethodInfo GetPrivateMethod(string methodName)
    {
        return typeof(AzureKeyVault).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)field.GetValue(instance)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(instance, value);
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string methodName, params object[] args)
    {
        var method = GetPrivateMethod(methodName);
        var task = (Task)method.Invoke(instance, args)!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result")!;
        return (T)resultProperty.GetValue(task)!;
    }
}