using System.Collections;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>A mutation input dictionary as a BSON document, to check it against a row rule.</summary>
public static class InputDocument
{
    public static BsonDocument From(IDictionary<string, object?> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var document = new BsonDocument();
        foreach (var entry in input)
            document[entry.Key] = ToBson(entry.Value);
        return document;
    }

    private static BsonValue ToBson(object? value)
    {
        switch (value)
        {
            case null:
                return BsonNull.Value;
            case BsonValue bson:
                return bson;
            case string text:
                return new BsonString(text);
            case IDictionary<string, object?> nested:
                return From(nested);
            case IEnumerable items:
                return new BsonArray(items.Cast<object?>().Select(ToBson));
        }

        try
        {
            return BsonValue.Create(value);
        }
        catch (ArgumentException)
        {
            // No BSON mapping: compare it as its text.
            return new BsonString(value.ToString() ?? string.Empty);
        }
    }
}
