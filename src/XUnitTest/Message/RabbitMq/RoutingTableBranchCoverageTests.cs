using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;

namespace XUnitTest.Message.RabbitMq;

public class RoutingTableBranchCoverageTests
{
    [Fact]
    public void Constructor_ShouldSkipDescriptors_WithoutImplementationType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConsumer<BranchPayload>>(_ => new BranchConsumer());

        var table = new RoutingTable(services);

        Assert.Empty(table.Routes);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTwoConsumersHandleSameMessageType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConsumer<BranchPayload>, BranchConsumer>();
        services.AddSingleton<IConsumer<BranchPayload>, DuplicateBranchConsumer>();

        var exception = Assert.Throws<InvalidOperationException>(() => new RoutingTable(services));

        Assert.Contains(nameof(BranchPayload), exception.Message);
    }

    private sealed class BranchPayload
    {
        public string? Value { get; set; }
    }

    private sealed class BranchConsumer : IConsumer<BranchPayload>
    {
        public Task Consume(BranchPayload context) => Task.CompletedTask;
    }

    private sealed class DuplicateBranchConsumer : IConsumer<BranchPayload>
    {
        public Task Consume(BranchPayload context) => Task.CompletedTask;
    }
}
