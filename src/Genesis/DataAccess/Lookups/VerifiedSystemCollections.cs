namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// System entities whose stored <c>CollectionName</c> is confirmed to be where their service writes them, so an
/// access lookup on them sees the real rows. Any other system entity is refused as a lookup target: a wrong
/// collection would find nothing, and NOT_EXISTS_IN would then match everyone. Adding an entity means confirming its
/// collection and adding a test.
/// </summary>
public static class VerifiedSystemCollections
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal) { "User", "Role" };

    /// <summary>True for exactly the verified system schema names (case-sensitive).</summary>
    public static bool Contains(string schemaName) => schemaName is not null && Names.Contains(schemaName);
}
