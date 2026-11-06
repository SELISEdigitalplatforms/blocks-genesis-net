using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Data;

/// <summary>How a property's default value is produced when an insert doesn't send the property.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefaultValueKind
{
    /// <summary>A fixed value, or a fixed list for a list property.</summary>
    Static,

    /// <summary>Text built from variables, e.g. <c>{{Employee.FirstName}} {{Employee.LastName}}</c> or <c>{{Auth.UserId}}</c>.</summary>
    Template,

    /// <summary>The insert time (UTC), optionally shifted by <see cref="FieldDefaultValue.Offset"/>.</summary>
    Now,
}

/// <summary>
/// A property's default value, applied on insert only, and only when the property is missing or null.
/// A value the caller sends always wins.
/// </summary>
[BsonIgnoreExtraElements]
public class FieldDefaultValue
{
    [BsonRepresentation(BsonType.String)]
    public DefaultValueKind Kind { get; set; }

    /// <summary>
    /// Static: a scalar, or a list for a list property. Template: one template string, or a list of them
    /// for a list property. Now: unused.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>Now only: the shift from the insert time. Each part may be negative.</summary>
    [BsonIgnoreIfNull]
    public DefaultValueOffset? Offset { get; set; }
}

/// <summary>A shift added to "now" for a <see cref="DefaultValueKind.Now"/> default.</summary>
[BsonIgnoreExtraElements]
public class DefaultValueOffset
{
    public int Days { get; set; }
    public int Hours { get; set; }
    public int Minutes { get; set; }
    public int Seconds { get; set; }

    public TimeSpan ToTimeSpan() => new(Days, Hours, Minutes, Seconds);
}
