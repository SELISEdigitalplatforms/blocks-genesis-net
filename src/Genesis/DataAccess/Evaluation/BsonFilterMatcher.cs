using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Matches one document against a MongoDB filter in memory, with MongoDB's semantics for the filters the policy
/// evaluator builds (<see cref="DataAccessPolicyHelper"/>): field equality, <c>$eq $ne $gt $gte $lt $lte $in $nin
/// $regex $not $exists $size $all</c>, <c>$and $or $nor</c>, and <c>$expr</c> with <c>$eq $ne $gt $gte $lt $lte $and
/// $or $not $cond $isArray $size $setIntersection $setIsSubset $in $literal</c>. Used to check a written row against
/// a row policy without a database round trip. Anything else throws <see cref="NotSupportedException"/>, so a caller
/// can refuse instead of guessing.
/// </summary>
public static class BsonFilterMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>True when <paramref name="row"/> matches <paramref name="filter"/>. An empty filter matches every row.</summary>
    public static bool Matches(BsonDocument row, BsonDocument filter)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(filter);
        return filter.All(element => MatchElement(row, element));
    }

    private static bool MatchElement(BsonDocument row, BsonElement element) => element.Name switch
    {
        "$and" => Clauses(element.Value).All(c => Matches(row, c)),
        "$or" => Clauses(element.Value).Any(c => Matches(row, c)),
        "$nor" => !Clauses(element.Value).Any(c => Matches(row, c)),
        "$expr" => Truthy(Evaluate(element.Value, row)),
        _ when element.Name.StartsWith('$') => throw Unsupported(element.Name),
        _ => MatchField(FieldValues(row, element.Name), element.Value)
    };

    private static IEnumerable<BsonDocument> Clauses(BsonValue value) =>
        value is BsonArray array && array.All(v => v is BsonDocument)
            ? array.Select(v => v.AsBsonDocument)
            : throw Unsupported("a logical operator without a list of filters");

    private static NotSupportedException Unsupported(string what) =>
        new($"The row policy filter uses {what}, which cannot be checked in memory.");

    #region Field conditions

    private static bool MatchField(List<BsonValue> values, BsonValue condition) =>
        IsOperatorDocument(condition)
            ? MatchOperators(values, condition.AsBsonDocument)
            : condition is BsonRegularExpression regex
                ? AnyCandidate(values, v => RegexMatches(v, regex.Pattern, regex.Options))
                : EqualsAny(values, condition);

    private static bool IsOperatorDocument(BsonValue value) =>
        value is BsonDocument document && document.ElementCount > 0 && document.GetElement(0).Name.StartsWith('$');

    private static bool MatchOperators(List<BsonValue> values, BsonDocument operators)
    {
        foreach (var element in operators)
        {
            if (!MatchOperator(values, element, operators))
                return false;
        }
        return true;
    }

    private static bool MatchOperator(List<BsonValue> values, BsonElement element, BsonDocument operators) => element.Name switch
    {
        "$eq" => EqualsAny(values, element.Value),
        "$ne" => !EqualsAny(values, element.Value),
        "$in" => InList(values, element.Value),
        "$nin" => !InList(values, element.Value),
        "$gt" => CompareAny(values, element.Value, c => c > 0, nullMatches: false),
        "$gte" => CompareAny(values, element.Value, c => c >= 0, nullMatches: true),
        "$lt" => CompareAny(values, element.Value, c => c < 0, nullMatches: false),
        "$lte" => CompareAny(values, element.Value, c => c <= 0, nullMatches: true),
        "$regex" => MatchRegex(values, element.Value, operators),
        "$options" => true, // read together with $regex
        "$not" => !MatchField(values, element.Value),
        "$exists" => (values.Count > 0) == Truthy(element.Value),
        "$size" => values.Any(v => v is BsonArray array && Number(element.Value) is { } size && array.Count == size),
        "$all" => element.Value is BsonArray all && all.All(item => EqualsAny(values, item)),
        _ => throw Unsupported(element.Name)
    };

    /// <summary>Equality as MongoDB applies it: the value itself, or any item when the field holds a list. Null also matches a missing field.</summary>
    private static bool EqualsAny(List<BsonValue> values, BsonValue target)
    {
        if (target.IsBsonNull && values.Count == 0)
            return true;
        return AnyCandidate(values, v => BsonEquals(v, target));
    }

    private static bool InList(List<BsonValue> values, BsonValue list)
    {
        if (list is not BsonArray items)
            throw Unsupported("$in / $nin without a list");
        return items.Any(item => item is BsonRegularExpression regex
            ? AnyCandidate(values, v => RegexMatches(v, regex.Pattern, regex.Options))
            : EqualsAny(values, item));
    }

    private static bool CompareAny(List<BsonValue> values, BsonValue target, Func<int, bool> test, bool nullMatches)
    {
        if (target.IsBsonNull)
            return nullMatches && EqualsAny(values, target);
        return AnyCandidate(values, v => CompareSameType(v, target) is { } c && test(c));
    }

    private static bool MatchRegex(List<BsonValue> values, BsonValue pattern, BsonDocument operators)
    {
        var options = operators.TryGetValue("$options", out var o) && o.IsString ? o.AsString : string.Empty;
        return pattern switch
        {
            BsonString text => AnyCandidate(values, v => RegexMatches(v, text.Value, options)),
            BsonRegularExpression regex => AnyCandidate(values, v => RegexMatches(v, regex.Pattern, options.Length > 0 ? options : regex.Options)),
            _ => throw Unsupported("$regex without a pattern")
        };
    }

    private static bool RegexMatches(BsonValue value, string pattern, string options)
    {
        if (!value.IsString)
            return false;
        var regexOptions = RegexOptions.None;
        foreach (var option in options)
        {
            regexOptions |= option switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None
            };
        }

        try
        {
            return Regex.IsMatch(value.AsString, pattern, regexOptions, RegexTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new NotSupportedException("The row policy's pattern took too long to check.", ex);
        }
    }

    /// <summary>The test holds for a value or, when the value is a list, for any of its items.</summary>
    private static bool AnyCandidate(List<BsonValue> values, Func<BsonValue, bool> test) =>
        values.Any(v => test(v) || (v is BsonArray array && array.Any(test)));

    #endregion

    #region Field paths

    /// <summary>The field paths a filter reads: its field keys and the <c>"$field"</c> references inside <c>$expr</c>.</summary>
    public static HashSet<string> ReferencedPaths(BsonDocument filter)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectReferences(filter, inExpression: false, paths);
        return paths;
    }

    private static void CollectReferences(BsonValue value, bool inExpression, HashSet<string> paths)
    {
        switch (value)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    if (!inExpression && !element.Name.StartsWith('$'))
                        paths.Add(element.Name);
                    CollectReferences(element.Value, inExpression || element.Name == "$expr", paths);
                }
                break;
            case BsonArray array:
                foreach (var item in array)
                    CollectReferences(item, inExpression, paths);
                break;
            case BsonString text when inExpression && text.Value.Length > 1 && text.Value[0] == '$' && text.Value[1] != '$':
                paths.Add(text.Value[1..]);
                break;
        }
    }

    /// <summary>
    /// The values a dotted path reaches, as a query sees them: through sub-documents, and through lists (each item, or
    /// the item at a numeric segment). Empty when the path is missing. <c>ItemId</c> falls back to <c>_id</c>.
    /// </summary>
    public static List<BsonValue> FieldValues(BsonDocument row, string path)
    {
        var values = new List<BsonValue>();
        var segments = path.Split('.');
        Collect(row, segments, 0, values);
        if (values.Count == 0 && segments[0] == "ItemId" && row.Contains(PolicyConstants.DbEntityIdFieldName))
        {
            segments[0] = PolicyConstants.DbEntityIdFieldName;
            Collect(row, segments, 0, values);
        }
        return values;
    }

    private static void Collect(BsonValue current, string[] segments, int index, List<BsonValue> output)
    {
        if (index == segments.Length)
        {
            output.Add(current);
            return;
        }

        var segment = segments[index];
        if (current is BsonDocument document)
        {
            if (document.TryGetValue(segment, out var next))
                Collect(next, segments, index + 1, output);
            return;
        }

        if (current is not BsonArray array)
            return;

        if (int.TryParse(segment, out var position) && position >= 0 && position < array.Count)
            Collect(array[position], segments, index + 1, output);
        foreach (var item in array.OfType<BsonDocument>())
            Collect(item, segments, index, output);
    }

    #endregion

    #region Comparison

    /// <summary>BSON equality, with numbers equal across int, long, double and decimal.</summary>
    public static bool BsonEquals(BsonValue left, BsonValue right)
    {
        if (left.IsNumeric && right.IsNumeric)
            return CompareNumbers(left, right) == 0;
        if (left.BsonType != right.BsonType)
            return false;
        return left switch
        {
            BsonArray leftArray => right is BsonArray rightArray && leftArray.Count == rightArray.Count &&
                                   leftArray.Zip(rightArray).All(pair => BsonEquals(pair.First, pair.Second)),
            BsonDocument leftDocument => right is BsonDocument rightDocument && leftDocument.ElementCount == rightDocument.ElementCount &&
                                         leftDocument.Zip(rightDocument).All(pair => pair.First.Name == pair.Second.Name && BsonEquals(pair.First.Value, pair.Second.Value)),
            _ => left.Equals(right)
        };
    }

    /// <summary>A query comparison: only values of the same kind compare (numbers, text, dates, booleans, ids); null otherwise.</summary>
    private static int? CompareSameType(BsonValue left, BsonValue right)
    {
        if (left.IsNumeric && right.IsNumeric)
            return CompareNumbers(left, right);
        if (left.BsonType != right.BsonType)
            return null;
        return left.BsonType switch
        {
            BsonType.String => string.CompareOrdinal(left.AsString, right.AsString),
            BsonType.DateTime => left.ToUniversalTime().CompareTo(right.ToUniversalTime()),
            BsonType.Boolean => left.AsBoolean.CompareTo(right.AsBoolean),
            BsonType.ObjectId => left.AsObjectId.CompareTo(right.AsObjectId),
            _ => null
        };
    }

    private static int CompareNumbers(BsonValue left, BsonValue right)
    {
        if (left.BsonType == BsonType.Decimal128 || right.BsonType == BsonType.Decimal128)
        {
            try
            {
                return left.ToDecimal().CompareTo(right.ToDecimal());
            }
            catch (OverflowException)
            {
                // Out of decimal range: compare as double below.
            }
        }
        return left.ToDouble().CompareTo(right.ToDouble());
    }

    /// <summary>The aggregation order across types (null &lt; numbers &lt; text &lt; documents &lt; lists &lt; ... ).</summary>
    private static int CompareAnyType(BsonValue left, BsonValue right)
    {
        var byType = TypeOrder(left).CompareTo(TypeOrder(right));
        if (byType != 0)
            return byType;
        if (CompareSameType(left, right) is { } same)
            return same;
        return BsonEquals(left, right) ? 0 : left.CompareTo(right);
    }

    private static int TypeOrder(BsonValue value) => value.BsonType switch
    {
        BsonType.MinKey => 0,
        BsonType.Undefined => 1, // a missing field in an expression: below null, and not equal to it
        BsonType.Null => 2,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => 3,
        BsonType.String or BsonType.Symbol => 4,
        BsonType.Document => 5,
        BsonType.Array => 6,
        BsonType.Binary => 7,
        BsonType.ObjectId => 8,
        BsonType.Boolean => 9,
        BsonType.DateTime => 10,
        BsonType.Timestamp => 11,
        BsonType.RegularExpression => 12,
        _ => 13
    };

    private static long? Number(BsonValue value) => value.IsNumeric ? (long)value.ToDouble() : null;

    private static bool Truthy(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => false,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => value.ToDouble() != 0,
        _ => true
    };

    #endregion

    #region $expr

    private static BsonValue Evaluate(BsonValue expression, BsonDocument row) => expression switch
    {
        BsonString text when text.Value.StartsWith("$$", StringComparison.Ordinal) => throw Unsupported("a variable"),
        BsonString text when text.Value.StartsWith('$') => ExpressionFieldValue(row, text.Value[1..]),
        // Inside a list literal a missing field becomes null, as in MongoDB.
        BsonArray array => new BsonArray(array.Select(item => Evaluate(item, row)).Select(v => v.IsBsonUndefined ? BsonNull.Value : v)),
        BsonDocument document when IsOperatorDocument(document) && document.ElementCount == 1 => EvaluateOperator(document.GetElement(0), row),
        BsonDocument document when IsOperatorDocument(document) => throw Unsupported("an expression with several operators"),
        BsonDocument document => new BsonDocument(document.Select(e => new BsonElement(e.Name, Evaluate(e.Value, row)))),
        _ => expression
    };

    /// <summary>A field in an expression: through lists of sub-documents it yields the list of their values; missing is <see cref="BsonUndefined"/>.</summary>
    private static BsonValue ExpressionFieldValue(BsonValue current, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                BsonDocument document => document.TryGetValue(segment, out var next) ? next : BsonUndefined.Value,
                BsonArray array => new BsonArray(array.OfType<BsonDocument>()
                    .Where(d => d.Contains(segment))
                    .Select(d => d[segment])),
                _ => BsonUndefined.Value
            };
            if (current.IsBsonUndefined)
                return current;
        }
        return current;
    }

    private static BsonValue EvaluateOperator(BsonElement element, BsonDocument row)
    {
        if (element.Name == "$literal")
            return element.Value;
        if (element.Name == "$cond")
            return EvaluateCondition(element.Value, row);

        var arguments = Arguments(element.Value, row);
        return element.Name switch
        {
            "$eq" => Compare(arguments, c => c == 0),
            "$ne" => Compare(arguments, c => c != 0),
            "$gt" => Compare(arguments, c => c > 0),
            "$gte" => Compare(arguments, c => c >= 0),
            "$lt" => Compare(arguments, c => c < 0),
            "$lte" => Compare(arguments, c => c <= 0),
            "$and" => arguments.All(Truthy),
            "$or" => arguments.Any(Truthy),
            "$not" => !Truthy(Single(arguments)),
            "$isArray" => Single(arguments) is BsonArray,
            "$size" => Single(arguments) is BsonArray sized ? sized.Count : throw Unsupported("$size of a value that is not a list"),
            "$setIntersection" => SetIntersection(arguments),
            "$setIsSubset" => SetIsSubset(arguments),
            "$in" => arguments.Count == 2 && arguments[1] is BsonArray candidates
                ? candidates.Any(c => BsonEquals(c, arguments[0]))
                : throw Unsupported("$in without a list"),
            _ => throw Unsupported(element.Name)
        };
    }

    // An argument list is written as a list; a single argument may be written without one ({ $isArray: "$Tags" }).
    private static List<BsonValue> Arguments(BsonValue arguments, BsonDocument row) =>
        arguments is BsonArray array ? array.Select(item => Evaluate(item, row)).ToList() : [Evaluate(arguments, row)];

    private static BsonValue Single(List<BsonValue> arguments) =>
        arguments.Count == 1 ? arguments[0] : throw Unsupported("an operator with the wrong number of arguments");

    private static BsonBoolean Compare(List<BsonValue> arguments, Func<int, bool> test) =>
        arguments.Count == 2 ? test(CompareAnyType(arguments[0], arguments[1])) : throw Unsupported("a comparison without two arguments");

    private static BsonValue EvaluateCondition(BsonValue arguments, BsonDocument row)
    {
        var (condition, then, otherwise) = arguments switch
        {
            BsonArray { Count: 3 } array => (array[0], array[1], array[2]),
            BsonDocument document when document.Contains("if") && document.Contains("then") && document.Contains("else") =>
                (document["if"], document["then"], document["else"]),
            _ => throw Unsupported("$cond without if, then and else")
        };
        return Truthy(Evaluate(condition, row)) ? Evaluate(then, row) : Evaluate(otherwise, row);
    }

    private static BsonValue SetIntersection(List<BsonValue> arguments)
    {
        if (arguments.Any(a => a.IsBsonNull || a.IsBsonUndefined))
            return BsonNull.Value;
        if (arguments.Count == 0 || arguments.Any(a => a is not BsonArray))
            throw Unsupported("$setIntersection of values that are not lists");

        var result = new BsonArray();
        foreach (var item in arguments[0].AsBsonArray)
        {
            if (!result.Any(r => BsonEquals(r, item)) && arguments.Skip(1).All(a => a.AsBsonArray.Any(other => BsonEquals(other, item))))
                result.Add(item);
        }
        return result;
    }

    private static BsonBoolean SetIsSubset(List<BsonValue> arguments)
    {
        if (arguments.Count != 2 || arguments[0] is not BsonArray subset || arguments[1] is not BsonArray superset)
            throw Unsupported("$setIsSubset of values that are not two lists");
        return subset.All(item => superset.Any(other => BsonEquals(other, item)));
    }

    #endregion
}
