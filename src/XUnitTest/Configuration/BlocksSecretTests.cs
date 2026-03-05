using Blocks.Genesis;

namespace XUnitTest.Configuration;

public class BlocksSecretTests
{
    [Fact]
    public void BlocksSecret_ShouldAllowSettingAndGettingAllProperties()
    {
        var secret = new BlocksSecret
        {
            CacheConnectionString = "cache",
            MessageConnectionString = "message",
            LogConnectionString = "log",
            MetricConnectionString = "metric",
            TraceConnectionString = "trace",
            LogDatabaseName = "logdb",
            MetricDatabaseName = "metricdb",
            TraceDatabaseName = "tracedb",
            ServiceName = "svc",
            DatabaseConnectionString = "db",
            RootDatabaseName = "rootdb",
            EnableHsts = true,
            SshHost = "ssh-host",
            SshUsername = "ssh-user",
            SshPassword = "ssh-pass",
            SshNginxTemplate = "template",
            ProdDatabaseConnectionString = "prod-db",
            LmtMessageConnectionString = "lmt-message",
            ProdVaultUrl = "https://vault",
            ProdVaultTenantId = "tenant-id",
            ProdVaultClientId = "client-id",
            ProdVaultClientSecret = "client-secret"
        };

        Assert.Equal("cache", secret.CacheConnectionString);
        Assert.Equal("message", secret.MessageConnectionString);
        Assert.Equal("log", secret.LogConnectionString);
        Assert.Equal("metric", secret.MetricConnectionString);
        Assert.Equal("trace", secret.TraceConnectionString);
        Assert.Equal("logdb", secret.LogDatabaseName);
        Assert.Equal("metricdb", secret.MetricDatabaseName);
        Assert.Equal("tracedb", secret.TraceDatabaseName);
        Assert.Equal("svc", secret.ServiceName);
        Assert.Equal("db", secret.DatabaseConnectionString);
        Assert.Equal("rootdb", secret.RootDatabaseName);
        Assert.True(secret.EnableHsts);
        Assert.Equal("ssh-host", secret.SshHost);
        Assert.Equal("ssh-user", secret.SshUsername);
        Assert.Equal("ssh-pass", secret.SshPassword);
        Assert.Equal("template", secret.SshNginxTemplate);
        Assert.Equal("prod-db", secret.ProdDatabaseConnectionString);
        Assert.Equal("lmt-message", secret.LmtMessageConnectionString);
        Assert.Equal("https://vault", secret.ProdVaultUrl);
        Assert.Equal("tenant-id", secret.ProdVaultTenantId);
        Assert.Equal("client-id", secret.ProdVaultClientId);
        Assert.Equal("client-secret", secret.ProdVaultClientSecret);
    }

    [Fact]
    public void UpdateProperty_ShouldSetWritableProperty_WhenPropertyExists()
    {
        var target = new BlocksSecret();

        BlocksSecret.UpdateProperty(target, nameof(BlocksSecret.ServiceName), "svc-a");

        Assert.Equal("svc-a", target.ServiceName);
    }

    [Fact]
    public void UpdateProperty_ShouldNotThrow_WhenPropertyDoesNotExist()
    {
        var target = new BlocksSecret();

        var exception = Record.Exception(() => BlocksSecret.UpdateProperty(target, "MissingProperty", "value"));

        Assert.Null(exception);
    }

    [Fact]
    public void ConvertValue_ShouldReturnSameString_WhenTargetTypeIsString()
    {
        var converted = BlocksSecret.ConvertValue("abc", typeof(string));

        Assert.Equal("abc", converted);
    }

    [Fact]
    public void ConvertValue_ShouldConvert_WhenTargetTypeIsConvertible()
    {
        var converted = BlocksSecret.ConvertValue("true", typeof(bool));

        Assert.IsType<bool>(converted);
        Assert.True((bool)converted);
    }

    [Fact]
    public void ConvertValue_ShouldReturnOriginalValue_WhenConversionFails()
    {
        var converted = BlocksSecret.ConvertValue("not-a-number", typeof(int));

        Assert.Equal("not-a-number", converted);
    }

    [Fact]
    public async Task ProcessBlocksSecret_ShouldPopulateConfiguredValues_ForOnPremVault()
    {
        var previousCache = Environment.GetEnvironmentVariable("BlocksSecret__CacheConnectionString");
        var previousHsts = Environment.GetEnvironmentVariable("BlocksSecret__EnableHsts");
        var previousServiceName = Environment.GetEnvironmentVariable("BlocksSecret__ServiceName");
        var previousMessage = Environment.GetEnvironmentVariable("BlocksSecret__MessageConnectionString");

        try
        {
            Environment.SetEnvironmentVariable("BlocksSecret__CacheConnectionString", "redis://localhost:6379");
            Environment.SetEnvironmentVariable("BlocksSecret__EnableHsts", "true");
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", "env-service");
            Environment.SetEnvironmentVariable("BlocksSecret__MessageConnectionString", "   ");

            var secret = await BlocksSecret.ProcessBlocksSecret(VaultType.OnPrem);

            Assert.NotNull(secret);
            Assert.Equal("redis://localhost:6379", secret.CacheConnectionString);
            Assert.True(secret.EnableHsts);
            Assert.Equal("env-service", secret.ServiceName);

            var typedSecret = Assert.IsType<BlocksSecret>(secret);
            Assert.Null(typedSecret.MessageConnectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BlocksSecret__CacheConnectionString", previousCache);
            Environment.SetEnvironmentVariable("BlocksSecret__EnableHsts", previousHsts);
            Environment.SetEnvironmentVariable("BlocksSecret__ServiceName", previousServiceName);
            Environment.SetEnvironmentVariable("BlocksSecret__MessageConnectionString", previousMessage);
        }
    }
}