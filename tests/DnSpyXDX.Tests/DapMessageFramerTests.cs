using System.Text;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DapMessageFramerTests
{
    [Fact]
    public async Task Reads_partial_header_and_payload_chunks()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "Content-Length: 11\r\nContent-Type: application/json\r\n\r\n{\"ok\":true}");
        await using var stream = new ChunkedReadStream(bytes, 2);
        var framer = new DapMessageFramer();

        var payload = await framer.ReadAsync(stream);

        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Reads_multiple_frames_without_consuming_next_header()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "Content-Length: 3\r\n\r\noneContent-Length: 3\r\n\r\ntwo");
        await using var stream = new ChunkedReadStream(bytes, 64);
        var framer = new DapMessageFramer();

        var first = await framer.ReadAsync(stream);
        var second = await framer.ReadAsync(stream);
        var end = await framer.ReadAsync(stream);

        Assert.Equal("one", Encoding.UTF8.GetString(first!));
        Assert.Equal("two", Encoding.UTF8.GetString(second!));
        Assert.Null(end);
    }

    [Fact]
    public async Task Writes_utf8_byte_length_and_round_trips()
    {
        var framer = new DapMessageFramer();
        var payload = Encoding.UTF8.GetBytes("{\"text\":\"λ\"}");
        await using var stream = new MemoryStream();

        await framer.WriteAsync(stream, payload);
        stream.Position = 0;
        var read = await framer.ReadAsync(stream);

        Assert.Equal(payload, read);
    }

    [Theory]
    [InlineData("Other: 1\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: x\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\na")]
    [InlineData("broken\r\n\r\n")]
    public async Task Rejects_malformed_headers(string message)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(message));
        var framer = new DapMessageFramer();

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await framer.ReadAsync(stream));
    }

    [Fact]
    public async Task Rejects_oversized_payload_before_allocation()
    {
        await using var stream = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n12345"));
        var framer = new DapMessageFramer(maximumPayloadBytes: 4);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await framer.ReadAsync(stream));

        Assert.Contains("exceeds 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_truncated_payload()
    {
        await using var stream = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n123"));
        var framer = new DapMessageFramer();

        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await framer.ReadAsync(stream));
    }

    private sealed class ChunkedReadStream(
        byte[] data,
        int maximumChunkSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(
                buffer[..Math.Min(buffer.Length, maximumChunkSize)],
                cancellationToken);
    }
}
