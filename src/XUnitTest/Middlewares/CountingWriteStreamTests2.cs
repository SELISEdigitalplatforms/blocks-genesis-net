using Blocks.Genesis;
using System.Reflection;

namespace XUnitTest.Middlewares;

/// <summary>
/// Coverage for the private <c>CountingWriteStream</c> nested in <see cref="TenantValidationMiddleware"/>,
/// which counts bytes written to the response while delegating all other stream operations.
/// </summary>
public class CountingWriteStreamTests2
{
    private static Stream NewStream(Stream inner)
    {
        var type = typeof(TenantValidationMiddleware).GetNestedType("CountingWriteStream", BindingFlags.NonPublic)!;
        return (Stream)Activator.CreateInstance(type, inner)!;
    }

    private static long BytesWritten(Stream stream) =>
        (long)stream.GetType().GetProperty("BytesWritten")!.GetValue(stream)!;

    [Fact]
    public async Task Write_ShouldCountBytes_AcrossAllWriteOverloads()
    {
        using var inner = new MemoryStream();
        var stream = NewStream(inner);

        stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
        stream.Write(new ReadOnlySpan<byte>(new byte[] { 4, 5 }));
        await stream.WriteAsync(new byte[] { 6 }, 0, 1);
        await stream.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 7, 8 }));

        Assert.Equal(8L, BytesWritten(stream));
        Assert.Equal(8, inner.Length);
    }

    [Fact]
    public async Task DelegatingMembers_ShouldForwardToInnerStream()
    {
        using var inner = new MemoryStream();
        var stream = NewStream(inner);
        stream.Write(new byte[] { 10, 20, 30, 40 }, 0, 4);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Equal(4, stream.Length);

        stream.Position = 0;
        Assert.Equal(0, stream.Position);

        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        stream.Position = 0;
        Assert.Equal(4, stream.Read(new Span<byte>(new byte[4])));
        stream.Position = 0;
        Assert.Equal(4, await stream.ReadAsync(new byte[4], 0, 4));
        stream.Position = 0;
        Assert.Equal(4, await stream.ReadAsync(new Memory<byte>(new byte[4])));

        Assert.Equal(2, stream.Seek(2, SeekOrigin.Begin));
        stream.SetLength(8);
        Assert.Equal(8, stream.Length);

        stream.Flush();
        await stream.FlushAsync();

        // Dispose must NOT close the (host-owned) inner stream.
        await stream.DisposeAsync();
        stream.Dispose();
        Assert.True(inner.CanWrite);
    }
}
