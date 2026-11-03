using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Moq;
using System.Collections.Concurrent;

namespace XUnitTest.Middlewares;

#pragma warning disable CA1707, CA2007 // same naming and await style as the middleware tests next to this file

/// <summary><see cref="MongoOsAllowedApiSource"/> against the local MongoDB, capturing the <c>find</c> commands it sends.</summary>
[Collection("BlocksAuthStaticState")]
[Trait("Category", "Integration")]
public class MongoOsAllowedApiSourceTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string Collection = "IdentityConfigurations";
    private static readonly string[] _projectedFields = ["AllowedApis", "_id"]; // ordinal order

    [Fact]
    public void Constructor_ShouldThrow_WhenDatabaseIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new MongoOsAllowedApiSource(null!));

        Assert.Equal("rootDatabase", ex.ParamName);
    }

    // E10, H6, P6
    [Fact]
    public async Task ReadAsync_ShouldReturnOnlyIdAndAllowedApis_WithProjectionAndLimitOne()
    {
        await using var db = new TestDatabase();
        await db.Collection.InsertOneAsync(new BsonDocument
        {
            { "AllowedApis", new BsonArray { "api/allowed" } },
            { "Blob", new string('x', 1024 * 1024) },
        });

        var document = await new MongoOsAllowedApiSource(db.Database).ReadAsync(CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(_projectedFields, document!.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new BsonArray { "api/allowed" }, document["AllowedApis"]);

        var find = Assert.Single(db.FindCommands);
        Assert.Equal(Collection, find["find"].AsString);
        Assert.Equal(1, find["limit"].ToInt32());
        Assert.Equal(new BsonDocument("AllowedApis", 1), find["projection"].AsBsonDocument);
        Assert.Equal(new BsonDocument(), find["filter"].AsBsonDocument);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnNull_WhenCollectionIsEmpty()
    {
        await using var db = new TestDatabase();

        var document = await new MongoOsAllowedApiSource(db.Database).ReadAsync(CancellationToken.None);

        Assert.Null(document);
        Assert.Single(db.FindCommands);
    }

    [Fact]
    public async Task ReadAsync_ShouldHonourCancellation()
    {
        await using var db = new TestDatabase();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MongoOsAllowedApiSource(db.Database).ReadAsync(cancelled.Token));
    }

    // H9 and H10 on real data, through the middleware: one find for many requests, mixed elements skipped.
    [Theory]
    [MemberData(nameof(StoredLists))]
    public async Task Middleware_ShouldDecideFromStoredList_WithOneFindForManyRequests(BsonDocument? stored, string path, bool allowed)
    {
        await using var db = new TestDatabase();
        if (stored is not null)
        {
            await db.Collection.InsertOneAsync(stored);
        }

        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantByID("root")).Returns(new Blocks.Genesis.Tenant
        {
            TenantId = "root",
            IsRootTenant = true,
            DbConnectionString = MongoConnectionString,
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer", Subject = "subject", Audiences = [],
                PublicCertificatePath = "path", PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow
            }
        });
        var middleware = new OnboardingApiAccessMiddleware(
            ctx => { ctx.Items["next"] = true; return Task.CompletedTask; },
            tenants.Object,
            Mock.Of<ILogger<OnboardingApiAccessMiddleware>>(),
            new MongoOsAllowedApiSource(db.Database),
            TimeProvider.System);

        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.SetContext(BlocksContext.Create(
                "root", [], "", true, "", "", DateTime.MinValue, "", [], "", "", "", "", "proj-1"));

            for (var i = 0; i < 3; i++)
            {
                var context = new DefaultHttpContext();
                context.Request.Path = path;
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "Sample.Controller.Action"));

                await middleware.InvokeAsync(context);

                Assert.Equal(allowed, context.Items.ContainsKey("next"));
                Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden, context.Response.StatusCode);
            }
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }

        Assert.Single(db.FindCommands);
    }

    public static TheoryData<BsonDocument?, string, bool> StoredLists() => new()
    {
        { null, "/api/anything", false },
        { new BsonDocument("Other", 1), "/api/anything", false },
        { new BsonDocument("AllowedApis", "api/anything"), "/api/anything", false },
        { new BsonDocument("AllowedApis", new BsonArray()), "/api/anything", false },
        { new BsonDocument("AllowedApis", new BsonArray { "api/a", 5, BsonNull.Value, "", new BsonDocument("x", 1), "API/B" }), "/api/b/", true },
        { new BsonDocument("AllowedApis", new BsonArray { "api/a", 5, BsonNull.Value }), "/5", false },
    };

    /// <summary>A per-test database on a client that records every <c>find</c> command against it. Dropped on dispose.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly ConcurrentQueue<BsonDocument> _finds = new();
        private readonly MongoClient _client;
        private readonly string _name = $"onboarding-source-tests-{Guid.NewGuid():N}";

        public TestDatabase()
        {
            var settings = MongoClientSettings.FromConnectionString(MongoConnectionString);
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find" && e.DatabaseNamespace.DatabaseName == _name)
                {
                    _finds.Enqueue(e.Command.DeepClone().AsBsonDocument);
                }
            });
            _client = new MongoClient(settings);
            Database = _client.GetDatabase(_name);
        }

        public IMongoDatabase Database { get; }

        public IMongoCollection<BsonDocument> Collection => Database.GetCollection<BsonDocument>(MongoOsAllowedApiSourceTests.Collection);

        public List<BsonDocument> FindCommands => _finds.ToList();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.DropDatabaseAsync(_name);
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}
