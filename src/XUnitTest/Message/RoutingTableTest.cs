using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Blocks.Genesis;
using System.Reflection;

public class RoutingTableTests
{
    [Fact]
    public void RoutingTable_ShouldInitializeWithEmptyRoutes()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();

        // Act
        var routingTable = new RoutingTable(serviceCollection);

        // Assert
        Assert.Empty(routingTable.Routes);
    }

    [Fact]
    public void RoutingTable_ShouldIgnoreNonConsumerServices()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddTransient<INonConsumerService, NonConsumerService>();

        // Act
        var routingTable = new RoutingTable(serviceCollection);

        // Assert
        Assert.Empty(routingTable.Routes);
    }

    // Test classes and interfaces for the unit tests
    public class TestMessage { }
    public class AnotherTestMessage { }

    public interface IConsumer<T>
    {
        Task Consume(T message);
    }

    public class TestConsumer : IConsumer<TestMessage>
    {
        public Task Consume(TestMessage message)
        {
            return Task.CompletedTask;
        }
    }

    public class AnotherTestConsumer : IConsumer<TestMessage>
    {
        public Task Consume(TestMessage message)
        {
            return Task.CompletedTask;
        }
    }

    public interface INonConsumerService
    {
        void DoSomething();
    }

    public class NonConsumerService : INonConsumerService
    {
        public void DoSomething() { }
    }
}
