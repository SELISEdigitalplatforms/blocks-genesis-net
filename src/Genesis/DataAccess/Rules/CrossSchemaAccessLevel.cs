namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>How far a platform lets a rule look into another schema (EXISTS_IN / NOT_EXISTS_IN).</summary>
public enum CrossSchemaAccessLevel
{
    /// <summary>Not available.</summary>
    Off,
    /// <summary>Rules on tenant schemas only.</summary>
    TenantSchemas,
    /// <summary>Rules on tenant and system schemas.</summary>
    AllSchemas
}
