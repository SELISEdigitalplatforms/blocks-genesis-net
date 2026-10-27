using System.Collections;
using System.Globalization;
using Blocks.Genesis.DataAccess.Evaluation;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// The record an "only when" condition is checked on: on insert the input, on update the stored record with the input on
/// top, where an explicit null clears a property. Values are typed from the schema (a date sent as text becomes a date,
/// a number sent as text a number), so the condition compares them as Mongo would.
/// </summary>
public static class ValidationRecord
{
    /// <summary>
    /// The merged record, from what a caller of <c>Validate</c> passes: the stored values, then the input on top. The raw
    /// input, when given, is the input as sent (explicit nulls included); otherwise the input values are.
    /// </summary>
    internal static BsonDocument Build(IReadOnlyDictionary<string, object?> input, BsonDocument? rawInput, IReadOnlyDictionary<string, object?>? stored, IReadOnlyList<FieldDefinitionResponse> fields)
    {
        var merged = new BsonDocument();
        foreach (var (name, value) in (IEnumerable<KeyValuePair<string, object?>>?)stored ?? [])
            merged[name] = ToBson(value);
        foreach (var (name, value) in input)
            merged[name] = ToBson(value);
        foreach (var element in (IEnumerable<BsonElement>?)rawInput ?? [])
            merged[element.Name] = element.Value;
        return Typed(merged, fields);
    }

    /// <summary>A copy with each property's value read as its schema type, where it was sent as text.</summary>
    internal static BsonDocument Typed(BsonDocument record, IReadOnlyList<FieldDefinitionResponse> fields)
    {
        var typed = new BsonDocument();
        foreach (var element in record)
        {
            var field = fields.FirstOrDefault(f => f.Name == element.Name);
            typed[element.Name] = field is null ? element.Value : TypedValue(element.Value, field);
        }
        return typed;
    }

    private static BsonValue TypedValue(BsonValue value, FieldDefinitionResponse field)
    {
        if (value is BsonArray items && field.IsArray)
            return new BsonArray(items.Select(item => TypedItem(item, field)));
        return TypedItem(value, field);
    }

    private static BsonValue TypedItem(BsonValue value, FieldDefinitionResponse field)
    {
        if (value is BsonDocument nested && field.Fields.Count > 0)
            return Typed(nested, field.Fields);
        if (value is not BsonString { Value: var text })
            return value;

        return field.Type switch
        {
            "DateTime" when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) => new BsonDateTime(date),
            "Int" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) => new BsonInt64(whole),
            "Float" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => new BsonDouble(number),
            "Boolean" when bool.TryParse(text, out var flag) => BsonBoolean.Create(flag),
            _ => value,
        };
    }

    /// <summary>The value at a dotted path, as the plain values the validator works with; null when missing or null.</summary>
    internal static object? Read(BsonDocument record, string path)
    {
        BsonValue current = record;
        foreach (var segment in path.Split('.'))
        {
            if (current is not BsonDocument document || !document.TryGetValue(segment, out current))
                return null;
        }
        return FromBson(current);
    }

    private static object? FromBson(BsonValue value) => value switch
    {
        BsonNull or BsonUndefined => null,
        BsonDocument document => document.ToDictionary(e => e.Name, e => FromBson(e.Value)),
        BsonArray array => array.Select(FromBson).ToList(),
        BsonDateTime date => date.ToUniversalTime(),
        _ => BsonTypeMapper.MapToDotNetValue(value),
    };

    private static BsonValue ToBson(object? value) => value switch
    {
        null => BsonNull.Value,
        BsonValue bson => bson,
        string text => new BsonString(text),
        DateTime date => new BsonDateTime(date),
        IDictionary<string, object?> map => new BsonDocument(map.Select(e => new BsonElement(e.Key, ToBson(e.Value)))),
        IEnumerable items => new BsonArray(items.Cast<object?>().Select(ToBson)),
        _ => BsonTypeMapper.TryMapToBsonValue(value, out var mapped) ? mapped : new BsonString(value.ToString() ?? string.Empty),
    };
}
