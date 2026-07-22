using Blocks.Genesis;
using System.Text;

namespace XUnitTest.Utilities;

/// <summary>Branch coverage for <see cref="CryptoService"/> hashing, HMAC and constant-time comparison.</summary>
public class CryptoServiceBranchTests
{
    private readonly CryptoService _sut = new();

    [Fact]
    public void Hash_String_ShouldBeDeterministic_SaltSensitive_AndSupportBase64()
    {
        var hex = _sut.Hash("value", "salt");
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.Equal(hex, _sut.Hash("value", "salt"));
        Assert.NotEqual(hex, _sut.Hash("value", "other-salt"));
        Assert.Equal(_sut.Hash("value"), _sut.Hash("value", null));

        var base64 = _sut.Hash("value", "salt", makeBase64: true);
        Assert.NotEqual(hex, base64);
        Assert.Equal(44, base64.Length); // 32-byte digest in base64
    }

    [Fact]
    public void Hash_Bytes_ShouldSupportHexAndBase64()
    {
        var data = Encoding.UTF8.GetBytes("payload");
        var hex = _sut.Hash(data);
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.NotEqual(hex, _sut.Hash(data, makeBase64: true));
    }

    [Fact]
    public void ComputeHmacSha256_ShouldSupportHexAndBase64_AndBeNullSafe()
    {
        var hex = _sut.ComputeHmacSha256("message", "secret");
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.Equal(hex, _sut.ComputeHmacSha256("message", "secret"));
        Assert.NotEqual(hex, _sut.ComputeHmacSha256("message", "secret", makeBase64: true));
        Assert.NotEqual(hex, _sut.ComputeHmacSha256("message", "different-key"));
        Assert.False(string.IsNullOrEmpty(_sut.ComputeHmacSha256(null!, null!)));
    }

    [Fact]
    public void ConstantTimeEquals_ShouldCompareContent()
    {
        Assert.True(_sut.ConstantTimeEquals("token-abc", "token-abc"));
        Assert.False(_sut.ConstantTimeEquals("token-abc", "token-xyz"));
        Assert.False(_sut.ConstantTimeEquals("short", "longer-value"));
        Assert.True(_sut.ConstantTimeEquals(null!, null!));
    }
}
