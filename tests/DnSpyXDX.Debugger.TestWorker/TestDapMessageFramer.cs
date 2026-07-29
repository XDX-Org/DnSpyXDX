using System.Globalization;
using System.Text;

internal sealed class TestDapMessageFramer
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public async ValueTask<byte[]?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new List<byte>();
        var next = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(next, cancellationToken);
            if (count == 0)
                return header.Count == 0
                    ? null
                    : throw new EndOfStreamException();
            header.Add(next[0]);
            if (header.Count >= HeaderTerminator.Length &&
                header.TakeLast(HeaderTerminator.Length)
                    .SequenceEqual(HeaderTerminator))
                break;
        }

        var headerText = Encoding.ASCII.GetString(
            [.. header.Take(header.Count - HeaderTerminator.Length)]);
        var contentLength = headerText
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts =>
                parts.Length == 2 &&
                parts[0].Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(
                parts[1].Trim(),
                CultureInfo.InvariantCulture))
            .Single();
        var payload = new byte[contentLength];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {payload.Length}\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
