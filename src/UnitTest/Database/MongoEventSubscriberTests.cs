using Blocks.Genesis;
using MongoDB.Driver.Core.Events;
using Moq;
using System.Diagnostics;

namespace XUnitTest.Database
{
    public class MongoEventSubscriberTests
    {
        private readonly Mock<ActivitySource> _activitySourceMock;
        private readonly MongoEventSubscriber _mongoEventSubscriber;
        private readonly Activity _mockActivity;

        public MongoEventSubscriberTests()
        {
            _activitySourceMock = new Mock<ActivitySource>();
            _mongoEventSubscriber = new MongoEventSubscriber(_activitySourceMock.Object);

            // Mock activity to simulate tracing behavior
            _mockActivity = new Activity("MongoDb::TestCommand");
            _activitySourceMock.Setup(a => a.StartActivity(It.IsAny<string>(), It.IsAny<ActivityKind>(), It.IsAny<ActivityContext>(), null, DateTimeOffset.Now))
                .Returns(_mockActivity);
        }

        [Fact]
        public void TryGetEventHandler_ShouldReturnTrue_WhenEventHandlerExists()
        {
            // Act
            var result = _mongoEventSubscriber.TryGetEventHandler<CommandStartedEvent>(out var handler);

            // Assert
            Assert.True(result);
            Assert.NotNull(handler);
        }

        [Fact]
        public void HandleCommandStartedEvent_ShouldStartActivity_AndAddTags()
        {
            // Arrange
            var commandStartedEvent = new CommandStartedEvent(
                "find",
                new ConnectionId(new ServerId(new ClusterId(), new MongoDB.Driver.Core.Servers.EndPoint { })),
                "testDb",
                new MongoDB.Bson.BsonDocument(),
                new MongoDB.Bson.BsonDocument(),
                DateTime.UtcNow,
                123);

            // Act
            _mongoEventSubscriber.Handle(commandStartedEvent);

            // Assert
            Assert.NotNull(_mockActivity);
            Assert.Equal("MongoDb::find", _mockActivity.OperationName);
            Assert.Equal("testDb", _mockActivity.GetTagValue("dbName"));
            Assert.Equal("find", _mockActivity.GetTagValue("operationName"));
            Assert.Equal("123", _mockActivity.GetTagValue("requestId"));
            Assert.True(_mongoEventSubscriber.TryGetEventHandler(out Action<CommandStartedEvent> _));
        }

        [Fact]
        public void HandleCommandSucceededEvent_ShouldTagAndStopActivity()
        {
            // Arrange
            var commandStartedEvent = new CommandStartedEvent(
                "insert",
                new ConnectionId(new ServerId(new ClusterId(), new MongoDB.Driver.Core.Servers.EndPoint { })),
                "testDb",
                new MongoDB.Bson.BsonDocument(),
                new MongoDB.Bson.BsonDocument(),
                DateTime.UtcNow,
                123);

            var commandSucceededEvent = new CommandSucceededEvent(
                "insert",
                new MongoDB.Bson.BsonDocument(),
                TimeSpan.FromMilliseconds(200),
                123);

            // Act
            _mongoEventSubscriber.Handle(commandStartedEvent); // Start activity
            _mongoEventSubscriber.Handle(commandSucceededEvent); // Succeed activity

            // Assert
            Assert.Equal("Success", _mockActivity.GetTagValue("Status"));
            Assert.Equal(200, _mockActivity.GetTagValue("Duration"));
            Assert.True(_mockActivity.Duration > TimeSpan.Zero);
        }

        [Fact]
        public void HandleCommandFailedEvent_ShouldTagWithFailureAndStopActivity()
        {
            // Arrange
            var commandStartedEvent = new CommandStartedEvent(
                "delete",
                new ConnectionId(new ServerId(new ClusterId(), new MongoDB.Driver.Core.Servers.EndPoint { })),
                "testDb",
                new MongoDB.Bson.BsonDocument(),
                new MongoDB.Bson.BsonDocument(),
                DateTime.UtcNow,
                123);

            var exception = new Exception("Test Exception");
            var commandFailedEvent = new CommandFailedEvent(
                "delete",
                exception,
                TimeSpan.FromMilliseconds(300),
                123);

            // Act
            _mongoEventSubscriber.Handle(commandStartedEvent); // Start activity
            _mongoEventSubscriber.Handle(commandFailedEvent); // Fail activity

            // Assert
            Assert.Equal("Failure", _mockActivity.GetTagValue("Status"));
            Assert.Equal(300, _mockActivity.GetTagValue("Duration"));
            Assert.Equal("Test Exception", _mockActivity.GetTagValue("ExceptionMessage"));
            Assert.True(_mockActivity.Duration > TimeSpan.Zero);
        }
    }
}



