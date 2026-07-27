using System.Buffers;
using System.Globalization;
using System.Text;

namespace DnSpyXDX.Debugging;

/// <summary>Reads and writes Debug Adapter Protocol Content-Length frames.</summary>
public sealed class DapMessageFramer
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumPayloadBytes = 8 * 1024 * 1024;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly int maximumHeaderBytes;
    private readonly int maximumPayloadBytes;

    public DapMessageFramer(
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHeaderBytes, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadBytes);
        this.maximumHeaderBytes = maximumHeaderBytes;
        this.maximumPayloadBytes = maximumPayloadBytes;
    }

    public async ValueTask<byte[]?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new ArrayBufferWriter<byte>();
        var next = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (header.WrittenCount == 0) return null;
                throw new EndOfStreamException("DAP stream ended inside a message header.");
            }

            header.Write(next);
            if (header.WrittenCount > maximumHeaderBytes)
                throw new InvalidDataException(
                    $"DAP header exceeds {maximumHeaderBytes} bytes.");
            if (header.WrittenCount >= HeaderTerminator.Length &&
                header.WrittenSpan[^HeaderTerminator.Length..]
                    .SequenceEqual(HeaderTerminator))
                break;
        }

        var contentLength = ParseContentLength(
            header.WrittenSpan[..^HeaderTerminator.Length]);
        if (contentLength > maximumPayloadBytes)
            throw new InvalidDataException(
                $"DAP payload length {contentLength} exceeds {maximumPayloadBytes} bytes.");

        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < payload.Length)
        {
            var count = await stream.ReadAsync(
                payload.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException(
                    $"DAP stream ended after {offset} of {payload.Length} payload bytes.");
            offset += count;
        }

        return payload;
    }

    public async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > maximumPayloadBytes)
            throw new InvalidDataException(
                $"DAP payload length {payload.Length} exceeds {maximumPayloadBytes} bytes.");

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        foreach (var value in headerBytes)
        {
            if (value > 0x7f)
                throw new InvalidDataException("DAP headers must contain ASCII bytes only.");
        }

        var header = Encoding.ASCII.GetString(headerBytes);
        int? contentLength = null;
        foreach (var line in header.Split("\r\n", StringSplitOptions.None))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException($"Malformed DAP header line: '{line}'.");
            var name = line[..separator].Trim();
            var text = line[(separator + 1)..].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength is not null)
                throw new InvalidDataException("DAP message has duplicate Content-Length headers.");
            if (!int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                parsed < 0)
                throw new InvalidDataException(
                    $"Invalid DAP Content-Length value: '{text}'.");
            contentLength = parsed;
        }

        return contentLength ??
            throw new InvalidDataException("DAP message has no Content-Length header.");
    }
}
