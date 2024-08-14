using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis
{
    public static class LmtConfiguration
    {
        public static string LogDatabaseName { get; } = "Logs";
        public static string TraceDatabaseName { get; } = "Traces";
        public static string MetricDatabaseName { get; } = "Metrics";

        public static IMongoDatabase GetMongoDatabase(string connection, string databaseName)
        {
            var mongoClient = new MongoClient(connection);
            return mongoClient.GetDatabase(databaseName);
        }

        public static IMongoCollection<TDocument> GetMongoCollection<TDocument>(string connection, string databaseName, string collectionName)
        {
            return GetMongoDatabase(connection, databaseName).GetCollection<TDocument>(collectionName);
        }

        public static async Task CreateCollectionForTraces(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                TimeSeriesOptions = new TimeSeriesOptions("Timestamp", "TenantId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                await CreateCollectionIfNotExistsAsync(connection, TraceDatabaseName, collectionName, options);
                await CreateIndexAsync(connection, TraceDatabaseName, collectionName, new BsonDocument { { "TenantId", 1 }, { "Timestamp", -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static async Task CreateCollectionForMetrics(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                TimeSeriesOptions = new TimeSeriesOptions("Timestamp", "TenantId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                await CreateCollectionIfNotExistsAsync(connection, MetricDatabaseName, collectionName, options);
                await CreateIndexAsync(connection, MetricDatabaseName, collectionName, new BsonDocument { { "TenantId", 1 }, { "Timestamp", -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static async Task CreateCollectionForLogs(string connection, string collectionName)
        {
            var options = new CreateCollectionOptions
            {
                //Capped = true,
                //MaxSize = 52428800, // 50MB
                TimeSeriesOptions = new TimeSeriesOptions("Timestamp", "TenantId", TimeSeriesGranularity.Minutes)
            };

            try
            {
                await CreateCollectionIfNotExistsAsync(connection, LogDatabaseName, collectionName, options);
                await CreateIndexAsync(connection, LogDatabaseName, collectionName, new BsonDocument { { "TenantId", 1 }, { "Timestamp", -1 } });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static async Task CreateCollectionIfNotExistsAsync(string connection, string databaseName, string collectionName, CreateCollectionOptions options)
        {
            try
            {
                var database = GetMongoDatabase(connection, databaseName);
                var collectionExists = await CollectionExistsAsync(database, collectionName);

                if (!collectionExists)
                {
                    await database.CreateCollectionAsync(collectionName, options);
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

        private static async Task<bool> CollectionExistsAsync(IMongoDatabase database, string collectionName)
        {
            var filter = new BsonDocument("name", collectionName);
            var options = new ListCollectionNamesOptions { Filter = filter };

            var collections = await database.ListCollectionNamesAsync(options);
            return await collections.AnyAsync();
        }

        public static async Task CreateIndexAsync(string connection, string databaseName, string collectionName, BsonDocument indexKeys)
        {
            var collection = GetMongoCollection<BsonDocument>(connection, databaseName, collectionName);
            var indexModel = new CreateIndexModel<BsonDocument>(indexKeys);
            await collection.Indexes.CreateOneAsync(indexModel);
            Console.WriteLine($"Created index on collection '{collectionName}' in database '{databaseName}'");
        }
    }
}
