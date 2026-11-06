using System.Reflection;
using System.Security.Cryptography;
using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace XUnitTest.Entities;

/// <summary>Checks shared by the per-service entity tests (genesis SPEC1 section 12).</summary>
internal static class EntityAssertions
{
    /// <summary>Every package class of one owner folder (Iam, Os, Logic, Data, Release, Monitor) that is stored as a BSON document.</summary>
    public static TheoryData<string> DocumentClassesOf(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var t in EntityCatalog.DocumentClasses().Where(t => t.Namespace == $"Blocks.Genesis.Entities.{folder}").OrderBy(t => t.FullName, StringComparer.Ordinal))
            data.Add(t.FullName!);
        return data;
    }

    /// <summary>The catalog rows (collection entities + KV value types) of one owner (iam, os, logic, data, release, monitor, genesis).</summary>
    public static TheoryData<string> CatalogClassesOf(string owner)
    {
        var data = new TheoryData<string>();
        foreach (var r in EntityCatalog.Rows.Where(r => r.Owner == owner)) data.Add(r.Class);
        return data;
    }

    public static Type PackageType(string fullName) => EntityCatalog.Package.GetType(fullName, throwOnError: true)!;

    /// <summary>
    /// Value per class = 3 hashes taken from the SERVICE class before the move: filled sample, default instance,
    /// all-null instance. So a changed field, type, default value or null handling ([BsonIgnoreIfNull]) fails.
    /// Read back and written again, the filled sample gives the same bytes.
    /// </summary>
    public static void GoldenBytes(string fullName, IReadOnlyDictionary<string, string> serviceClassHashes)
    {
        var type = PackageType(fullName);
        Assert.True(serviceClassHashes.ContainsKey(fullName), $"no service-class hash for {fullName}");
        var expected = serviceClassHashes[fullName].Split(' ');
        Assert.Equal(3, expected.Length);

        var filled = EntitySamples.ToBson(EntitySamples.Filled(type), type);
        Assert.True(expected[0] == Hash(filled), $"{fullName}: filled sample bytes changed");
        Assert.True(expected[1] == Hash(EntitySamples.DefaultBson(type)), $"{fullName}: default-instance bytes changed (a default value?)");
        Assert.True(expected[2] == Hash(EntitySamples.NullBson(type)), $"{fullName}: all-null bytes changed (null handling?)");
        Assert.Equal(filled, EntitySamples.ToBson(EntitySamples.FromBson(filled, type), type));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    public static void HashesCoverEveryClass(string folder, IReadOnlyDictionary<string, string> serviceClassHashes) =>
        Assert.Equal(
            serviceClassHashes.Keys.OrderBy(k => k, StringComparer.Ordinal),
            DocumentClassesOf(folder).Select(d => (string)d[0]).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>The class map builds (no duplicate _id) and writes no discriminator.</summary>
    public static void ClassMapBuilds(string fullName)
    {
        var map = BsonClassMap.LookupClassMap(PackageType(fullName));
        Assert.DoesNotContain(map.AllMemberMaps, m => m.ElementName == "_t");
    }

    /// <summary>
    /// One catalog class: same class name (so the same "typeof(T).Name + s" collection name), right base for its
    /// group, and [BsonIgnoreExtraElements] on the class so an unknown field never throws (owner 2026-10-13).
    /// </summary>
    public static void CatalogEntity(string cls)
    {
        var row = EntityCatalog.Rows.Single(r => r.Class == cls);
        var type = EntityCatalog.PackageType(row);

        Assert.Equal(cls, type.Name);
        Assert.Equal(row.Group == 'A', typeof(BaseEntity).IsAssignableFrom(type));
        Assert.NotNull(type.GetCustomAttribute<BsonIgnoreExtraElementsAttribute>(inherit: false));

        var doc = EntitySamples.Filled(type).ToBsonDocument(type);
        doc["SomeFieldNobodyKnows"] = 42;
        Assert.Null(Record.Exception(() => BsonSerializer.Deserialize(doc, type)));
    }
}
