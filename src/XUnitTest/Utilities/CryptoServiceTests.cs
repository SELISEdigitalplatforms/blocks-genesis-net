using Blocks.Genesis;
using System.Security.Cryptography;
using System.Text;

namespace XUnitTest.Utilities;

public class CryptoServiceTests
{
    [Fact]
    public void Hash_WithStringAndSalt_ShouldReturnExpectedHexHash()
    {
        var service = new CryptoService();
        var expected = ComputeHex("hello" + "salt");

        var actual = service.Hash("hello", "salt");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_WithNullSalt_ShouldBehaveLikeEmptySalt()
    {
        var service = new CryptoService();

        var withNullSalt = service.Hash("hello", null!);
        var withEmptySalt = service.Hash("hello", string.Empty);

        Assert.Equal(withEmptySalt, withNullSalt);
    }

    [Fact]
    public void Hash_WithByteArrayAndBase64_ShouldReturnExpectedBase64()
    {
        var service = new CryptoService();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var expected = Convert.ToBase64String(SHA256.HashData(bytes));

        var actual = service.Hash(bytes, makeBase64: true);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_WithByteArrayAndDefaultFlag_ShouldReturnHexEncodedHash()
    {
        var service = new CryptoService();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var expected = BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();

        var actual = service.Hash(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(64, actual.Length);
        Assert.Matches("^[0-9a-f]+$", actual);
    }

    [Fact]
    public void ComputeHmacSha256_ShouldReturnLowercaseHex_ByDefault()
    {
        var service = new CryptoService();
        var expected = ComputeHmacHex("message", "key");

        var actual = service.ComputeHmacSha256("message", "key");

        Assert.Equal(expected, actual);
        Assert.Matches("^[0-9a-f]+$", actual);
    }

    [Fact]
    public void ComputeHmacSha256_ShouldReturnBase64_WhenFlagIsTrue()
    {
        var service = new CryptoService();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("key"));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("message")));

        var actual = service.ComputeHmacSha256("message", "key", makeBase64: true);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeHmacSha256_ShouldTreatNullMessageAndKey_AsEmpty()
    {
        var service = new CryptoService();

        var withNulls = service.ComputeHmacSha256(null!, null!);
        var withEmpty = service.ComputeHmacSha256(string.Empty, string.Empty);

        Assert.Equal(withEmpty, withNulls);
    }

    [Fact]
    public void ComputeHmacSha256_ShouldProduceDifferentOutputs_ForDifferentKeys()
    {
        var service = new CryptoService();

        var first = service.ComputeHmacSha256("message", "key-1");
        var second = service.ComputeHmacSha256("message", "key-2");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ConstantTimeEquals_ShouldReturnTrue_ForEqualStrings()
    {
        var service = new CryptoService();

        Assert.True(service.ConstantTimeEquals("secret-value", "secret-value"));
    }

    [Fact]
    public void ConstantTimeEquals_ShouldReturnFalse_ForDifferentStrings()
    {
        var service = new CryptoService();

        Assert.False(service.ConstantTimeEquals("secret-value", "secret-other"));
    }

    [Fact]
    public void ConstantTimeEquals_ShouldReturnFalse_ForDifferentLengths()
    {
        var service = new CryptoService();

        // FixedTimeEquals returns false when lengths differ.
        Assert.False(service.ConstantTimeEquals("short", "much-longer-string"));
    }

    [Fact]
    public void ConstantTimeEquals_ShouldTreatNulls_AsEmpty()
    {
        var service = new CryptoService();

        Assert.True(service.ConstantTimeEquals(null!, null!));
        Assert.True(service.ConstantTimeEquals(null!, string.Empty));
        Assert.False(service.ConstantTimeEquals(null!, "x"));
    }

    private static string ComputeHex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static string ComputeHmacHex(string message, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}