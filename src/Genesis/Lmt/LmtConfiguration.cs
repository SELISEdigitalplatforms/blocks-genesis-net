using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Diagnostics;

namespace Blocks.Genesis
{
    /// <summary>
    /// Configuration utility for MongoDB collections used by the Logging, Monitoring, and Telemetry (LMT) system.
    /// Manages time-series collection creation and index management.
    /// </summary>
    public static class LmtConfiguration
    {
        public static string LogDatabaseName { get; } = "Logs";
        public static string TraceDatabaseName { get; } = "Traces";
        public static string MetricDatabaseName { get; } = "Metrics";

        private const string _timeField = "Timestamp";


        public static IMongoDatabase GetMongoDatabase(string connection, string databaseName)
        {
            var mongoClient = new MongoClient(connection);
            return mongoClient.GetDatabase(databaseName);
        }

        public static IMongoCollection<TDocument> GetMongoCollection<TDocument>(string connection, string databaseName, string collectionName)
        {
            return GetMongoDatabase(connection, databaseName).GetCollection<TDocument>(collectionName);
        }

        /// <summary>
        /// Creates a time-series collection for trace data with automatic TTL.
        /// </summary>
        /// <param name="connection">MongoDB connection string</param>
        /// <param name="collectionName">Name of the collection to create</param>
        public static void CreateCollectionForTrace(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "TraceId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, TraceDatabaseName, collectionName, options);
                CreateIndex(connection, TraceDatabaseName, collectionName, new BsonDocument { { "TraceId", 1 }, { _timeField, -1 } });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating trace collection: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Creates a time-series collection for metrics data with automatic TTL.
        /// </summary>
        /// <param name="connection">MongoDB connection string</param>
        /// <param name="collectionName">Name of the collection to create</param>
        public static void CreateCollectionForMetrics(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "MeterName", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, MetricDatabaseName, collectionName, options);
                CreateIndex(connection, MetricDatabaseName, collectionName, new BsonDocument { { "MeterName", 1 }, { _timeField, -1 } });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating metrics collection: {ex}");
            }
        }

        /// <summary>
        /// Creates a time-series collection for log data with automatic TTL and tenant-based partitioning.
        /// </summary>
        /// <param name="connection">MongoDB connection string</param>
        /// <param name="collectionName">Name of the collection to create</param>
        public static void CreateCollectionForLogs(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "TenantId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, LogDatabaseName, collectionName, options);
                CreateIndex(
                    connection,
                    LogDatabaseName,
                    collectionName,
                    new BsonDocument { { "TenantId", 1 }, { _timeField, -1 } },
                    new BsonDocument("TenantId", new BsonDocument("$exists", true)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating logs collection: {ex}");
            }
        }

        /// <summary>
        /// Creates a MongoDB collection if it does not exist, or recreates it as a time-series if it exists as a normal collection.
        /// </summary>
        private static void CreateCollectionIfNotExists(string connection, string databaseName, string collectionName, CreateCollectionOptions options)
        {
            try
            {
                var database = GetMongoDatabase(connection, databaseName);
                var collectionExists = CollectionExists(database, collectionName);

                if (!collectionExists)
                {
                    database.CreateCollection(collectionName, options);
                    Debug.WriteLine($"Created collection '{collectionName}' in database '{databaseName}'");
                }
                else if (!IsTimeSeriesCollection(database, collectionName))
                {
                    Debug.WriteLine($"Collection '{collectionName}' in database '{databaseName}' is a normal collection. Converting to time series.");
                    database.DropCollection(collectionName);
                    database.CreateCollection(collectionName, options);
                    Debug.WriteLine($"Recreated collection '{collectionName}' as time series in database '{databaseName}'");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating collection '{collectionName}': {ex}");
                throw;
            }
        }

        private static bool CollectionExists(IMongoDatabase database, string collectionName)
        {
            var filter = new BsonDocument("name", collectionName);
            var options = new ListCollectionNamesOptions { Filter = filter };

            var collections = database.ListCollectionNames(options);
            return collections.Any();
        }

        private static bool IsTimeSeriesCollection(IMongoDatabase database, string collectionName)
        {
            var filter = new BsonDocument("name", collectionName);
            var options = new ListCollectionsOptions { Filter = filter };
            var collectionInfo = database.ListCollections(options).FirstOrDefault();

            return collectionInfo != null
                && collectionInfo.Contains("type")
                && collectionInfo["type"].AsString == "timeseries";
        }

        /// <summary>
        /// Creates an index on a MongoDB collection with optional partial filter.
        /// </summary>
        public static void CreateIndex(
            string connection,
            string databaseName,
            string collectionName,
            IndexKeysDefinition<BsonDocument> indexKeys,
            FilterDefinition<BsonDocument>? partialFilter = null)
        {
            var indexName = $"{collectionName}_Index";
            var indexOptions = new CreateIndexOptions<BsonDocument> { Name = indexName };
            if (partialFilter != null)
            {
                indexOptions.PartialFilterExpression = partialFilter;
            }
            var collection = GetMongoCollection<BsonDocument>(connection, databaseName, collectionName);
            var serializerRegistry = BsonSerializer.SerializerRegistry;
            var documentSerializer = serializerRegistry.GetSerializer<BsonDocument>();
            var expectedIndexKeys = indexKeys.Render(new RenderArgs<BsonDocument>(documentSerializer, serializerRegistry));

            try
            {
                // Get existing indexes
                var indexCursor = collection.Indexes.List();
                var existingIndexes = indexCursor.ToList();

                // Check if index with same name exists
                var indexWithSameNameExists = existingIndexes.Any(idx =>
                    idx.Contains("name") && idx["name"].AsString == indexName);

                // Check if index with same key definition exists but different name
                var indexWithSameKeysExists = existingIndexes.Any(idx =>
                    idx.Contains("name")
                    && idx["name"].AsString != "_id_"
                    && idx.Contains("key")
                    && idx["key"].AsBsonDocument.Equals(expectedIndexKeys));

                // Create index if it doesn't exist with either the same name or key structure
                if (!indexWithSameNameExists && !indexWithSameKeysExists)
                {
                    var indexModel = new CreateIndexModel<BsonDocument>(indexKeys, indexOptions);
                    collection.Indexes.CreateOne(indexModel);
                    Debug.WriteLine($"Created index on collection '{collectionName}' in database '{databaseName}'");
                }
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("Index already exists with a different name"))
            {
                // Handle specific case where the index exists with a different name
                Debug.WriteLine($"Index with same key pattern exists on collection '{collectionName}' in database '{databaseName}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating index on collection '{collectionName}': {ex.Message}");
                throw;
            }
        }
    }
}
