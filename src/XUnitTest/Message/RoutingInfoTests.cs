using System;
using System.Reflection;
using Xunit;
using Blocks.Genesis;

public class RoutingInfoTests
{
    [Fact]
    public void RoutingInfo_ShouldInitializeWithCorrectValues()
    {
        // Arrange
        string contextName = "TestContext";
        Type contextType = typeof(TestMessage);
        Type consumerType = typeof(TestConsumer);
        MethodInfo consumerMethod = consumerType.GetMethod("Consume");

        // Act
        var routingInfo = new RoutingInfo(contextName, contextType, consumerType, consumerMethod);

        // Assert
        Assert.Equal(contextName, routingInfo.ContextName);
        Assert.Equal(contextType, routingInfo.ContextType);
        Assert.Equal(consumerType, routingInfo.ConsumerType);
        Assert.Equal(consumerMethod, routingInfo.ConsumerMethod);
    }

    [Fact]
    public void RoutingInfo_ShouldSetCorrectContextName()
    {
        // Arrange
        string contextName = "AnotherContext";
        Type contextType = typeof(AnotherTestMessage);
        Type consumerType = typeof(AnotherTestConsumer);
        MethodInfo consumerMethod = consumerType.GetMethod("Consume");

        // Act
        var routingInfo = new RoutingInfo(contextName, contextType, consumerType, consumerMethod);

        // Assert
        Assert.Equal("AnotherContext", routingInfo.ContextName);
    }

    [Fact]
    public void RoutingInfo_ShouldSetCorrectContextType()
    {
        // Arrange
        Type contextType = typeof(TestMessage);
        Type consumerType = typeof(TestConsumer);
        MethodInfo consumerMethod = consumerType.GetMethod("Consume");

        // Act
        var routingInfo = new RoutingInfo("TestContext", contextType, consumerType, consumerMethod);

        // Assert
        Assert.Equal(typeof(TestMessage), routingInfo.ContextType);
    }

    [Fact]
    public void RoutingInfo_ShouldSetCorrectConsumerType()
    {
        // Arrange
        Type contextType = typeof(TestMessage);
        Type consumerType = typeof(TestConsumer);
        MethodInfo consumerMethod = consumerType.GetMethod("Consume");

        // Act
        var routingInfo = new RoutingInfo("TestContext", contextType, consumerType, consumerMethod);

        // Assert
        Assert.Equal(typeof(TestConsumer), routingInfo.ConsumerType);
    }

    [Fact]
    public void RoutingInfo_ShouldSetCorrectConsumerMethod()
    {
        // Arrange
        Type contextType = typeof(TestMessage);
        Type consumerType = typeof(TestConsumer);
        MethodInfo consumerMethod = consumerType.GetMethod("Consume");

        // Act
        var routingInfo = new RoutingInfo("TestContext", contextType, consumerType, consumerMethod);

        // Assert
        Assert.Equal(consumerMethod, routingInfo.ConsumerMethod);
    }

    // Test classes and interfaces for the unit tests
    public class TestMessage { }

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

    public class AnotherTestMessage { }

    public class AnotherTestConsumer : IConsumer<AnotherTestMessage>
    {
        public Task Consume(AnotherTestMessage message)
        {
            return Task.CompletedTask;
        }
    }
}
