using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>The row an update produces, and the paths it touches.</summary>
public sealed record RowUpdateResult(BsonDocument Row, IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> UncheckablePaths);

/// <summary>
/// What a write changes in a row: the paths that differ between two rows, the paths of a whole row, and the row an
/// update document produces. Paths include their parents (<c>Contact</c> and <c>Contact.Email</c>), as
/// <see cref="ClsInputHelper.GetAllPathsFromInput"/> lists them, so column rules on either level apply.
/// </summary>
public static class RowChanges
{
    /// <summary>Every path in <paramref name="row"/> (root, nested and list items).</summary>
    public static List<string> AllPaths(BsonDocument row)
    {
        var paths = new List<string>();
        CollectAll(row, string.Empty, paths);
        return paths;
    }

    private static void CollectAll(BsonDocument document, string prefix, List<string> paths)
    {
        foreach (var element in document)
        {
            var path = Join(prefix, element.Name);
            paths.Add(path);
            CollectChildren(element.Value, path, paths);
        }
    }

    private static void CollectChildren(BsonValue value, string path, List<string> paths)
    {
        if (value is BsonDocument nested)
        {
            CollectAll(nested, path, paths);
            return;
        }

        if (value is not BsonArray array)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = path + "." + i;
            paths.Add(itemPath);
            if (array[i] is BsonDocument item)
                CollectAll(item, itemPath, paths);
        }
    }

    /// <summary>The paths whose value differs between <paramref name="before"/> and <paramref name="after"/>, with their parents.</summary>
    public static List<string> ChangedPaths(BsonDocument before, BsonDocument after)
    {
        var paths = new List<string>();
        CollectChanged(before, after, string.Empty, paths);
        return paths;
    }

    private static void CollectChanged(BsonDocument before, BsonDocument after, string prefix, List<string> paths)
    {
        foreach (var name in before.Names.Union(after.Names, StringComparer.Ordinal))
        {
            var path = Join(prefix, name);
            var hasBefore = before.TryGetValue(name, out var oldValue);
            var hasAfter = after.TryGetValue(name, out var newValue);
            if (hasBefore && hasAfter && BsonFilterMatcher.BsonEquals(oldValue!, newValue!))
                continue;

            paths.Add(path);
            if (hasBefore && hasAfter && oldValue is BsonDocument oldDocument && newValue is BsonDocument newDocument)
                CollectChanged(oldDocument, newDocument, path, paths);
            else if (hasAfter)
                CollectChildren(newValue!, path, paths);
        }
    }

    /// <summary>
    /// Applies a MongoDB update document to a copy of <paramref name="stored"/>. A replacement (no operators) is the new row.
    /// Applied: <c>$set</c>, <c>$unset</c>, <c>$inc</c>, <c>$currentDate</c>; <c>$setOnInsert</c> is ignored (it does not
    /// run on an update). Any other operator, and positional paths (<c>Items.$.Name</c>), are listed in
    /// <see cref="RowUpdateResult.UncheckablePaths"/>: their result is not computed and the stored value is kept.
    /// </summary>
    public static RowUpdateResult ApplyUpdate(BsonDocument stored, BsonDocument update)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(update);

        if (update.ElementCount == 0 || !update.GetElement(0).Name.StartsWith('$'))
            return new RowUpdateResult(update, ChangedPaths(stored, update), []);

        var row = stored.DeepClone().AsBsonDocument;
        var changed = new List<string>();
        var uncheckable = new List<string>();
        foreach (var operation in update)
        {
            if (operation.Name == "$setOnInsert" || operation.Value is not BsonDocument fields)
                continue;

            foreach (var field in fields)
            {
                AddWithParents(changed, field.Name);
                if (!Apply(row, operation.Name, field))
                    uncheckable.Add(field.Name);
            }
        }
        return new RowUpdateResult(row, changed.Distinct(StringComparer.Ordinal).ToList(), uncheckable);
    }

    private static bool Apply(BsonDocument row, string operation, BsonElement field)
    {
        var segments = field.Name.Split('.');
        if (segments.Any(s => s.StartsWith('$')))
            return false;

        return operation switch
        {
            "$set" => SetPath(row, segments, field.Value),
            "$unset" => UnsetPath(row, segments),
            "$inc" => Increment(row, segments, field.Value),
            "$currentDate" => SetPath(row, segments, new BsonDateTime(DateTime.UtcNow)),
            _ => false
        };
    }

    private static bool Increment(BsonDocument row, string[] segments, BsonValue amount)
    {
        if (!amount.IsNumeric)
            return false;
        var current = BsonFilterMatcher.FieldValues(row, string.Join('.', segments)).FirstOrDefault();
        if (current is null || current.IsBsonNull)
            return SetPath(row, segments, amount);
        if (!current.IsNumeric)
            return false;

        BsonValue sum = current.BsonType == BsonType.Int32 && amount.BsonType == BsonType.Int32
            ? new BsonInt64((long)current.AsInt32 + amount.AsInt32)
            : new BsonDouble(current.ToDouble() + amount.ToDouble());
        return SetPath(row, segments, sum);
    }

    private static bool SetPath(BsonDocument row, string[] segments, BsonValue value)
    {
        var container = Container(row, segments, create: true);
        if (container is BsonDocument document)
        {
            document[segments[^1]] = value;
            return true;
        }
        if (container is BsonArray array && int.TryParse(segments[^1], out var index) && index >= 0 && index < array.Count)
        {
            array[index] = value;
            return true;
        }
        return false;
    }

    private static bool UnsetPath(BsonDocument row, string[] segments)
    {
        var container = Container(row, segments, create: false);
        if (container is BsonDocument document)
            document.Remove(segments[^1]);
        else if (container is BsonArray array && int.TryParse(segments[^1], out var index) && index >= 0 && index < array.Count)
            array[index] = BsonNull.Value;
        return true;
    }

    /// <summary>The document or list that holds the last segment; created as documents along the way for a set.</summary>
    private static BsonValue? Container(BsonDocument row, string[] segments, bool create)
    {
        BsonValue current = row;
        foreach (var segment in segments[..^1])
        {
            current = Step(current, segment, create);
            if (current.IsBsonNull)
                return null;
        }
        return current;
    }

    private static BsonValue Step(BsonValue current, string segment, bool create)
    {
        if (current is BsonDocument document)
        {
            if (document.TryGetValue(segment, out var next))
                return next;
            if (!create)
                return BsonNull.Value;
            var created = new BsonDocument();
            document[segment] = created;
            return created;
        }

        return current is BsonArray array && int.TryParse(segment, out var index) && index >= 0 && index < array.Count
            ? array[index]
            : BsonNull.Value;
    }

    private static void AddWithParents(List<string> paths, string path)
    {
        var segments = path.Split('.');
        for (var i = 1; i <= segments.Length; i++)
            paths.Add(string.Join('.', segments[..i]));
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
}
