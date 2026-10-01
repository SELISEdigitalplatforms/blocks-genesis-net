using Blocks.Genesis;
using System.Text;
using Xunit;

namespace XUnitTest.Utilities;

public class CryptoServiceEncryptionTests
{
    private const string Salt = "9f2c1d4ea77b40c8a1e3b6d5c0f81a72";   // shaped like a TenantSalt
    private const string Secret = "s3cr3t-hs256-signing-value";

    private readonly CryptoService _crypto = new();

    [Fact]
    public void Roundtrips()
    {
        var envelope = _crypto.Encrypt(Secret, Salt);

        Assert.NotEqual(Secret, envelope);
        Assert.Equal(Secret, _crypto.Decrypt(envelope, Salt));
    }

    [Fact]
    public void ProducesADifferentEnvelopeEachTime()
    {
        // A fresh nonce per call, so identical secrets for two providers do not produce identical
        // ciphertext — otherwise the store leaks which providers share a secret.
        var first = _crypto.Encrypt(Secret, Salt);
        var second = _crypto.Encrypt(Secret, Salt);

        Assert.NotEqual(first, second);
        Assert.Equal(Secret, _crypto.Decrypt(first, Salt));
        Assert.Equal(Secret, _crypto.Decrypt(second, Salt));
    }

    [Fact]
    public void ReturnsNull_ForTheWrongKeyMaterial()
    {
        var envelope = _crypto.Encrypt(Secret, Salt);

        Assert.Null(_crypto.Decrypt(envelope, "a-different-tenant-salt"));
    }

    [Fact]
    public void ReturnsNull_WhenTheCiphertextIsTampered()
    {
        var raw = Convert.FromBase64String(_crypto.Encrypt(Secret, Salt));
        raw[^1] ^= 0xFF;   // flip a bit in the authentication tag

        Assert.Null(_crypto.Decrypt(Convert.ToBase64String(raw), Salt));
    }

    [Fact]
    public void ReturnsNull_ForAnUnknownEnvelopeVersion()
    {
        // The version byte is what lets the scheme change later; an unrecognised one must decline
        // rather than be interpreted under today's rules.
        var raw = Convert.FromBase64String(_crypto.Encrypt(Secret, Salt));
        raw[0] = 0x7F;

        Assert.Null(_crypto.Decrypt(Convert.ToBase64String(raw), Salt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-$$$")]
    [InlineData("AQID")]           // valid base64, far too short to hold nonce + tag
    public void ReturnsNull_ForMalformedInput(string envelope)
    {
        Assert.Null(_crypto.Decrypt(envelope, Salt));
    }

    [Fact]
    public void ReturnsNull_WhenKeyMaterialIsMissing()
    {
        var envelope = _crypto.Encrypt(Secret, Salt);

        Assert.Null(_crypto.Decrypt(envelope, string.Empty));
        Assert.Null(_crypto.Decrypt(envelope, "   "));
    }

    [Fact]
    public void RejectsMissingKeyMaterial_OnEncrypt()
    {
        // Encrypting under an empty key would silently produce a fixed, guessable key — the worst
        // outcome available, so it throws rather than degrading.
        Assert.Throws<ArgumentException>(() => _crypto.Encrypt(Secret, string.Empty));
        Assert.Throws<ArgumentException>(() => _crypto.Encrypt(Secret, "   "));
        Assert.Throws<ArgumentNullException>(() => _crypto.Encrypt(Secret, null!));
    }

    [Fact]
    public void HandlesEmptyAndUnicodePlaintext()
    {
        Assert.Equal(string.Empty, _crypto.Decrypt(_crypto.Encrypt(string.Empty, Salt), Salt));

        const string unicode = "sécret-你好-🔐";
        Assert.Equal(unicode, _crypto.Decrypt(_crypto.Encrypt(unicode, Salt), Salt));
    }

    [Fact]
    public void EnvelopeLayoutIsStable()
    {
        // Guards the wire format blocks-genesis-py has to reproduce byte for byte:
        // version(1) | nonce(12) | ciphertext(n) | tag(16), base64.
        var plainLength = Encoding.UTF8.GetByteCount(Secret);
        var raw = Convert.FromBase64String(_crypto.Encrypt(Secret, Salt));

        Assert.Equal(1, raw[0]);
        Assert.Equal(1 + 12 + plainLength + 16, raw.Length);
    }
}
