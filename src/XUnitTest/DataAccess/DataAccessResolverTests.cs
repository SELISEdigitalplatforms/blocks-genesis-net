using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// The by-name resolver against a real MongoDB (localhost:27017, like the other Genesis integration tests):
/// rows in SchemaDefinitions, DataAccessPolicys and DataValidations are enough to resolve filters, projections and inputs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataAccessResolverTests : IDisposable
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private readonly MongoClient _client = new(MongoConnectionString);
    private readonly IMongoDatabase _db;
    private readonly DataAccessResolver _resolver;
    private AccessPrincipal _principal = User();

    public DataAccessResolverTests()
    {
        _db = _client.GetDatabase("dataaccess_" + Guid.NewGuid().ToString("N"));
        var contextProvider = new Mock<IDbContextProvider>();
        contextProvider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        var principals = new Mock<IAccessPrincipalAccessor>();
        principals.Setup(p => p.Current).Returns(() => _principal);
        _resolver = new DataAccessResolver(contextProvider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), principals.Object);
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private async Task SeedOrderAsync()
    {
        var schema = new SchemaDefinition
        {
            ItemId = "sch-order",
            SchemaName = "Order",
            ReadAccessLevel = SchemaAccessLevel.Custom,
            EditAccessLevel = SchemaAccessLevel.Custom,
            Fields = [new FieldDefinition { Name = "Status", Type = "String" }, new FieldDefinition { Name = "CreatedBy", Type = "String" }]
        };
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(schema);

        var owner = OwnerPolicy(PolicyOperation.READ);
        owner.ItemId = "pol-owner";
        owner.SchemaId = schema.ItemId;

        var deleted = Policy(PolicyType.RLS, PolicyOperation.EDIT, [], Group(PolicyLogicalOperator.AND));
        deleted.ItemId = "pol-deleted";
        deleted.SchemaId = schema.ItemId;
        deleted.IsDeleted = true;

        await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertManyAsync([owner, deleted]);
        await _db.GetCollection<DataValidation>("DataValidations").InsertOneAsync(
            new DataValidation { ItemId = "val-1", SchemaId = schema.ItemId, FieldName = "Status" });
    }

    private async Task SeedProductAsync()
    {
        // Public read and write; Email may only be written by signed-in users.
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-product",
            SchemaName = "Product",
            ReadAccessLevel = SchemaAccessLevel.Public,
            WriteAccessLevel = SchemaAccessLevel.Public,
            EditAccessLevel = SchemaAccessLevel.Public,
            Fields =
            [
                new FieldDefinition { Name = "Name", Type = "String", WriteAccessLevel = SchemaAccessLevel.Public },
                new FieldDefinition { Name = "Email", Type = "String", WriteAccessLevel = SchemaAccessLevel.User, EditAccessLevel = SchemaAccessLevel.User },
            ]
        });
    }

    [Fact]
    public async Task ResolveReadAsync_ReturnsFilterProjectionAndMask()
    {
        await SeedProductAsync();

        var read = await _resolver.ResolveReadAsync("Product", new BsonDocument("ItemId", "p1"), ["Name"]);

        Assert.True(read.IsAccessGranted);
        Assert.Equal(new BsonDocument("_id", "p1").ToJson(), read.Filter!.ToJson());
        Assert.True(read.Projection!.Contains("Name"));
        var row = new Dictionary<string, object> { ["_id"] = "p1", ["Name"] = "Pen" };
        Assert.Same(row, read.ApplyReadPolicy(row));
        Assert.Equal("Pen", row["Name"]);
    }

    [Fact]
    public async Task ResolveReadAsync_Denied_HasNoFilterAndMaskThrows()
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-locked", SchemaName = "Locked", ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = [new FieldDefinition { Name = "Name", Type = "String" }]
        });

        var read = await _resolver.ResolveReadAsync("Locked", new BsonDocument(), null);

        Assert.False(read.IsAccessGranted);
        Assert.Null(read.Filter);
        Assert.Null(read.Projection);
        Assert.Throws<InvalidOperationException>(() => read.ApplyReadPolicy([]));
    }

    [Fact]
    public async Task ResolveReadAsync_CustomSchemaWithOwnerPolicy_MaskKeepsOwnRowFields()
    {
        // Fails without the policy derivation: no column policy is stored, yet the owner must see their fields.
        await SeedOrderAsync();

        var read = await _resolver.ResolveReadAsync("Order", new BsonDocument(), ["Status", "CreatedBy"]);

        Assert.True(read.IsAccessGranted);
        var row = new Dictionary<string, object> { ["_id"] = "1", ["Status"] = "paid", ["CreatedBy"] = "user-1" };
        read.ApplyReadPolicy(row);
        Assert.Equal("paid", row["Status"]);
        Assert.Equal("user-1", row["CreatedBy"]);
    }

    [Fact]
    public async Task ResolveProjectionAsync_MatchesReadProjection()
    {
        await SeedProductAsync();

        var read = await _resolver.ResolveReadAsync("Product", new BsonDocument(), ["Name", "Email"]);
        var projection = await _resolver.ResolveProjectionAsync("Product", ["Name", "Email"]);

        Assert.True(projection.IsAccessGranted);
        Assert.Equal(read.Projection!.ToJson(), projection.Projection!.ToJson());
    }

    [Fact]
    public async Task ResolveInsertAsync_RemovesUserOnlyFieldForAnonymousCaller()
    {
        await SeedProductAsync();
        _principal = AccessPrincipal.Anonymous with { TenantId = "tenant-1" };
        var input = new Dictionary<string, object?> { ["Name"] = "Pen", ["Email"] = "x@y" };

        var resolved = await _resolver.ResolveInsertAsync("Product", input);

        Assert.True(resolved.IsAccessGranted);
        Assert.Same(input, resolved.Input);
        Assert.Equal(["Email"], resolved.RemovedFields);
        Assert.True(input.ContainsKey("Name"));
        Assert.False(input.ContainsKey("Email"));
    }

    [Fact]
    public async Task ResolveInputAsync_SignedInCaller_KeepsEverything()
    {
        await SeedProductAsync();
        var input = new Dictionary<string, object?> { ["Name"] = "Pen", ["Email"] = "x@y" };

        var resolved = await _resolver.ResolveInputAsync("Product", PolicyOperation.EDIT, input);

        Assert.True(resolved.IsAccessGranted);
        Assert.Empty(resolved.RemovedFields);
        Assert.Equal(2, input.Count);
    }

    [Fact]
    public async Task ResolveUpdateAsync_PublicSchema_ReturnsBaseFilterAndInput_CustomWithoutPolicy_Denied()
    {
        await SeedProductAsync();
        await SeedOrderAsync();
        var baseFilter = new BsonDocument("_id", "1");

        var ok = await _resolver.ResolveUpdateAsync("Product", baseFilter, new Dictionary<string, object?> { ["Name"] = "Pen" });
        Assert.True(ok.IsAccessGranted);
        Assert.Same(baseFilter, ok.Filter);
        Assert.NotNull(ok.Input);

        var denied = await _resolver.ResolveUpdateAsync("Order", baseFilter, new Dictionary<string, object?> { ["Status"] = "x" });
        Assert.False(denied.IsAccessGranted);
        Assert.Null(denied.Filter);
        Assert.Null(denied.Input);
    }

    [Fact]
    public async Task ResolveDeleteAsync_CustomWithOwnerPolicy_AndsRlsFilter()
    {
        await SeedOrderAsync();
        var owner = OwnerPolicy(PolicyOperation.DELETE);
        owner.ItemId = "pol-delete";
        owner.SchemaId = "sch-order";
        await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertOneAsync(owner);
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").UpdateOneAsync(
            Builders<SchemaDefinition>.Filter.Eq(s => s.ItemId, "sch-order"),
            Builders<SchemaDefinition>.Update.Set(s => s.DeleteAccessLevel, SchemaAccessLevel.Custom));

        var resolved = await _resolver.ResolveDeleteAsync("Order", new BsonDocument("_id", "1"));

        Assert.True(resolved.IsAccessGranted);
        Assert.Contains(new BsonDocument("CreatedBy", "user-1"), resolved.Filter!["$and"].AsBsonArray);
    }

    [Fact]
    public async Task ResolveReadFilterAsync_FromSchemaNameToFinalFilter()
    {
        await SeedOrderAsync();

        var resolved = await _resolver.ResolveReadFilterAsync("Order", new BsonDocument("Status", "paid"));

        Assert.True(resolved.IsAccessGranted);
        var and = resolved.Filter!["$and"].AsBsonArray;
        Assert.Equal(2, and.Count);
        Assert.Contains(new BsonDocument("Status", "paid"), and);
        Assert.Contains(new BsonDocument("CreatedBy", "user-1"), and);
    }

    [Fact]
    public async Task ResolveMutationFilterAsync_DeniedWithoutEditPolicy_UnknownSchemaThrows()
    {
        await SeedOrderAsync();

        var edit = await _resolver.ResolveMutationFilterAsync("Order", PolicyOperation.EDIT, new BsonDocument("_id", "1"));
        Assert.False(edit.IsAccessGranted);
        Assert.Null(edit.Filter);

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => _resolver.ResolveReadFilterAsync("Missing", new BsonDocument()));
        Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSchemaAsync_AttachesStoredPoliciesValidationsAndDerivedColumnPolicies()
    {
        await SeedOrderAsync();

        var schema = await _resolver.LoadSchemaAsync("Order", _principal);

        Assert.NotNull(schema);
        Assert.Equal(SchemaAccessLevel.Custom, schema!.ReadAccessLevel);
        Assert.Contains(schema.Policies, p => p.ItemId == "pol-owner" && p.PolicyType == PolicyType.RLS);
        Assert.DoesNotContain(schema.Policies, p => p.ItemId == "pol-deleted");
        // Inherited fields get a column copy of the owner policy, as at publish time.
        Assert.Equal(
            new[] { "CreatedBy", "Status" },
            schema.Policies.Where(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ).SelectMany(p => p.FieldNames).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("val-1", schema.Fields.Single(f => f.Name == "Status").ValidationRule!.ItemId);
        Assert.Null(await _resolver.LoadSchemaAsync("Nope", _principal));
    }
}
