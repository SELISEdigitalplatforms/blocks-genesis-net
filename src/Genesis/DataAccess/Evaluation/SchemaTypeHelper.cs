using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>Former GraphQlTypeHelper.IsScalar; the gateway forwards here.</summary>
public static class SchemaTypeHelper
{
    public const string GeoJsonTypeName = "GeoJson";
    public const string EnumTypeName = "Enum";
    /// <summary>Free-form value (dictionary, key/value pair, untyped object): one leaf for policy purposes, keys inside cannot be referenced.</summary>
    public const string JsonTypeName = "Json";

    public static bool IsScalar(string type) =>
        type is "String" or "Int" or "Float" or "Boolean" or "DateTime" or "ID"
            or GeoJsonTypeName or EnumTypeName or JsonTypeName;
}
