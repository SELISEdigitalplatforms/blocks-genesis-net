using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess;

/// <summary>Builders for DataAccess tests (same shapes as the blocks-data gateway tests they came from).</summary>
internal static class DataAccessTestSupport
{
    public static AccessPrincipal User(bool root = false, bool authenticated = true, params string[] roles) =>
        new("user-1", "user@x.com", "tenant-1", "org-1", roles, [], authenticated, root) { HasContext = true };

    public static FieldDefinitionResponse Field(
        string name,
        string type = "String",
        bool isArray = false,
        SchemaAccessLevel read = SchemaAccessLevel.Inherited,
        SchemaAccessLevel write = SchemaAccessLevel.Inherited,
        SchemaAccessLevel edit = SchemaAccessLevel.Inherited,
        List<FieldDefinitionResponse>? children = null) => new()
        {
            Name = name,
            Type = type,
            IsArray = isArray,
            ReadAccessLevel = read,
            WriteAccessLevel = write,
            EditAccessLevel = edit,
            Fields = children ?? []
        };

    public static SchemaDefinitionExtended Schema(
        string schemaName = "Person",
        SchemaAccessLevel read = SchemaAccessLevel.Public,
        SchemaAccessLevel write = SchemaAccessLevel.Public,
        SchemaAccessLevel edit = SchemaAccessLevel.Public,
        SchemaAccessLevel delete = SchemaAccessLevel.Public,
        List<FieldDefinitionResponse>? fields = null,
        List<DataAccessPolicy>? policies = null) => new()
        {
            SchemaName = schemaName,
            CollectionName = schemaName + "s",
            SchemaType = SchemaType.Entity,
            ReadAccessLevel = read,
            WriteAccessLevel = write,
            EditAccessLevel = edit,
            DeleteAccessLevel = delete,
            Fields = fields ?? [],
            Policies = policies ?? []
        };

    public static PolicyRule Rule(
        ConditionSource leftSource, string leftOperand, PolicyOperator op,
        ConditionSource rightSource = ConditionSource.STATIC_VALUE, string rightOperand = "", object? staticValue = null) => new()
        {
            LeftSource = leftSource,
            LeftOperand = leftOperand,
            Operator = op,
            RightSource = rightSource,
            RightOperand = rightOperand,
            StaticValue = staticValue
        };

    public static DataAccessPolicy Policy(PolicyType type, PolicyOperation operation, string[] fieldNames, PolicyRuleGroup ruleGroup, bool isAllow = true) => new()
    {
        PolicyType = type,
        Operation = operation,
        FieldNames = fieldNames,
        RuleGroup = ruleGroup,
        IsAllowPolicy = isAllow,
        PolicyName = "policy"
    };

    public static PolicyRuleGroup Group(PolicyLogicalOperator op, params PolicyRule[] rules) => new()
    {
        LogicalOperator = op,
        Rules = rules.ToList(),
        NestedGroups = []
    };

    public static DataAccessPolicy RolePolicy(PolicyOperation operation, string role, PolicyType type = PolicyType.RLS, string[]? fields = null) =>
        Policy(type, operation, fields ?? [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.AUTH, "roles", PolicyOperator.CONTAIN, ConditionSource.STATIC_VALUE, staticValue: role)));

    public static DataAccessPolicy OwnerPolicy(PolicyOperation operation) =>
        Policy(PolicyType.RLS, operation, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId")));
}
