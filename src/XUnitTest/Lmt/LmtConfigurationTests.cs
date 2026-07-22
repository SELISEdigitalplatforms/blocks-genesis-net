using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;

namespace XUnitTest.Lmt;

/// <summary>
/// Tests for <see cref="LmtConfiguration"/> - the logs/metrics/traces Mongo time-series setup.
/// Connection-dependent paths are exercised against an unreachable server (fast server-selection
/// timeout) to verify the catch/rethrow behaviour without requiring a live database.
/// </summary>
public class LmtConfigurationTests
{
    private const string UnreachableMongo = "mongodb://127.0.0.1:59999/?serverSelectionTimeoutMS=300&connectTimeoutMS=300";

    [Fact]
    public void DatabaseNames_ShouldExposeExpectedConstants()
    {
        Assert.Equal("Logs", LmtConfiguration.LogDatabaseName);
        Assert.Equal("Traces", LmtConfiguration.TraceDatabaseName);
        Assert.Equal("Metrics", LmtConfiguration.MetricDatabaseName);
    }

    [Fact]
    public void GetMongoDatabase_ShouldReturnHandle_WithoutConnecting()
    {
        var db = LmtConfiguration.GetMongoDatabase(UnreachableMongo, "Logs");
        Assert.NotNull(db);
        Assert.Equal("Logs", db.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetMongoCollection_ShouldReturnHandle_WithoutConnecting()
    {
        var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(UnreachableMongo, "Logs", "app");
        Assert.NotNull(collection);
        Assert.Equal("app", collection.CollectionNamespace.CollectionName);
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("metrics")]
    [InlineData("logs")]
    public void CreateCollectionForX_ShouldSwallowConnectionFailures(string kind)
    {
        var ex = Record.Exception(() =>
        {
            switch (kind)
            {
                case "trace": LmtConfiguration.CreateCollectionForTrace(UnreachableMongo, "cov_trace"); break;
                case "metrics": LmtConfiguration.CreateCollectionForMetrics(UnreachableMongo, "cov_metrics"); break;
                default: LmtConfiguration.CreateCollectionForLogs(UnreachableMongo, "cov_logs"); break;
            }
        });

        Assert.Null(ex); // connection failures are caught and logged, not propagated
    }

    [Fact]
    public void CreateIndex_ShouldThrow_WhenMongoUnreachable()
    {
        Assert.ThrowsAny<Exception>(() =>
            LmtConfiguration.CreateIndex(
                UnreachableMongo, "Logs", "cov_idx",
                new BsonDocument { { "TenantId", 1 } }));
    }
}
