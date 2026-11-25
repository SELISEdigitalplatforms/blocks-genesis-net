using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>How a reference lookup came out.</summary>
public enum LookupOutcome
{
    /// <summary>Compare against the resolved values.</summary>
    Values,
    /// <summary>Nothing can match (EXISTS_IN is false for every row).</summary>
    Never,
    /// <summary>Everything matches (NOT_EXISTS_IN is true for every row).</summary>
    Always
}

/// <summary>The resolved result of a PolicyReference lookup, held on the rule for one evaluation.</summary>
public sealed record LookupResolution(LookupOutcome Outcome, BsonArray Values, IReadOnlySet<string> Keys)
{
    public static LookupResolution Never { get; } = new(LookupOutcome.Never, [], new HashSet<string>());

    public static LookupResolution Always { get; } = new(LookupOutcome.Always, [], new HashSet<string>());

    /// <summary>
    /// Compare against <paramref name="values"/>: the DB filter uses them as stored, the in-memory check their
    /// <see cref="Blocks.Genesis.DataAccess.Lookups.LookupKeys"/> (values without a key, such as null, are never matched).
    /// </summary>
    public static LookupResolution Of(IReadOnlyList<BsonValue> values) =>
        new(LookupOutcome.Values, new BsonArray(values),
            values.Select(Blocks.Genesis.DataAccess.Lookups.LookupKeys.Of).OfType<string>().ToHashSet(StringComparer.Ordinal));
}
