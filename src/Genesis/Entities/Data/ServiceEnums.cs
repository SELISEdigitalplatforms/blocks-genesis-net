// Moved from blocks-data server/DataGateway.DomainService/Models/ServiceEnums.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using System.Text.Json.Serialization;

namespace Blocks.Genesis.Entities.Data;

public enum SchemaType
{
    Entity = 1,
    Dto = 2
}

public enum SchemaChangeType
{
    SchemaCreate,
    SchemaUpdate,
    SchemaDelete,
    SchemaFieldCreate,
    SchemaFieldUpdate,
    SchemaAccessLevelUpdate,
    SchemaPolicyCreate,
    SchemaPolicyUpdate,
    SchemaFieldValidationCreate,
    SchemaFieldValidationUpdate,
    SchemaFieldValidationDelete,
    SchemaIndexCreate,
    SchemaIndexDelete,
    SchemaPolicyDelete
}

public enum SchemaAccessLevel
{
    Inherited = 0,
    User = 1,
    Public = 2,
    Custom = 3
}

/// <summary>
/// Specifies the source of a condition operand.
/// </summary>
public enum ConditionSource
{
    /// <summary>
    /// Value comes from the user's authentication token (JWT claims).
    /// Available properties: UserId, Email, Roles, Permissions, TenantId, and custom claims.
    /// </summary>
    AUTH,

    /// <summary>
    /// Value comes from a field in the schema/database record.
    /// </summary>
    SCHEMA_FIELD,

    /// <summary>
    /// A static/literal value provided in the policy definition.
    /// </summary>
    STATIC_VALUE
}

/// <summary>
/// Logical operators for combining rules.
/// </summary>
public enum PolicyLogicalOperator
{
    AND,
    OR
}

/// <summary>
/// Comparison operators for policy rules.
/// </summary>
public enum PolicyOperator
{
    EQUAL,
    NOT_EQUAL,
    GREATER_THAN,
    GREATER_THAN_OR_EQUAL,
    LESS_THAN,
    LESS_THAN_OR_EQUAL,
    CONTAIN,
    NOT_CONTAIN,
    IN,
    NOT_IN,
    START_WITH,
    END_WITH,
    IS_NULL,
    IS_NOT_NULL,
    REGEX
}

/// <summary>
/// CRUD operations that policies can apply to.
/// </summary>
public enum PolicyOperation
{
    READ,
    WRITE,
    EDIT,
    DELETE,
    ALL
}

/// <summary>
/// Type of security policy.
/// </summary>
public enum PolicyType
{
    /// <summary>
    /// Row-Level Security: Controls which records a user can access.
    /// </summary>
    RLS,

    /// <summary>
    /// Column-Level Security: Controls which fields a user can access.
    /// </summary>
    CLS
}

/// <summary>
/// Types of validations available
/// </summary>
public enum ValidationType
{
    // Common validations for all types
    NotEmpty,
    Regex,

    // String specific validations
    MinLength,
    MaxLength,
    LengthRange,

    // Numeric and DateTime validations
    Equal,
    NotEqual,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Range,

    // The value (or each element of a primitive array) must exist in another Entity schema's data.
    // Value = target SchemaName, SecondaryValue = target field name (ItemId when empty).
    Reference
}
