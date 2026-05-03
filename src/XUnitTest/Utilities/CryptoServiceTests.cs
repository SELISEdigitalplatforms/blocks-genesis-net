using Blocks.Genesis;
using System.Security.Cryptography;
using System.Text;

namespace XUnitTest.Utilities;

public class CryptoServiceTests
{
    [Fact]
    public void Hash_WithString_ShouldReturnExpectedHexHash()
    {
        var service = new CryptoService();
        var expected = ComputeHex("hello");

        var actual = service.Hash("hello");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_WithStringBase64_ShouldReturnExpectedBase64()
    {
        var service = new CryptoService();
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("hello")));

        var actual = service.Hash("hello", makeBase64: true);

        Assert.Equal(expected, actual);
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

    private static string ComputeHex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}