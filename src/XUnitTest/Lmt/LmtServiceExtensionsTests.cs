using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using SeliseBlocks.LMT.Client;

namespace XUnitTest.Lmt;

public class LmtServiceExtensionsTests
{
    [Fact]
    public void AddLmtClient_WithAction_ShouldRegisterOptionsAndLogger()
    {
        var services = new ServiceCollection();

        services.AddLmtClient(opts =>
        {
            opts.ServiceId = "ext-svc";
            opts.ConnectionString = "Endpoint=sb://dummy";
            opts.EnableLogging = true;
        });

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<LmtOptions>();
        Assert.Equal("ext-svc", options.ServiceId);
        Assert.True(options.EnableLogging);

        // Verify IBlocksLogger is registered (check descriptor rather than resolve to avoid real connection)
        Assert.Contains(services, s => s.ServiceType == typeof(IBlocksLogger) && s.ImplementationType == typeof(BlocksLogger));
    }

    [Fact]
    public void AddLmtClient_WithConfiguration_ShouldBindAndRegister()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Lmt:ServiceId"] = "config-svc",
            ["Lmt:ConnectionString"] = "Endpoint=sb://config-dummy",
            ["Lmt:EnableLogging"] = "true",
            ["Lmt:EnableTracing"] = "false",
            ["Lmt:LogBatchSize"] = "50",
            ["Lmt:TraceBatchSize"] = "500",
            ["Lmt:FlushIntervalSeconds"] = "10",
            ["Lmt:XBlocksKey"] = "my-tenant"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLmtClient(configuration);

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<LmtOptions>();
        Assert.Equal("config-svc", options.ServiceId);
        Assert.Equal("Endpoint=sb://config-dummy", options.ConnectionString);
        Assert.True(options.EnableLogging);
        Assert.False(options.EnableTracing);
        Assert.Equal(50, options.LogBatchSize);
        Assert.Equal(500, options.TraceBatchSize);
        Assert.Equal(10, options.FlushIntervalSeconds);
        Assert.Equal("my-tenant", options.XBlocksKey);

        Assert.Contains(services, s => s.ServiceType == typeof(IBlocksLogger));
    }

    [Fact]
    public void AddLmtClient_WithAction_ShouldReturnSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddLmtClient(opts =>
        {
            opts.ServiceId = "svc";
            opts.ConnectionString = "Endpoint=sb://x";
        });

        Assert.Same(services, result);
    }

    [Fact]
    public void AddLmtTracing_ShouldReturnTracerProviderBuilder()
    {
        var options = new LmtOptions
        {
            ServiceId = "trace-svc",
            ConnectionString = "Endpoint=sb://dummy.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dummykey==",
            EnableTracing = true
        };

        var services = new ServiceCollection();
        var tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder();

        var result = tracerProvider.AddLmtTracing(options);

        Assert.NotNull(result);
        Assert.Same(tracerProvider, result);
    }
}
