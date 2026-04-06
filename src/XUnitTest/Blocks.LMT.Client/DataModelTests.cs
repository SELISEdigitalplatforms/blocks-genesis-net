using Xunit;

namespace SeliseBlocks.LMT.Client.Tests.BlocksLMTClient
{
    public class LogDataTests
    {
        [Fact]
        public void Constructor_InitializesDefaultValues()
        {
            // Act
            var logData = new LogData();

            // Assert
            Assert.Equal(default(DateTime), logData.Timestamp);
            Assert.Equal(string.Empty, logData.Level);
            Assert.Equal(string.Empty, logData.Message);
            Assert.Equal(string.Empty, logData.Exception);
            Assert.Equal(string.Empty, logData.ServiceName);
            Assert.NotNull(logData.Properties);
            Assert.Empty(logData.Properties);
            Assert.Equal(string.Empty, logData.TenantId);
        }

        [Fact]
        public void Properties_CanAddAndRetrieveValues()
        {
            // Arrange
            var logData = new LogData();

            // Act
            logData.Properties["key1"] = "value1";
            logData.Properties["key2"] = 123;

            // Assert
            Assert.Equal("value1", logData.Properties["key1"]);
            Assert.Equal(123, logData.Properties["key2"]);
            Assert.Equal(2, logData.Properties.Count);
        }

        [Fact]
        public void LogData_CanBeSerialized()
        {
            // Arrange
            var logData = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                Message = "Test message",
                Exception = "No exception",
                ServiceName = "test-service",
                TenantId = "tenant-123"
            };
            logData.Properties["userId"] = 42;

            // Act & Assert - should not throw during serialization
            Assert.NotNull(logData);
            Assert.Equal("test-service", logData.ServiceName);
            Assert.Equal("Information", logData.Level);
        }
    }

    public class TraceDataTests
    {
        [Fact]
        public void Constructor_InitializesDefaultValues()
        {
            // Act
            var traceData = new TraceData();

            // Assert
            Assert.Equal(default(DateTime), traceData.Timestamp);
            Assert.Equal(string.Empty, traceData.TraceId);
            Assert.Equal(string.Empty, traceData.SpanId);
            Assert.Equal(string.Empty, traceData.ParentSpanId);
            Assert.Equal(string.Empty, traceData.ParentId);
            Assert.Equal(string.Empty, traceData.Kind);
            Assert.Equal(string.Empty, traceData.ActivitySourceName);
            Assert.Equal(string.Empty, traceData.OperationName);
            Assert.Equal(0, traceData.Duration);
            Assert.NotNull(traceData.Attributes);
            Assert.Empty(traceData.Attributes);
            Assert.NotNull(traceData.Baggage);
            Assert.Empty(traceData.Baggage);
        }

        [Fact]
        public void Attributes_CanAddAndRetrieveValues()
        {
            // Arrange
            var traceData = new TraceData();

            // Act
            traceData.Attributes["http.method"] = "GET";
            traceData.Attributes["http.status_code"] = 200;

            // Assert
            Assert.Equal("GET", traceData.Attributes["http.method"]);
            Assert.Equal(200, traceData.Attributes["http.status_code"]);
            Assert.Equal(2, traceData.Attributes.Count);
        }

        [Fact]
        public void Baggage_CanAddAndRetrieveValues()
        {
            // Arrange
            var traceData = new TraceData();

            // Act
            traceData.Baggage["userId"] = "user-123";
            traceData.Baggage["requestId"] = "req-456";

            // Assert
            Assert.Equal("user-123", traceData.Baggage["userId"]);
            Assert.Equal("req-456", traceData.Baggage["requestId"]);
            Assert.Equal(2, traceData.Baggage.Count);
        }

        [Fact]
        public void TraceData_CanBeSerializedWithCompleteData()
        {
            // Arrange
            var traceData = new TraceData
            {
                Timestamp = DateTime.UtcNow,
                TraceId = "12345",
                SpanId = "67890",
                ParentSpanId = "11111",
                Kind = "Server",
                ActivitySourceName = "MyApp",
                OperationName = "/api/users",
                Status = "Ok",
                ServiceName = "test-service",
                TenantId = "tenant-123",
                Duration = 150.5
            };
            traceData.Attributes["http.method"] = "POST";
            traceData.Baggage["userId"] = "user-123";

            // Act & Assert - should not throw
            Assert.NotNull(traceData);
            Assert.Equal("12345", traceData.TraceId);
            Assert.Equal(150.5, traceData.Duration);
            Assert.Single(traceData.Attributes);
            Assert.Single(traceData.Baggage);
        }
    }

    public class FailedLogBatchTests
    {
        [Fact]
        public void Constructor_InitializesDefaultValues()
        {
            // Act
            var batch = new FailedLogBatch();

            // Assert
            Assert.NotNull(batch.Logs);
            Assert.Empty(batch.Logs);
            Assert.Equal(0, batch.RetryCount);
            Assert.Equal(default(DateTime), batch.NextRetryTime);
        }

        [Fact]
        public void Logs_CanAddMultipleLogEntries()
        {
            // Arrange
            var batch = new FailedLogBatch();
            var log1 = new LogData { Message = "Log 1" };
            var log2 = new LogData { Message = "Log 2" };

            // Act
            batch.Logs.Add(log1);
            batch.Logs.Add(log2);

            // Assert
            Assert.Equal(2, batch.Logs.Count);
            Assert.Equal("Log 1", batch.Logs[0].Message);
            Assert.Equal("Log 2", batch.Logs[1].Message);
        }
    }

    public class FailedTraceBatchTests
    {
        [Fact]
        public void Constructor_InitializesDefaultValues()
        {
            // Act
            var batch = new FailedTraceBatch();

            // Assert
            Assert.NotNull(batch.TenantBatches);
            Assert.Empty(batch.TenantBatches);
            Assert.Equal(0, batch.RetryCount);
            Assert.Equal(default(DateTime), batch.NextRetryTime);
        }

        [Fact]
        public void TenantBatches_CanAddMultipleTenants()
        {
            // Arrange
            var batch = new FailedTraceBatch();
            var traces1 = new List<TraceData> { new TraceData { TraceId = "trace1" } };
            var traces2 = new List<TraceData> { new TraceData { TraceId = "trace2" } };

            // Act
            batch.TenantBatches["tenant1"] = traces1;
            batch.TenantBatches["tenant2"] = traces2;

            // Assert
            Assert.Equal(2, batch.TenantBatches.Count);
            Assert.Single(batch.TenantBatches["tenant1"]);
            Assert.Single(batch.TenantBatches["tenant2"]);
        }
    }
}
