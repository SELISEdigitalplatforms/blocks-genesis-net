using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using XUnitTest.RateLimit;

namespace XUnitTest.Auth;

#pragma warning disable CA1707, CA2007 // same naming and await style as the handler tests next to this file

/// <summary>
/// A tenant whose database routing is missing (blocks-genesis-net#130): the quota check in
/// <see cref="ProtectedEndpointAccessHandler"/> turns the provider's "database information is missing"
/// failure into a 403 with <c>tenant_database_unavailable</c> and the <c>TENANT_DATABASE_MISSING</c>
/// failure reason, fails closed, logs one Warning, and leaves every other path as it was.
/// The last tests run the spec's E2E steps in-process through the real authorization pipeline.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class ProtectedEndpointAccessHandlerTenantDatabaseTests : IDisposable
{
    private const string Resource = "svc::project::gets";
    private const string HealthyTenant = "tenant-healthy";
    private const string MissingTenant = "tenant-missing";

    // Stands in for a connection string with credentials; it must never reach a log or a response.
    private const string SentinelConnectionString = "mongodb://sentinel-user:sentinel-secret-130@127.0.0.1:1";

    public ProtectedEndpointAccessHandlerTenantDatabaseTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        GC.SuppressFinalize(this);
    }

    // ---- missing database routing (H5, H6, H7, C2, C3, C4, C8) ----

    [Fact]
    public async Task MissingDatabaseRouting_Returns403WithStableKey_AndFailsWithReason()
    {
        BlocksContext.SetContext(CreateContext(MissingTenant));
        var dbContext = new Mock<IDbContextProvider>();
        dbContext.Setup(d => d.GetDatabase(MissingTenant)).Throws(MissingRoutingException(MissingTenant));
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http);

        Assert.True(context.HasFailed);
        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r.Message == "TENANT_DATABASE_MISSING");
        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);

        using var body = await ReadJsonBodyAsync(http);
        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal("Tenant database is not available.", errors.GetProperty("tenant_database_unavailable").GetString());
        Assert.Single(errors.EnumerateObject());
    }

    [Fact]
    public async Task MissingDatabaseRouting_FailsClosed_WithoutAccessCheckOrQuotaQuery()
    {
        BlocksContext.SetContext(CreateContext(MissingTenant));
        var dbContext = new Mock<IDbContextProvider>(MockBehavior.Strict);
        dbContext.Setup(d => d.GetDatabase(MissingTenant)).Throws(MissingRoutingException(MissingTenant));

        var context = await HandleAsync(dbContext.Object, HttpContextWithResource(Resource), AuthenticatedUser(Resource));

        // Strict mock: no Permissions collection, no other tenant, no ResourceLimits read.
        Assert.False(context.HasSucceeded);
        dbContext.Verify(d => d.GetDatabase(MissingTenant), Times.Once);
        dbContext.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingDatabaseRouting_LogsOneWarning_WithTenantAndResource_AndNoSecrets()
    {
        BlocksContext.SetContext(CreateContext(MissingTenant));
        var dbContext = new Mock<IDbContextProvider>();
        dbContext.Setup(d => d.GetDatabase(MissingTenant)).Throws(
            new InvalidOperationException($"Could not initialize database for tenant '{MissingTenant}' {SentinelConnectionString}",
                new KeyNotFoundException(SentinelConnectionString)));
        var logger = new ListLogger<ProtectedEndpointAccessHandler>();

        await HandleAsync(dbContext.Object, HttpContextWithResource(Resource), logger: logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(8101, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.Equal(MissingTenant, entry.State["TenantId"]);
        Assert.Equal(Resource, entry.State["Resource"]);
        Assert.Contains(MissingTenant, entry.Message, StringComparison.Ordinal);
        Assert.Contains(Resource, entry.Message, StringComparison.Ordinal);
        Assert.Contains("TENANT_DATABASE_MISSING", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mongodb://", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(entry.State.Values, v => Assert.DoesNotContain("sentinel", v?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    // ---- the real provider (H8, C1, C5, A4) ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("tenant-db", null)]
    [InlineData(null, SentinelConnectionString)]
    [InlineData("tenant-db", "   ")]
    [InlineData("   ", SentinelConnectionString)]
    [InlineData("", "")]
    public async Task RealProvider_MissingOrBlankRouting_Returns403_AndOpensNoMongoClient(string? dbName, string? connectionString)
    {
        BlocksContext.SetContext(CreateContext(MissingTenant));
        var tenants = new Mock<ITenants>(MockBehavior.Strict);
        tenants.Setup(t => t.GetTenantDatabaseConnectionString(MissingTenant)).Returns((dbName, connectionString));
        var providerLogger = new ListLogger<MongoDbContextProvider>();
        var provider = new MongoDbContextProvider(providerLogger, tenants.Object, new ActivitySource("XUnitTest.Auth.TenantDatabase"));
        var handlerLogger = new ListLogger<ProtectedEndpointAccessHandler>();
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(provider, http, logger: handlerLogger);

        Assert.True(context.HasFailed);
        Assert.Contains(context.FailureReasons, r => r.Message == "TENANT_DATABASE_MISSING");
        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);

        // C1: only the request's tenant was looked up, and no client or database handle was created.
        tenants.Verify(t => t.GetTenantDatabaseConnectionString(MissingTenant), Times.Once);
        tenants.VerifyNoOtherCalls();
        Assert.Equal(0, PrivateDictionaryCount(provider, "_mongoClients"));
        Assert.Equal(0, PrivateDictionaryCount(provider, "_databases"));
        Assert.DoesNotContain(providerLogger.Entries, e => e.Message.Contains("MongoClient", StringComparison.Ordinal));

        // A3: the provider keeps its own Error; the handler adds one Warning, so no second Error.
        Assert.Single(providerLogger.Entries, e => e.Level == LogLevel.Error);
        Assert.Single(handlerLogger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(handlerLogger.Entries, e => e.Level >= LogLevel.Error);

        // C4: neither log carries the connection string.
        Assert.All(providerLogger.Entries.Concat(handlerLogger.Entries), e =>
        {
            Assert.DoesNotContain("sentinel", e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sentinel", e.Exception?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RealProvider_ConcurrentRequestsForMisconfiguredTenant_AllGetTheControlledFailure()
    {
        // C6: a burst of parallel requests for the same tenant, each with its own HttpContext.
        BlocksContext.SetContext(CreateContext(MissingTenant));
        var tenants = new Mock<ITenants>();
        tenants.Setup(t => t.GetTenantDatabaseConnectionString(MissingTenant)).Returns(((string?)null, (string?)null));
        var provider = new MongoDbContextProvider(new ListLogger<MongoDbContextProvider>(), tenants.Object, new ActivitySource("XUnitTest.Auth.TenantDatabase"));
        var logger = new ListLogger<ProtectedEndpointAccessHandler>();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            var http = HttpContextWithResource(Resource);
            var context = await HandleAsync(provider, http, logger: logger);
            return (http.Response.StatusCode, context.HasSucceeded, Reasons: context.FailureReasons.Select(r => r.Message).ToArray());
        })));

        Assert.All(results, r =>
        {
            Assert.Equal(StatusCodes.Status403Forbidden, r.StatusCode);
            Assert.False(r.HasSucceeded);
            Assert.Contains("TENANT_DATABASE_MISSING", r.Reasons);
        });
        Assert.Equal(50, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Equal(0, PrivateDictionaryCount(provider, "_mongoClients"));
    }

    [Fact]
    public async Task RealProvider_WhitespaceTenantId_StillThrowsArgumentNullException_AsBefore()
    {
        // Pre-existing behaviour outside the narrow catch: a whitespace tenant id is not empty, so it
        // reaches GetDatabase, which rejects it with an unwrapped ArgumentNullException.
        BlocksContext.SetContext(CreateContext("   "));
        var provider = new MongoDbContextProvider(new ListLogger<MongoDbContextProvider>(), new Mock<ITenants>().Object, new ActivitySource("XUnitTest.Auth.TenantDatabase"));
        var http = HttpContextWithResource(Resource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => HandleAsync(provider, http));

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
    }

    // ---- other failures are unchanged (A1: narrow catch) ----

    public static TheoryData<string> OtherDatabaseFailures => new()
    {
        "wrapped-argument",
        "wrapped-format",
        "unwrapped-invalid-operation",
        "unwrapped-key-not-found",
        "argument-null",
    };

    [Theory]
    [MemberData(nameof(OtherDatabaseFailures))]
    public async Task OtherGetDatabaseFailures_PropagateUnchanged_AndAreNever403(string failure)
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        Exception thrown = failure switch
        {
            "wrapped-argument" => new InvalidOperationException("Could not initialize database", new ArgumentException("bad connection string")),
            "wrapped-format" => new InvalidOperationException("Could not initialize database", new FormatException("bad uri")),
            "unwrapped-invalid-operation" => new InvalidOperationException("Could not initialize database"),
            "unwrapped-key-not-found" => new KeyNotFoundException("Database information is missing"),
            _ => new ArgumentNullException(nameof(failure), "Tenant ID cannot be null or empty."),
        };
        var dbContext = new Mock<IDbContextProvider>();
        dbContext.Setup(d => d.GetDatabase(HealthyTenant)).Throws(thrown);
        var logger = new ListLogger<ProtectedEndpointAccessHandler>();
        var http = HttpContextWithResource(Resource);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => HandleAsync(dbContext.Object, http, logger: logger));

        Assert.Same(thrown, actual);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, http.Response.Body.Length);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    public async Task ResourceLimitsQueryFailure_WhenMongoIsDown_PropagatesUnchanged(string failure)
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        Exception thrown = failure == "connection"
            ? new MongoConnectionException(new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(), new DnsEndPoint("localhost", 27017))), "Mongo is down")
            : new TimeoutException("A timeout occurred after 30000ms selecting a server.");
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<FindOptions<BsonDocument, BsonDocument>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrown);
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>())).Returns(resourceLimits.Object);
        var dbContext = new Mock<IDbContextProvider>();
        dbContext.Setup(d => d.GetDatabase(HealthyTenant)).Returns(database.Object);
        var http = HttpContextWithResource(Resource);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => HandleAsync(dbContext.Object, http));

        Assert.Same(thrown, actual);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, http.Response.Body.Length);
    }

    // ---- preserved behaviour (H1-H4, H9, H10, C7) ----

    [Fact]
    public async Task HealthyTenantWithinQuota_ProceedsToAccessCheck_AndReadsOnlyItsResourceLimits()
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        var dbContext = new Mock<IDbContextProvider>();
        var database = SetupQuota(dbContext, HealthyTenant, new BsonDocument { ["Resource"] = Resource, ["Limit"] = 10, ["Usage"] = 3 });
        SetupPermission(dbContext, HealthyTenant, granted: true);
        var logger = new ListLogger<ProtectedEndpointAccessHandler>();
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http, AuthenticatedUser(Resource), logger);

        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, http.Response.Body.Length);
        Assert.Empty(logger.Entries);
        dbContext.Verify(d => d.GetDatabase(HealthyTenant), Times.Once);
        dbContext.Verify(d => d.GetDatabase(It.Is<string>(t => t != HealthyTenant)), Times.Never);
        dbContext.Verify(d => d.GetCollection<BsonDocument>(HealthyTenant, "Permissions"), Times.Once);
        database.Verify(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()), Times.Once);
        database.Verify(d => d.GetCollection<BsonDocument>(It.Is<string>(n => n != "ResourceLimits"), It.IsAny<MongoCollectionSettings>()), Times.Never);
    }

    [Fact]
    public async Task HealthyTenantWithoutResourceLimitsDocument_StaysWithinQuota()
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, HealthyTenant, null);
        SetupPermission(dbContext, HealthyTenant, granted: true);
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http, AuthenticatedUser(Resource));

        Assert.True(context.HasSucceeded);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
    }

    [Fact]
    public async Task HealthyTenantWithinQuota_ButNoPermission_StillFailsAfterTheQuotaCheck()
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, HealthyTenant, new BsonDocument { ["Limit"] = 10, ["Usage"] = 3 });
        SetupPermission(dbContext, HealthyTenant, granted: false);
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http, AuthenticatedUser("other-resource"));

        Assert.True(context.HasFailed);
        Assert.DoesNotContain(context.FailureReasons, r => r.Message == "TENANT_DATABASE_MISSING");
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        dbContext.Verify(d => d.GetDatabase(HealthyTenant), Times.Once);
        dbContext.Verify(d => d.GetCollection<BsonDocument>(HealthyTenant, "Permissions"), Times.Once);
    }

    [Fact]
    public async Task ExhaustedResourceLimits_StillReturn429WithExceedLimit()
    {
        BlocksContext.SetContext(CreateContext(HealthyTenant));
        var dbContext = new Mock<IDbContextProvider>();
        SetupQuota(dbContext, HealthyTenant, new BsonDocument { ["Limit"] = 5, ["Usage"] = 5 });
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http, AuthenticatedUser(Resource));

        Assert.True(context.HasFailed);
        Assert.Contains(context.FailureReasons, r => r.Message == "RATE_LIMIT_EXCEEDED");
        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        using var body = await ReadJsonBodyAsync(http);
        Assert.Equal("Request limit exceeded.", body.RootElement.GetProperty("errors").GetProperty("exceed_limit").GetString());
        dbContext.Verify(d => d.GetCollection<BsonDocument>(It.IsAny<string>(), "Permissions"), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task EmptyTenant_SkipsTheQuotaLookup(string? tenantId)
    {
        if (tenantId is not null)
        {
            BlocksContext.SetContext(CreateContext(tenantId));
        }

        var dbContext = new Mock<IDbContextProvider>();
        var http = HttpContextWithResource(Resource);

        var context = await HandleAsync(dbContext.Object, http, AuthenticatedUser(Resource));

        // No tenant: the quota is skipped and the access check denies, as before.
        Assert.True(context.HasFailed);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        dbContext.Verify(d => d.GetDatabase(It.IsAny<string>()), Times.Never);
    }

    // ---- spec section 10, in-process through the real authorization pipeline ----

    [Fact]
    public async Task Pipeline_HealthyExhaustedAndMisconfiguredTenants_GetTheirOwnOutcomes()
    {
        await using var host = await PipelineHost.StartAsync();

        // Step 1: the healthy tenant reaches the endpoint.
        using (var healthy = await host.GetAsync(HealthyTenant))
        {
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
            Assert.Equal("projects", await healthy.Content.ReadAsStringAsync());
        }

        // Step 2: exhausted limits give 429 + exceed_limit; restoring them lets the tenant through again.
        await host.SetUsageAsync(100);
        using (var exhausted = await host.GetAsync(HealthyTenant))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
            using var body = JsonDocument.Parse(await exhausted.Content.ReadAsStringAsync());
            Assert.Equal("Request limit exceeded.", body.RootElement.GetProperty("errors").GetProperty("exceed_limit").GetString());
        }

        await host.SetUsageAsync(1);
        using (var restored = await host.GetAsync(HealthyTenant))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        // Step 3: the misconfigured tenant gets the controlled 403, not a 500.
        using (var missing = await host.GetAsync(MissingTenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            using var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());
            Assert.Equal("Tenant database is not available.", body.RootElement.GetProperty("errors").GetProperty("tenant_database_unavailable").GetString());
        }

        // Step 4: one Warning naming tenant and resource; the global exception handler never ran; no secrets.
        var entries = host.Logs.Entries;
        var warning = Assert.Single(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("TENANT_DATABASE_MISSING", StringComparison.Ordinal));
        Assert.Contains(MissingTenant, warning.Message, StringComparison.Ordinal);
        Assert.Contains(Resource, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(entries, e => e.Message.Contains("Unhandled exception", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pipeline_ParallelBurst_MisconfiguredTenantAlwaysGets403_HealthyTenantStillPasses()
    {
        await using var host = await PipelineHost.StartAsync();

        var requests = Enumerable.Range(0, 30)
            .Select(i => i % 5 == 0 ? HealthyTenant : MissingTenant)
            .Select(async tenant =>
            {
                using var response = await host.GetAsync(tenant);
                return (Tenant: tenant, response.StatusCode, Body: await response.Content.ReadAsStringAsync());
            });
        var results = await Task.WhenAll(requests);

        Assert.All(results.Where(r => r.Tenant == MissingTenant), r =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("tenant_database_unavailable", r.Body, StringComparison.Ordinal);
        });
        Assert.All(results.Where(r => r.Tenant == HealthyTenant), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("Unhandled exception", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static InvalidOperationException MissingRoutingException(string tenantId) =>
        new($"Could not initialize database for tenant '{tenantId}'",
            new KeyNotFoundException($"Database information is missing for tenant: {tenantId}"));

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        IDbContextProvider dbContextProvider,
        HttpContext http,
        ClaimsPrincipal? user = null,
        ILogger<ProtectedEndpointAccessHandler>? logger = null)
    {
        var handler = new ProtectedEndpointAccessHandler(dbContextProvider, logger ?? new ListLogger<ProtectedEndpointAccessHandler>());
        var requirement = new ProtectedEndpointAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], user ?? AuthenticatedUser(Resource), http);
        await handler.HandleAsync(context);
        return context;
    }

    private static async Task<JsonDocument> ReadJsonBodyAsync(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(http.Response.Body);
    }

    private static int PrivateDictionaryCount(MongoDbContextProvider provider, string fieldName)
    {
        var field = typeof(MongoDbContextProvider).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.ICollection)field.GetValue(provider)!).Count;
    }

    private static BlocksContext CreateContext(string tenantId) =>
        BlocksContext.Create(tenantId, ["admin"], "user-1", true, "/projects", "", DateTime.MinValue, "", [], "", "", "", "", tenantId, "");

    private static ClaimsPrincipal AuthenticatedUser(params string[] permissions)
    {
        var claims = new List<Claim> { new(BlocksContext.USER_ID_CLAIM, "user-1") };
        claims.AddRange(permissions.Select(p => new Claim(BlocksContext.PERMISSION_CLAIM, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static DefaultHttpContext HttpContextWithResource(string resourceName)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ProtectedEndPointAttribute(resourceName)), "test-endpoint"));
        return http;
    }

    private static Mock<IMongoDatabase> SetupQuota(Mock<IDbContextProvider> dbContext, string tenantId, BsonDocument? limitDocument)
    {
        var database = new Mock<IMongoDatabase>();
        var resourceLimits = new Mock<IMongoCollection<BsonDocument>>();
        resourceLimits
            .Setup(c => c.FindAsync(It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<FindOptions<BsonDocument, BsonDocument>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(limitDocument));
        database
            .Setup(d => d.GetCollection<BsonDocument>("ResourceLimits", It.IsAny<MongoCollectionSettings>()))
            .Returns(resourceLimits.Object);
        dbContext.Setup(d => d.GetDatabase(tenantId)).Returns(database.Object);
        return database;
    }

    private static void SetupPermission(Mock<IDbContextProvider> dbContext, string tenantId, bool granted)
    {
        var permissions = new Mock<IMongoCollection<BsonDocument>>();
        permissions
            .Setup(c => c.FindAsync(It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<FindOptions<BsonDocument, BsonDocument>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCursor(granted ? new BsonDocument { ["_id"] = ObjectId.GenerateNewId() } : null));
        dbContext.Setup(d => d.GetCollection<BsonDocument>(tenantId, "Permissions")).Returns(permissions.Object);
    }

    private static IAsyncCursor<BsonDocument> CreateCursor(BsonDocument? firstItem)
    {
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        if (firstItem is null)
        {
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns(Array.Empty<BsonDocument>());
        }
        else
        {
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns([firstItem]);
        }

        return cursor.Object;
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception, values.ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
    }

    /// <summary>
    /// In-process host with the library's own authorization registration (JwtBearerAuthentication: the
    /// "Protected" policy, the scoped handler resolved from DI, and JwtBearer as the forbid scheme), the
    /// real <see cref="MongoDbContextProvider"/> against the local MongoDB, and
    /// <see cref="GlobalExceptionHandlerMiddleware"/> ahead of authentication as in the API branch.
    /// Identity comes from a test scheme instead of a signed token.
    /// </summary>
    private sealed class PipelineHost : IAsyncDisposable
    {
        private const string TestScheme = "Test";
        private const string TenantHeader = "X-Test-Tenant";

        private readonly WebApplication _app;
        private readonly IMongoDatabase _database;
        private readonly MongoClient _client;

        private PipelineHost(WebApplication app, MongoClient client, IMongoDatabase database, CapturingLogger logs)
        {
            _app = app;
            _client = client;
            _database = database;
            Logs = logs;
        }

        public CapturingLogger Logs { get; }

        public static async Task<PipelineHost> StartAsync()
        {
            const string connectionString = "mongodb://127.0.0.1:27017";
            var databaseName = $"genesis130_e2e_{Guid.NewGuid():N}";
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase(databaseName);
            await database.GetCollection<BsonDocument>("ResourceLimits").InsertOneAsync(
                new BsonDocument { ["Resource"] = Resource, ["Limit"] = 100L, ["Usage"] = 1L });
            await database.GetCollection<BsonDocument>("Permissions").InsertOneAsync(
                new BsonDocument { ["OrganizationId"] = "default", ["Resource"] = Resource, ["Roles"] = new BsonArray { "admin" } });

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantDatabaseConnectionString(HealthyTenant)).Returns((databaseName, connectionString));
            tenants.Setup(t => t.GetTenantDatabaseConnectionString(MissingTenant)).Returns((" ", SentinelConnectionString));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var logs = new CapturingLoggerProvider();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);

            builder.Services.AddSingleton(tenants.Object);
            builder.Services.AddSingleton(new Mock<ICryptoService>().Object); // the library's SecretAuthorizationHandler needs it
            builder.Services.AddSingleton(new ActivitySource("XUnitTest.Auth.TenantDatabase.Pipeline"));
            builder.Services.AddSingleton<IDbContextProvider, MongoDbContextProvider>();
            builder.Services.AddRouting();
            builder.Services.JwtBearerAuthentication();
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { });
            builder.Services.Configure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = TestScheme);

            var app = builder.Build();
            app.UseRouting();
            app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
            app.Use(async (context, next) =>
            {
                // Stands in for the tenant context a validated token provides.
                var tenantId = context.Request.Headers[TenantHeader].ToString();
                BlocksContext.SetContext(BlocksContext.Create(tenantId, ["admin"], "user-1", true, "/api/Project/Gets", "", DateTime.MinValue, "", [Resource], "", "", "", "", tenantId, ""));
                await next(context);
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/api/Project/Gets", () => Results.Text("projects")).WithMetadata(new ProtectedEndPointAttribute(Resource));

            await app.StartAsync();
            return new PipelineHost(app, client, database, logs.Logger);
        }

        public Task<HttpResponseMessage> GetAsync(string tenantId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Project/Gets");
            request.Headers.Add(TenantHeader, tenantId);
            return _app.GetTestClient().SendAsync(request);
        }

        public async Task SetUsageAsync(long usage)
        {
            var result = await _database.GetCollection<BsonDocument>("ResourceLimits").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("Resource", Resource),
                Builders<BsonDocument>.Update.Set("Usage", usage));
            Assert.Equal(1, result.MatchedCount);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _client.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
            _client.Dispose();
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenantId = Request.Headers["X-Test-Tenant"].ToString();
            var identity = new ClaimsIdentity(
                [
                    new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
                    new Claim(BlocksContext.TENANT_ID_CLAIM, tenantId),
                    new Claim(BlocksContext.PERMISSION_CLAIM, Resource),
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
