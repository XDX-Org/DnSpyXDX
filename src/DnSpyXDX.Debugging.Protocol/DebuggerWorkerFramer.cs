using System.Buffers.Binary;

namespace DnSpyXDX.Debugging.Protocol;

public sealed class DebuggerWorkerFramer(int maximumPayloadBytes = DebuggerWorkerProtocol.DefaultMaximumPayloadBytes)
{
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public int MaximumPayloadBytes { get; } = maximumPayloadBytes > 0
        ? maximumPayloadBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

    public async ValueTask<DebuggerWorkerMessage?> ReadAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new byte[sizeof(int)];
        var headerBytes = await ReadAtMostAsync(input, header, cancellationToken)
            .ConfigureAwait(false);
        if (headerBytes == 0) return null;
        if (headerBytes != header.Length)
            throw new EndOfStreamException("Debugger worker message header ended early.");

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumPayloadBytes)
            throw new InvalidDataException(
                $"Debugger worker payload length {length} is outside the allowed range.");
        var payload = new byte[length];
        if (await ReadAtMostAsync(input, payload, cancellationToken).ConfigureAwait(false) != length)
            throw new EndOfStreamException("Debugger worker message payload ended early.");
        return DebuggerWorkerProtocol.Deserialize(payload);
    }

    public async ValueTask WriteAsync(
        Stream output,
        DebuggerWorkerMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var payload = DebuggerWorkerProtocol.Serialize(message);
        if (payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Debugger worker message exceeds the configured payload limit.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
