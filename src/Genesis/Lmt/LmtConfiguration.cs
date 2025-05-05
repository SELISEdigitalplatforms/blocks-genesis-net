using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections;

namespace Blocks.Genesis
{
    public static class LmtConfiguration
    {
        public static string LogDatabaseName { get; } = "Logs";
        public static string TraceDatabaseName { get; } = "Traces";
        public static string MetricDatabaseName { get; } = "Metrics";
        public static string HealthDatabaseName { get; } = "Healths";

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

        public static void CreateCollectionForHealth(string connection)
        {
            var timeSeriesOptionsMultiMeta = new CreateCollectionOptions
            {
                TimeSeriesOptions = new TimeSeriesOptions(
                    timeField: "Timestamp",
                    metaField: null,                               // No single meta field
                    granularity: TimeSeriesGranularity.Minutes
                )
            };
            try
            {
                CreateCollectionIfNotExists(connection, HealthDatabaseName, HealthDatabaseName, timeSeriesOptionsMultiMeta);
                var indexDefinition = Builders<BsonDocument>.IndexKeys
                                    .Ascending("ServiceName")
                                    .Ascending("Instance")
                                    .Descending("Timestamp");

                CreateIndex(connection, HealthDatabaseName, HealthDatabaseName, indexDefinition);

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static void CreateCollectionForTrace(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                //ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "TraceId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, TraceDatabaseName, collectionName, options);
                CreateIndex(connection, TraceDatabaseName, collectionName, new BsonDocument { { "TraceId", 1 }, { _timeField, -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static void CreateCollectionForMetrics(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                //ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "MeterName", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, MetricDatabaseName, collectionName, options);
                CreateIndex(connection, MetricDatabaseName, collectionName, new BsonDocument { { "MeterName", 1 }, { _timeField, -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static void CreateCollectionForLogs(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                //ExpireAfter = TimeSpan.FromDays(90),
                TimeSeriesOptions = new TimeSeriesOptions(_timeField, "TenantId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                CreateCollectionIfNotExists(connection, LogDatabaseName, collectionName, options);
                CreateIndex(connection, LogDatabaseName, collectionName, new BsonDocument { { "TenantId", 1 }, { _timeField, -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static void CreateCollectionIfNotExists(string connection, string databaseName, string collectionName, CreateCollectionOptions options)
        {
            try
            {
                var database = GetMongoDatabase(connection, databaseName);
                var collectionExists = CollectionExists(database, collectionName);

                if (!collectionExists)
                {
                    database.CreateCollection(collectionName, options);
                    Console.WriteLine($"Created collection '{collectionName}' in database '{databaseName}'");
                }
                else
                {
                    Console.WriteLine($"Collection '{collectionName}' already exists in database '{databaseName}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
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

        public static void CreateIndex(string connection, string databaseName, string collectionName, IndexKeysDefinition<BsonDocument> indexKeys)
        {
            var indexName = $"{collectionName}_Index";
            var indexOptions = new CreateIndexOptions { Background = true, Name = indexName };
            var collection = GetMongoCollection<BsonDocument>(connection, databaseName, collectionName);

            try
            {
                // Get existing indexes
                var indexCursor = collection.Indexes.List();
                var existingIndexes = indexCursor.ToList();

                // Check if index with same name exists
                var indexWithSameNameExists = existingIndexes.Any(idx =>
                    idx.Contains("name") && idx["name"].AsString == indexName);

                // Check if index with same key definition exists but different name
                // This is a simplified check - you may need to enhance based on your specific key structure
                var indexWithSameKeysExists = false;
                foreach (var idx in existingIndexes)
                {
                    // Skip the _id index which exists by default
                    if (idx["name"].AsString == "_id_")
                        continue;

                    // Check if the key structure is the same
                    if (idx.Contains("key"))
                    {
                        // This is a simplified check - in real use, you'd need to compare 
                        // the actual key structures which can be complex
                        indexWithSameKeysExists = true;
                        break;
                    }
                }

                // Create index if it doesn't exist with either the same name or key structure
                if (!indexWithSameNameExists && !indexWithSameKeysExists)
                {
                    var indexModel = new CreateIndexModel<BsonDocument>(indexKeys, indexOptions);
                    collection.Indexes.CreateOne(indexModel);
                    Console.WriteLine($"Created index on collection '{collectionName}' in database '{databaseName}'");
                }
                else if (indexWithSameKeysExists)
                {
                    Console.WriteLine($"Index with the same key structure already exists on collection '{collectionName}' in database '{databaseName}'");
                }
                else
                {
                    Console.WriteLine($"Index with name '{indexName}' already exists on collection '{collectionName}' in database '{databaseName}'");
                }
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("Index already exists with a different name"))
            {
                // Handle specific case where the index exists with a different name
                Console.WriteLine($"Cannot create index: An index with the same key pattern already exists with a different name on collection '{collectionName}' in database '{databaseName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating index on collection '{collectionName}': {ex.Message}");
                throw;
            }
        }
    }
}
