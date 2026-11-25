using System.Globalization;
using System.Numerics;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Lookups;

/// <summary>
/// The comparison key of a looked-up value, so the in-memory answer of EXISTS_IN / NOT_EXISTS_IN equals MongoDB's
/// <c>$in</c> / <c>$nin</c>: two values have the same key exactly when MongoDB finds them equal.
/// <list type="bullet">
/// <item>Numbers (Int32, Int64, Double, Decimal128) by exact value across types: 5 == 5L == 5.0 == 5m; no rounding
/// through double, so two different Int64 values never share a key. A Double whose exact value has more digits than a
/// Decimal128 can hold (34) can only equal another Double and is keyed by its round-trip text.</item>
/// <item>Text case-sensitive; bool, ObjectId, date (UTC milliseconds, as stored), binary (Guid included, by subtype
/// and bytes) and other types tagged by type, so none of them ever equals text or a number.</item>
/// <item>Null, missing, lists and objects: no key. They are never in the set (looked-up values are single values,
/// and <c>$in</c> doesn't look inside a nested list).</item>
/// </list>
/// Not <c>WriteRules.ReferenceKey</c>, which validation keeps for its own, looser comparison.
/// </summary>
public static class LookupKeys
{
    private const int Decimal128Digits = 34;

    /// <summary>The key of <paramref name="value"/>, or null when it can never be in a looked-up set.</summary>
    public static string? Of(BsonValue? value) => value switch
    {
        null or BsonNull or BsonUndefined or BsonArray or BsonDocument => null,
        BsonString text => "s:" + text.Value,
        BsonInt32 number => IntegerKey(number.Value),
        BsonInt64 number => IntegerKey(number.Value),
        BsonDouble number => DoubleKey(number.Value),
        BsonDecimal128 number => Decimal128Key(number.Value),
        BsonBoolean flag => flag.Value ? "b:true" : "b:false",
        BsonObjectId id => "o:" + id.Value.ToString(),
        BsonDateTime date => "t:" + date.MillisecondsSinceEpoch.ToString(CultureInfo.InvariantCulture),
        BsonBinaryData binary => "x:" + ((int)binary.SubType).ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(binary.Bytes),
        _ => value.BsonType.ToString() + ":" + value.ToString()
    };

    // Numbers are keyed as "n:<coefficient>e<exponent>" with no trailing zeros in the coefficient (0 is "n:0e0"):
    // one form per exact value.
    private static string NumberKey(BigInteger coefficient, int exponent)
    {
        if (coefficient.IsZero)
            return "n:0e0";
        while (coefficient % 10 == 0)
        {
            coefficient /= 10;
            exponent++;
        }
        return "n:" + coefficient.ToString(CultureInfo.InvariantCulture) + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static string IntegerKey(long value) => NumberKey(value, 0);

    private static string? SpecialKey(double value)
    {
        if (double.IsNaN(value))
            return "n:NaN";
        if (double.IsInfinity(value))
            return value > 0 ? "n:Infinity" : "n:-Infinity";
        return null;
    }

    private static string DoubleKey(double value)
    {
        if (SpecialKey(value) is { } special)
            return special;
        // value = ±mantissa * 2^exponent exactly.
        var bits = BitConverter.DoubleToInt64Bits(value);
        if ((bits & long.MaxValue) == 0)
            return "n:0e0"; // 0.0 or -0.0 (only the sign bit set): -0.0 equals 0

        var negative = bits < 0;
        var rawExponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (rawExponent == 0)
            rawExponent = 1; // subnormal
        else
            mantissa |= 1L << 52;
        var exponent = rawExponent - 1075;
        while ((mantissa & 1) == 0)
        {
            mantissa >>= 1;
            exponent++;
        }

        BigInteger coefficient;
        int decimalExponent;
        var mantissaBits = 64 - BitOperations.LeadingZeroCount((ulong)mantissa);
        if (exponent >= 0)
        {
            // An integer. Its trailing zeros come only from factors of 5 in the mantissa (at most 22 of them), so past
            // that margin it has more significant digits than a Decimal128 can hold.
            if ((mantissaBits + exponent) * 0.30103 > Decimal128Digits + 22 + 2)
                return RoundTripKey(value);
            coefficient = new BigInteger(mantissa) << exponent;
            decimalExponent = 0;
        }
        else
        {
            // mantissa / 2^k == mantissa * 5^k / 10^k; mantissa is odd, so mantissa * 5^k has no trailing zeros and
            // all of its digits are significant. Too many for a Decimal128: only a Double can be equal.
            var k = -exponent;
            var estimatedDigits = mantissaBits * 0.30103 + k * 0.69897;
            if (estimatedDigits > Decimal128Digits + 2)
                return RoundTripKey(value);
            coefficient = mantissa * BigInteger.Pow(5, k);
            decimalExponent = -k;
        }

        if (SignificantDigits(coefficient) > Decimal128Digits)
            return RoundTripKey(value);
        return NumberKey(negative ? -coefficient : coefficient, decimalExponent);
    }

    private static string RoundTripKey(double value) => "d:" + value.ToString("R", CultureInfo.InvariantCulture);

    private static int SignificantDigits(BigInteger coefficient)
    {
        var digits = BigInteger.Abs(coefficient).ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        return digits.Length;
    }

    private static string Decimal128Key(Decimal128 value)
    {
        if (Decimal128.IsNaN(value))
            return "n:NaN";
        if (Decimal128.IsInfinity(value))
            return Decimal128.IsNegative(value) ? "n:-Infinity" : "n:Infinity";

        // Text forms: "-12.50", "1.5E+3", "0E-6", "-0".
        var text = value.ToString();
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
            text = text[1..];
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }
        var coefficient = BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return NumberKey(negative ? -coefficient : coefficient, exponent);
    }
}
