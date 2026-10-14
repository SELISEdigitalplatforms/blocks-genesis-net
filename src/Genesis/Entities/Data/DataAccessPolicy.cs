// Moved from blocks-data server/DataGateway.DomainService/Entities/DataAccessPolicy.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;

namespace Blocks.Genesis.Entities.Data;

/// <summary>
/// Represents a data access policy for a schema.
/// Supports both Row-Level Security (RLS) and Column-Level Security (CLS).
/// </summary>
[BsonIgnoreExtraElements]
public class DataAccessPolicy : GraphQlBaseEntity
{
    public string ReferencePolicyId { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public string PolicyDescription { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public PolicyType PolicyType { get; set; }

    /// <summary>
    /// The CRUD operation this policy applies to.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public PolicyOperation Operation { get; set; } = PolicyOperation.READ;

    public string SchemaName { get; set; } = string.Empty;
    public string SchemaId { get; set; } = string.Empty;

    /// <summary>
    /// For CLS: Fields that this policy controls access to. Root: ["Name"]. Nested: ["ContactInfo.Email"], ["ContactInfo.HomeAddress.House"], ["Courses.Title"].
    /// When the policy applies to a whole object, use the object path: ["Courses"], ["ContactInfo"], ["ContactInfo.HomeAddress"] — the rule then applies to all properties inside.
    /// For RLS: Fields that are involved in the policy conditions.
    /// </summary>
    public string[] FieldNames { get; set; } = [];

    /// <summary>
    /// The root rule group that contains all policy conditions.
    /// Supports nested groups with AND/OR logic.
    /// </summary>
    public PolicyRuleGroup RuleGroup { get; set; } = new();

    /// <summary>
    /// Priority for policy evaluation. Higher priority policies are evaluated first.
    /// If a higher priority policy grants access, lower priority policies may be skipped.
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// If true, this policy grants access when conditions are met.
    /// If false, this policy denies access when conditions are met.
    /// </summary>
    public bool IsAllowPolicy { get; set; } = true;


    public DataAccessPolicy Clone()
    {
        return new DataAccessPolicy
        {
            PolicyName = PolicyName,
            PolicyDescription = PolicyDescription,
            PolicyType = PolicyType,
            Operation = Operation,
            SchemaName = SchemaName,
            SchemaId = SchemaId,
            FieldNames = FieldNames,
            RuleGroup = RuleGroup,
            Priority = Priority,
            IsAllowPolicy = IsAllowPolicy,
        };
    }
}

/// <summary>
/// Represents a group of policy rules combined with a logical operator.
/// Supports nested groups for complex expressions like:
/// ({{token.Roles}}=="Silver-User" && Email=={{token.Email}}) || {{token.Roles}}=="HR-Admin"
/// </summary>
[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class PolicyRuleGroup
{
    /// <summary>
    /// How rules in this group are combined (AND/OR).
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public PolicyLogicalOperator LogicalOperator { get; set; } = PolicyLogicalOperator.AND;

    /// <summary>
    /// Individual rules in this group.
    /// </summary>
    public List<PolicyRule> Rules { get; set; } = [];

    /// <summary>
    /// Nested rule groups for complex expressions.
    /// </summary>
    public List<PolicyRuleGroup> NestedGroups { get; set; } = [];
}

/// <summary>
/// Represents a single policy rule/condition.
/// </summary>
[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements] // added: owner 2026-10-13, all package classes ignore unknown fields
public class PolicyRule
{
    /// <summary>
    /// The source of the left operand (Token property or Schema field).
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public ConditionSource LeftSource { get; set; } = ConditionSource.SCHEMA_FIELD;

    /// <summary>
    /// The field/property name for the left operand.
    /// For Token: UserId, Email, Roles, Permissions, TenantId, or custom claim name.
    /// For Schema: The field name in the schema (e.g., "Email", "Department").
    /// </summary>
    public string LeftOperand { get; set; } = string.Empty;

    /// <summary>
    /// The comparison operator.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public PolicyOperator Operator { get; set; } = PolicyOperator.EQUAL;

    /// <summary>
    /// The source of the right operand.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public ConditionSource RightSource { get; set; } = ConditionSource.STATIC_VALUE;

    /// <summary>
    /// The field/property name for the right operand (when RightSource is not StaticValue).
    /// </summary>
    public string RightOperand { get; set; } = string.Empty;

    /// <summary>
    /// Optional multiple right-side field operands. When empty, RightOperand
    /// remains the backward-compatible single operand.
    /// </summary>
    public List<string> RightOperands { get; set; } = [];

    private object? _staticValue;

    /// <summary>
    /// The static value to compare against (when RightSource is StaticValue).
    /// Supports: string, int, long, double, bool, DateTime, string[], etc.
    /// </summary>
    [BsonSerializer(typeof(PolicyStaticValueSerializer))]
    public object? StaticValue
    {
        get => _staticValue;
        set => _staticValue = ConvertFromJsonElement(value);
    }

    /// <summary>
    /// Optional description for this rule.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Converts JsonElement to proper .NET types for MongoDB serialization.
    /// </summary>
    private static object? ConvertFromJsonElement(object? value)
    {
        if (value is System.Text.Json.JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString(),
                System.Text.Json.JsonValueKind.Number => jsonElement.TryGetInt64(out var l) ? l : jsonElement.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.Array => ConvertJsonArray(jsonElement),
                System.Text.Json.JsonValueKind.Object => jsonElement.GetRawText(),
                _ => value
            };
        }
        return value;
    }

    private static object?[] ConvertJsonArray(System.Text.Json.JsonElement jsonElement)
    {
        var list = new List<object?>();
        foreach (var item in jsonElement.EnumerateArray())
        {
            list.Add(ConvertFromJsonElement(item));
        }
        return list.ToArray();
    }
}

/// <summary>
/// Custom BSON serializer for StaticValue to handle dynamic types.
/// </summary>
public class PolicyStaticValueSerializer : IBsonSerializer<object?>
{
    public Type ValueType => typeof(object);

    public object? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonType = context.Reader.GetCurrentBsonType();

        switch (bsonType)
        {
            case BsonType.Null:
                context.Reader.ReadNull();
                return null;
            case BsonType.String:
                return context.Reader.ReadString();
            case BsonType.Int32:
                return context.Reader.ReadInt32();
            case BsonType.Int64:
                return context.Reader.ReadInt64();
            case BsonType.Double:
                return context.Reader.ReadDouble();
            case BsonType.Boolean:
                return context.Reader.ReadBoolean();
            case BsonType.DateTime:
                return new BsonDateTime(context.Reader.ReadDateTime()).ToUniversalTime();
            case BsonType.Array:
                return DeserializeArray(context);
            case BsonType.Document:
                return DeserializeDocument(context);
            case BsonType.Decimal128:
                return (double)context.Reader.ReadDecimal128();
            case BsonType.ObjectId:
                return context.Reader.ReadObjectId().ToString();
            case BsonType.Undefined:
                context.Reader.ReadUndefined();
                return null;
            default:
                // Skip unknown types
                context.Reader.SkipValue();
                return null;
        }
    }

    private static object?[] DeserializeArray(BsonDeserializationContext context)
    {
        var list = new List<object?>();
        context.Reader.ReadStartArray();
        while (context.Reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var serializer = new PolicyStaticValueSerializer();
            list.Add(serializer.Deserialize(context, new BsonDeserializationArgs()));
        }
        context.Reader.ReadEndArray();
        return list.ToArray();
    }

    private static Dictionary<string, object?> DeserializeDocument(BsonDeserializationContext context)
    {
        var dict = new Dictionary<string, object?>();
        context.Reader.ReadStartDocument();
        while (context.Reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var name = context.Reader.ReadName(Utf8NameDecoder.Instance);
            var serializer = new PolicyStaticValueSerializer();
            dict[name] = serializer.Deserialize(context, new BsonDeserializationArgs());
        }
        context.Reader.ReadEndDocument();
        return dict;
    }

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, object? value)
    {
        if (value == null)
        {
            context.Writer.WriteNull();
            return;
        }

        switch (value)
        {
            case string s:
                context.Writer.WriteString(s);
                break;
            case int i:
                context.Writer.WriteInt32(i);
                break;
            case long l:
                context.Writer.WriteInt64(l);
                break;
            case double d:
                context.Writer.WriteDouble(d);
                break;
            case decimal dec:
                context.Writer.WriteDecimal128(dec);
                break;
            case bool b:
                context.Writer.WriteBoolean(b);
                break;
            case DateTime dt:
                context.Writer.WriteDateTime(new BsonDateTime(dt).MillisecondsSinceEpoch);
                break;
            case string[] strArr:
                SerializeStringArray(context, strArr);
                break;
            case object[] arr:
                SerializeArray(context, arr);
                break;
            case IEnumerable<object> enumerable:
                SerializeArray(context, enumerable.ToArray());
                break;
            default:
                context.Writer.WriteString(value.ToString());
                break;
        }
    }

    private static void SerializeArray(BsonSerializationContext context, object[] arr)
    {
        context.Writer.WriteStartArray();
        var serializer = new PolicyStaticValueSerializer();
        foreach (var item in arr)
        {
            serializer.Serialize(context, new BsonSerializationArgs(), item);
        }
        context.Writer.WriteEndArray();
    }

    private static void SerializeStringArray(BsonSerializationContext context, string[] arr)
    {
        context.Writer.WriteStartArray();
        foreach (var item in arr)
        {
            context.Writer.WriteString(item);
        }
        context.Writer.WriteEndArray();
    }

    object? IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        return Deserialize(context, args);
    }

    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
    {
        Serialize(context, args, value);
    }
}
