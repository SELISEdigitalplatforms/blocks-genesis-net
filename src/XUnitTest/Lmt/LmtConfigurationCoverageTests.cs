using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;

namespace XUnitTest.Lmt;

public class LmtConfigurationCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string UnreachableConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200&connectTimeoutMS=200";

    [Fact]
    public void GetMongoDatabase_ShouldReturnDatabaseWithRequestedName()
    {
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, "lmt-any-db");

        Assert.Equal("lmt-any-db", database.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void GetMongoCollection_ShouldReturnCollectionWithRequestedName()
    {
        var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(MongoConnectionString, "lmt-any-db", "lmt-any-collection");

        Assert.Equal("lmt-any-collection", collection.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void CreateCollectionForTrace_ShouldCreateTimeSeriesCollectionAndIndex_AndBeIdempotent()
    {
        var collectionName = $"trace-cov-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.TraceDatabaseName);
        try
        {
            LmtConfiguration.CreateCollectionForTrace(MongoConnectionString, collectionName);

            Assert.True(CollectionExists(database, collectionName));
            Assert.True(IsTimeSeries(database, collectionName));

            // Second call goes through the exists-and-is-time-series branch without recreating.
            LmtConfiguration.CreateCollectionForTrace(MongoConnectionString, collectionName);
            Assert.True(CollectionExists(database, collectionName));
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateCollectionForTrace_ShouldRecreateCollection_WhenExistingIsNotTimeSeries()
    {
        var collectionName = $"trace-recreate-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.TraceDatabaseName);
        try
        {
            database.CreateCollection(collectionName);
            Assert.False(IsTimeSeries(database, collectionName));

            LmtConfiguration.CreateCollectionForTrace(MongoConnectionString, collectionName);

            Assert.True(IsTimeSeries(database, collectionName));
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateCollectionForMetrics_ShouldCreateTimeSeriesCollection()
    {
        var collectionName = $"metrics-cov-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.MetricDatabaseName);
        try
        {
            LmtConfiguration.CreateCollectionForMetrics(MongoConnectionString, collectionName);

            Assert.True(CollectionExists(database, collectionName));
            Assert.True(IsTimeSeries(database, collectionName));
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateCollectionForLogs_ShouldCreateTimeSeriesCollectionWithPartialIndex()
    {
        var collectionName = $"logs-cov-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.LogDatabaseName);
        try
        {
            LmtConfiguration.CreateCollectionForLogs(MongoConnectionString, collectionName);

            Assert.True(CollectionExists(database, collectionName));
            var indexes = database.GetCollection<BsonDocument>(collectionName).Indexes.List().ToList();
            Assert.Contains(indexes, i => i["name"].AsString == $"{collectionName}_Index");
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateCollectionForTrace_ShouldSwallowFailures_WhenServerIsUnreachable()
    {
        var exception = Record.Exception(() =>
            LmtConfiguration.CreateCollectionForTrace(UnreachableConnectionString, "trace-unreachable"));

        Assert.Null(exception);
    }

    [Fact]
    public void CreateCollectionForMetrics_ShouldSwallowFailures_WhenServerIsUnreachable()
    {
        var exception = Record.Exception(() =>
            LmtConfiguration.CreateCollectionForMetrics(UnreachableConnectionString, "metrics-unreachable"));

        Assert.Null(exception);
    }

    [Fact]
    public void CreateCollectionForLogs_ShouldSwallowFailures_WhenServerIsUnreachable()
    {
        var exception = Record.Exception(() =>
            LmtConfiguration.CreateCollectionForLogs(UnreachableConnectionString, "logs-unreachable"));

        Assert.Null(exception);
    }

    [Fact]
    public void CreateIndex_ShouldThrowContextualError_WhenServerIsUnreachable()
    {
        var keys = Builders<BsonDocument>.IndexKeys.Ascending("TenantId");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LmtConfiguration.CreateIndex(UnreachableConnectionString, "Logs", "index-unreachable", keys));

        Assert.Contains("index-unreachable", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void CreateIndex_ShouldSkipCreation_WhenIndexWithSameKeysExistsUnderDifferentName()
    {
        var collectionName = $"idx-samekeys-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.TraceDatabaseName);
        try
        {
            database.CreateCollection(collectionName);
            var collection = database.GetCollection<BsonDocument>(collectionName);
            collection.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "TraceId", 1 }, { "Timestamp", -1 } },
                new CreateIndexOptions { Name = "pre-existing-name" }));

            LmtConfiguration.CreateIndex(
                MongoConnectionString,
                LmtConfiguration.TraceDatabaseName,
                collectionName,
                new BsonDocument { { "TraceId", 1 }, { "Timestamp", -1 } });

            var indexes = collection.Indexes.List().ToList();
            Assert.Contains(indexes, i => i["name"].AsString == "pre-existing-name");
            Assert.DoesNotContain(indexes, i => i["name"].AsString == $"{collectionName}_Index");
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateIndex_ShouldWarnAndContinue_WhenEquivalentKeyPatternExistsWithDifferentName()
    {
        var collectionName = $"idx-equivalent-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.TraceDatabaseName);
        try
        {
            database.CreateCollection(collectionName);
            var collection = database.GetCollection<BsonDocument>(collectionName);

            // Same logical key pattern expressed as doubles: the BsonDocument equality
            // check in CreateIndex sees a different document, but the server treats the
            // pattern as an equivalent duplicate and rejects the second name.
            collection.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "TraceId", 1.0 }, { "Timestamp", -1.0 } },
                new CreateIndexOptions { Name = "double-typed-name" }));

            var exception = Record.Exception(() => LmtConfiguration.CreateIndex(
                MongoConnectionString,
                LmtConfiguration.TraceDatabaseName,
                collectionName,
                new BsonDocument { { "TraceId", 1 }, { "Timestamp", -1 } }));

            Assert.Null(exception);
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    [Fact]
    public void CreateIndex_ShouldSkipCreation_WhenIndexWithSameNameExists()
    {
        var collectionName = $"idx-samename-{Guid.NewGuid():N}";
        var database = LmtConfiguration.GetMongoDatabase(MongoConnectionString, LmtConfiguration.TraceDatabaseName);
        try
        {
            database.CreateCollection(collectionName);
            var keys = new BsonDocument { { "TraceId", 1 }, { "Timestamp", -1 } };

            LmtConfiguration.CreateIndex(MongoConnectionString, LmtConfiguration.TraceDatabaseName, collectionName, keys);
            LmtConfiguration.CreateIndex(MongoConnectionString, LmtConfiguration.TraceDatabaseName, collectionName, keys);

            var indexes = database.GetCollection<BsonDocument>(collectionName).Indexes.List().ToList();
            Assert.Single(indexes, i => i["name"].AsString == $"{collectionName}_Index");
        }
        finally
        {
            database.DropCollection(collectionName);
        }
    }

    private static bool CollectionExists(IMongoDatabase database, string collectionName)
    {
        var filter = new BsonDocument("name", collectionName);
        return database.ListCollectionNames(new ListCollectionNamesOptions { Filter = filter }).Any();
    }

    private static bool IsTimeSeries(IMongoDatabase database, string collectionName)
    {
        var filter = new BsonDocument("name", collectionName);
        var info = database.ListCollections(new ListCollectionsOptions { Filter = filter }).FirstOrDefault();
        return info != null && info.Contains("type") && info["type"].AsString == "timeseries";
    }
}
