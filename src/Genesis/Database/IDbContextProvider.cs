using MongoDB.Driver;

namespace Blocks.Genesis
{
    /// <summary>
    /// Provides an interface for interacting with MongoDB databases and collections.
    /// </summary>
    public interface IDbContextProvider
    {
        /// <summary>
        /// Gets the MongoDB database for the specified tenant ID.
        /// </summary>
        /// <param name="tenantId">The ID of the tenant.</param>
        /// <returns>The MongoDB database associated with the specified tenant.</returns>
        IMongoDatabase GetDatabase(string tenantId);

        /// <summary>
        /// Gets the default MongoDB database.
        /// </summary>
        /// <returns>The default MongoDB database, or null if not available.</returns>
        IMongoDatabase? GetDatabase();

        /// <summary>
        /// Gets a MongoDB database using the specified connection string and database name.
        /// </summary>
        /// <param name="connectionString">The connection string for the MongoDB server.</param>
        /// <param name="databaseName">The name of the database to connect to.</param>
        /// <returns>The specified MongoDB database.</returns>
        IMongoDatabase GetDatabase(string connectionString, string databaseName);

        /// <summary>
        /// Gets a MongoDB collection for the specified collection name.
        /// </summary>
        /// <typeparam name="T">The type of documents in the collection.</typeparam>
        /// <param name="collectionName">The name of the collection.</param>
        /// <returns>The MongoDB collection of the specified type.</returns>
        IMongoCollection<T> GetCollection<T>(string collectionName);

        /// <summary>
        /// Gets a MongoDB collection for the specified tenant ID and collection name.
        /// </summary>
        /// <typeparam name="T">The type of documents in the collection.</typeparam>
        /// <param name="tenantId">The ID of the tenant.</param>
        /// <param name="collectionName">The name of the collection.</param>
        /// <returns>The MongoDB collection of the specified type for the specified tenant.</returns>
        IMongoCollection<T> GetCollection<T>(string tenantId, string collectionName);
    }
}
