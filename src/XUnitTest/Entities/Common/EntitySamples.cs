using System.Collections;
using System.Reflection;
using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace XUnitTest.Entities;

/// <summary>
/// Builds the same filled sample for two classes with the same shape (the package class and its
/// fixture copy of the service class). Values depend only on member names, member types and depth,
/// never on the class identity, so equal shapes give equal BSON.
/// </summary>
internal static class EntitySamples
{
    private const string ObjectIdText = "5f1d7e0b9c3a4b2a1c0d9e8f";
    private static readonly DateTime FixedDate = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private const int MaxDepth = 3;

    public static object Create(Type type) => Activator.CreateInstance(type, nonPublic: true)!;

    public static object Filled(Type type)
    {
        var instance = Create(type);
        Fill(instance, type, 0);
        return instance;
    }

    public static byte[] ToBson(object value, Type nominalType) => value.ToBson(nominalType);

    /// <summary>
    /// BSON of a new instance as its constructor and initialisers leave it. Values that differ per run
    /// (a DateTime other than MinValue, a Guid string, a non-empty ObjectId) are replaced by fixed markers,
    /// so a changed default value or a changed null handling still changes the bytes.
    /// </summary>
    public static byte[] DefaultBson(Type type) => Stable(() => Normalize(Create(type).ToBsonDocument(type)).ToBson());

    /// <summary>BSON of a new instance with every settable nullable member set to null (checks [BsonIgnoreIfNull] and similar).</summary>
    public static byte[] NullBson(Type type) => Stable(() =>
    {
        var o = Create(type);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.IsDefined(typeof(BsonIgnoreAttribute), true)) continue;
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter != null && IsNullable(prop.PropertyType)) setter.Invoke(o, [null]);
        }
        return Normalize(o.ToBsonDocument(type)).ToBson();
    });

    // A serializer that refuses a null member is part of the class contract too: its exception type is hashed.
    private static byte[] Stable(Func<byte[]> make)
    {
        try { return make(); }
        catch (Exception ex) { return System.Text.Encoding.UTF8.GetBytes("error:" + (ex.InnerException ?? ex).GetType().FullName); }
    }

    private static BsonDocument Normalize(BsonDocument doc)
    {
        foreach (var el in doc.Elements.ToList()) doc[el.Name] = Normalize(el.Value);
        return doc;
    }

    private static BsonValue Normalize(BsonValue v) => v switch
    {
        BsonDocument d => Normalize(d),
        BsonArray a => new BsonArray(a.Select(Normalize)),
        BsonDateTime dt when dt.MillisecondsSinceEpoch != BsonConstants.DateTimeMinValueMillisecondsSinceEpoch => new BsonString("<time>"),
        BsonString str when Guid.TryParse(str.Value, out _) => new BsonString("<guid>"),
        BsonObjectId oid when oid.Value != ObjectId.Empty => new BsonString("<objectid>"),
        _ => v,
    };

    public static object FromBson(byte[] bytes, Type type) => BsonSerializer.Deserialize(bytes, type);

    private static void Fill(object instance, Type type, int depth)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Superset fields (SPEC1 6.5) exist only in the package class; the owner never sets them.
            if (prop.DeclaringType?.FullName == "Blocks.Genesis.Entities.Iam.Permission" && prop.Name is "IsCaptchaRequired" or "IsMFARequired") continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.IsDefined(typeof(BsonIgnoreAttribute), true)) continue;
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter == null) continue;
            var value = ValueFor(prop.PropertyType, prop.Name, prop, depth);
            if (value != null || IsNullable(prop.PropertyType)) setter.Invoke(instance, [value]);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsInitOnly || field.IsDefined(typeof(BsonIgnoreAttribute), true)) continue;
            field.SetValue(instance, ValueFor(field.FieldType, field.Name, field, depth));
        }
    }

    private static bool IsNullable(Type t) => !t.IsValueType || Nullable.GetUnderlyingType(t) != null;

    private static object? ValueFor(Type t, string name, MemberInfo? member, int depth)
    {
        var inner = Nullable.GetUnderlyingType(t);
        if (inner != null) t = inner;
        var rep = member?.GetCustomAttribute<BsonRepresentationAttribute>();

        if (t == typeof(string)) return rep?.Representation == BsonType.ObjectId ? ObjectIdText : $"{name}-value";
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 7;
        if (t == typeof(long)) return 70000000000L;
        if (t == typeof(short)) return (short)3;
        if (t == typeof(byte)) return (byte)2;
        if (t == typeof(double)) return 1.5d;
        if (t == typeof(float)) return 2.5f;
        if (t == typeof(decimal)) return 3.25m;
        if (t == typeof(DateTime)) return FixedDate;
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(FixedDate);
        if (t == typeof(TimeSpan)) return TimeSpan.FromSeconds(90);
        if (t == typeof(Guid)) return new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        if (t == typeof(ObjectId)) return ObjectId.Parse(ObjectIdText);
        if (t == typeof(byte[])) return new byte[] { 1, 2, 3 };
        if (t == typeof(object)) return $"{name}-object";
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.Length == 0 ? Activator.CreateInstance(t) : values.GetValue(values.Length - 1);
        }
        if (t == typeof(BsonArray)) return new BsonArray { 1, "a" };
        if (t == typeof(BsonDocument) || t == typeof(BsonValue)) return new BsonDocument { { "k", name }, { "n", 1 } };
        if (t.IsArray)
        {
            var et = t.GetElementType()!;
            var arr = Array.CreateInstance(et, 2);
            arr.SetValue(ValueFor(et, name + "0", null, depth + 1), 0);
            arr.SetValue(ValueFor(et, name + "1", null, depth + 1), 1);
            return arr;
        }
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                if (depth >= MaxDepth) return dict;
                dict[ValueFor(args[0], name + "Key0", null, depth + 1)!] = ValueFor(args[1], name + "Value0", null, depth + 1);
                dict[ValueFor(args[0], name + "Key1", null, depth + 1) is string s ? s + "b" : ValueFor(args[0], name + "Key1", null, depth + 1)!] =
                    ValueFor(args[1], name + "Value1", null, depth + 1);
                return dict;
            }
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args))!;
                if (depth >= MaxDepth) return list;
                list.Add(ValueFor(args[0], name + "0", null, depth + 1));
                list.Add(ValueFor(args[0], name + "1", null, depth + 1));
                return list;
            }
            if (def == typeof(HashSet<>) || def == typeof(ISet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args))!;
                if (depth >= MaxDepth) return set;
                var add = set.GetType().GetMethod("Add")!;
                add.Invoke(set, [ValueFor(args[0], name + "0", null, depth + 1)]);
                add.Invoke(set, [ValueFor(args[0], name + "1", null, depth + 1)]);
                return set;
            }
        }
        if (t.IsClass && !t.IsAbstract)
        {
            if (depth >= MaxDepth) return null;
            var o = Create(t);
            Fill(o, t, depth + 1);
            return o;
        }
        return null;
    }
}

/// <summary>The package types under test.</summary>
internal static class EntityCatalog
{
    public static readonly Assembly Package = typeof(BaseEntity).Assembly;
    private static readonly string[] Owners = ["Iam", "Os", "Logic", "Data", "Release", "Monitor"];

    /// <summary>Owner of each class (folder) and group: A = inherits BaseEntity, B = keeps its own id member and fields (no base).</summary>
    public sealed record Row(string Class, string Owner, char Group, bool ValueType);

    /// <summary>The package catalog (genesis SPEC1 6.4): 70 collection entities + 4 value types.</summary>
    public static IReadOnlyList<Row> Rows { get; } =
    [
        new("User", "iam", 'B', false),
        new("Role", "iam", 'A', false),
        new("Permission", "iam", 'A', false),
        new("Organization", "iam", 'A', false),
        new("UserKeyMap", "iam", 'B', false),
        new("IdentityConfiguration", "iam", 'B', false),
        new("IdentityProvider", "iam", 'A', false),
        new("ClientCredential", "iam", 'A', false),
        new("OidcClientRegistration", "iam", 'A', false),
        new("RefreshTokenModel", "iam", 'B', false),
        new("IdpSessionModel", "iam", 'B', false),
        new("TokenRevocationModel", "iam", 'B', false),
        new("DeviceAuthorizationRequestModel", "iam", 'B', false),
        new("SignupLink", "iam", 'A', false),
        new("SignupLinkConfiguration", "iam", 'A', false),
        new("UserActivity", "iam", 'A', false),
        new("UserTotpDetail", "iam", 'A', false),
        new("ImpersonationSession", "iam", 'B', false),
        new("BiometricCredential", "iam", 'A', false),
        new("BlocksClientConfig", "iam", 'A', false),
        new("Secret", "iam", 'A', false),
        new("MfaConfiguration", "iam", 'A', true),
        new("TenantConfiguration", "iam", 'A', true),
        new("EmailTemplate", "os", 'A', false),
        new("IntegrationRequest", "os", 'A', false),
        new("IntegrationSetup", "os", 'A', false),
        new("IntegrationTemplate", "os", 'A', false),
        new("ThirdPartyJWTClaims", "os", 'A', false),
        new("MailServerConfiguration", "os", 'A', false),
        new("ResourceLimit", "os", 'A', false),
        new("MigrationTracker", "os", 'A', false),
        new("NotificationConfiguration", "os", 'A', true),
        new("WorkflowEntity", "logic", 'A', false),
        new("WorkflowVersionEntity", "logic", 'A', false),
        new("WorkflowExecutionEntity", "logic", 'B', false),
        new("WorkflowItemExecutionEntity", "logic", 'B', false),
        new("FunctionEntity", "logic", 'A', false),
        new("FunctionVersionEntity", "logic", 'A', false),
        new("FunctionRunEntity", "logic", 'A', false),
        new("FunctionRunLogEntity", "logic", 'A', false),
        new("FunctionBuildEntity", "logic", 'A', false),
        new("FunctionAuditEventEntity", "logic", 'A', false),
        new("ProxyDetailEntity", "logic", 'A', false),
        new("ProxyVersionEntity", "logic", 'A', false),
        new("ProxyExecutionEntity", "logic", 'A', false),
        new("Schedule", "logic", 'A', false),
        new("MailBoxEntity", "logic", 'B', false),
        new("MailBoxSyncCursor", "logic", 'B', false),
        new("NotificationConnection", "logic", 'B', false),
        new("NotificationSubscription", "logic", 'B', false),
        new("OfflineNotification", "logic", 'B', false),
        new("FirebaseConfiguration", "logic", 'A', false),
        new("File", "data", 'A', false),
        new("FileDirectory", "data", 'A', false),
        new("FileVersion", "data", 'A', false),
        new("ObjectItem", "data", 'A', false),
        new("ObjectAccessPolicy", "data", 'A', false),
        new("ObjectAuditLog", "data", 'A', false),
        new("StorageConfiguration", "data", 'A', false),
        new("SchemaDefinition", "data", 'A', false),
        new("SchemaIndexDefinition", "data", 'A', false),
        new("SchemaChangeLog", "data", 'A', false),
        new("DataValidation", "data", 'A', false),
        new("DataAccessPolicy", "data", 'A', false),
        new("PublishedSchemaSnapshot", "data", 'A', false),
        new("PublishedSchemaSnapshotChunk", "data", 'A', false),
        new("Build", "release", 'A', false),
        new("Repo", "release", 'A', false),
        new("RepositoryWebhook", "release", 'A', false),
        new("DependencyTrackProjects", "release", 'A', true),
        new("MonitorConfiguration", "monitor", 'A', false),
        new("MonitorIncident", "monitor", 'A', false),
        new("MonitorPingLog", "monitor", 'B', false),
        new("KeyValueEntry", "genesis", 'A', false),
    ];

    public static Type PackageType(Row row) => row.Owner == "genesis"
        ? Package.GetType($"Blocks.Genesis.{row.Class}", throwOnError: true)!
        : Package.GetType($"Blocks.Genesis.Entities.{Cap(row.Owner)}.{row.Class}", throwOnError: true)!;

    /// <summary>Every class in the 6 owner namespaces that the Mongo driver serializes as a document.</summary>
    public static IEnumerable<Type> DocumentClasses() => Package.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && !(t.IsAbstract && t.IsSealed) && t.IsPublic
                    && t.Namespace != null && Owners.Any(o => t.Namespace == $"Blocks.Genesis.Entities.{o}")
                    && !typeof(MongoDB.Bson.Serialization.IBsonSerializer).IsAssignableFrom(t));

    public static IEnumerable<Type> Enums() => Package.GetTypes()
        .Where(t => t.IsEnum && t.Namespace != null && Owners.Any(o => t.Namespace == $"Blocks.Genesis.Entities.{o}"));

    private static string Cap(string s) => s switch { "iam" => "Iam", "os" => "Os", "logic" => "Logic", "data" => "Data", "release" => "Release", "monitor" => "Monitor", _ => s };
}
