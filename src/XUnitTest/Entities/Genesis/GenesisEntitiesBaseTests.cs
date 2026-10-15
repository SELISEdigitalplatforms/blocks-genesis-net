using System.Reflection;
using Blocks.Genesis;
using MongoDB.Bson;

namespace XUnitTest.Entities.Genesis;

/// <summary>
/// The package base (BaseEntity, unchanged), genesis KeyValueEntry, the catalog, and the
/// entity classes inside Genesis.OS (genesis SPEC1 6.1, one package).
/// </summary>
public class GenesisEntitiesBaseTests
{
    [Fact]
    public void Catalog_Has67CollectionEntities_And4ValueTypes()
    {
        Assert.Equal(67, EntityCatalog.Rows.Count(r => !r.ValueType));
        Assert.Equal(4, EntityCatalog.Rows.Count(r => r.ValueType));
        Assert.Equal(15, EntityCatalog.Rows.Count(r => r.Group == 'B'));
        Assert.Equal(EntityCatalog.Rows.Count, EntityCatalog.Rows.Select(r => r.Class).Distinct().Count());
    }

    [Fact]
    public void BaseEntity_HasExactlyTodays12Fields()
    {
        var props = typeof(BaseEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => $"{p.Name}:{p.PropertyType.Name}");
        Assert.Equal(
            // Create/Read/Update/Delete added 2026-10-14 (owner decision): per-record access lists, null by default.
            ["ItemId:String", "CreatedDate:DateTime", "LastUpdatedDate:DateTime", "CreatedBy:String", "Language:String",
             "LastUpdatedBy:String", "OrganizationId:String", "Tags:List`1",
             "Create:OperationAccess", "Read:OperationAccess", "Update:OperationAccess", "Delete:OperationAccess"],
            props);
        Assert.NotNull(typeof(BaseEntity).GetCustomAttribute<MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElementsAttribute>());

        var e = new BaseEntity();
        Assert.Equal(string.Empty, e.ItemId);
        Assert.Equal("default", e.OrganizationId);
        Assert.Empty(e.Tags);
        Assert.Null(e.CreatedBy);
        Assert.Null(e.Create);
        Assert.Null(e.Read);
        Assert.Null(e.Update);
        Assert.Null(e.Delete);
    }

    [Fact]
    public void BaseEntity_WritesTheSameBytesAsTheOld8FieldBase()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var entity = new BaseEntity
        {
            ItemId = "id-1", CreatedDate = created, LastUpdatedDate = created, CreatedBy = "u1", Language = "en",
            LastUpdatedBy = "u2", OrganizationId = "org-1", Tags = ["t1"],
        };
        // Element order and values of Genesis.OS 4.2.5 BaseEntity (src/Genesis/Entities/BaseEntity.cs before the move).
        var expected = new BsonDocument
        {
            { "_id", "id-1" }, { "CreatedDate", created }, { "LastUpdatedDate", created }, { "CreatedBy", "u1" },
            { "Language", "en" }, { "LastUpdatedBy", "u2" }, { "OrganizationId", "org-1" }, { "Tags", new BsonArray { "t1" } },
        };
        Assert.Equal(expected.ToBson(), entity.ToBson());

        var kv = new KeyValueEntry { ItemId = "kv-1", CreatedDate = created, LastUpdatedDate = created, Key = "k", Value = new BsonInt32(5) };
        var kvExpected = new BsonDocument
        {
            { "_id", "kv-1" }, { "CreatedDate", created }, { "LastUpdatedDate", created }, { "CreatedBy", BsonNull.Value },
            { "Language", BsonNull.Value }, { "LastUpdatedBy", BsonNull.Value }, { "OrganizationId", "default" },
            { "Tags", new BsonArray() }, { "Key", "k" }, { "Value", 5 },
        };
        Assert.Equal(kvExpected.ToBson(), kv.ToBson());
    }

    [Fact]
    public void EntityClasses_LiveInGenesisOs_UnderTheSameNames()
    {
        // One package (owner 2026-10-14): the entity classes are part of Blocks.Genesis.dll (SeliseBlocks.Genesis.OS).
        // A DLL compiled against Genesis.OS 4.2.x asks for [Blocks.Genesis]Blocks.Genesis.BaseEntity: no type forward needed.
        var genesis = typeof(Blocks.Genesis.BlocksContext).Assembly;
        Assert.Equal("Blocks.Genesis", genesis.GetName().Name);
        Assert.Same(genesis, typeof(BaseEntity).Assembly);
        Assert.Same(typeof(BaseEntity), Type.GetType("Blocks.Genesis.BaseEntity, Blocks.Genesis", throwOnError: true));
        Assert.Same(typeof(KeyValueEntry), Type.GetType("Blocks.Genesis.KeyValueEntry, Blocks.Genesis", throwOnError: true));
        Assert.All(EntityCatalog.Rows, r => Assert.Same(genesis, EntityCatalog.PackageType(r).Assembly));
        Assert.Empty(genesis.GetForwardedTypes());
    }

    [Fact]
    public void KeyValueEntry_NameBaseAndIgnoreExtraElements() => EntityAssertions.CatalogEntity(nameof(KeyValueEntry));
}
