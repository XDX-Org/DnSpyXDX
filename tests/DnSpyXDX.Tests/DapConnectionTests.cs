using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DapConnectionTests
{
    [Fact]
    public async Task Correlates_out_of_order_responses()
    {
        var (clientStream, adapterStream) = ChannelDuplexStream.CreatePair();
        await using var client = new DapConnection(clientStream, clientStream, ownsStreams: true);
        await using var adapter = adapterStream;
        var framer = new DapMessageFramer();

        var firstTask = client.SendRequestAsync("first");
        var secondTask = client.SendRequestAsync("second");
        var firstRequest = Request(await framer.ReadAsync(adapter));
        var secondRequest = Request(await framer.ReadAsync(adapter));

        await WriteResponseAsync(framer, adapter, secondRequest);
        await WriteResponseAsync(framer, adapter, firstRequest);

        Assert.Equal("first", (await firstTask).Command);
        Assert.Equal("second", (await secondTask).Command);
    }

    [Fact]
    public async Task Dispatches_events_and_clones_body()
    {
        var (clientStream, adapterStream) = ChannelDuplexStream.CreatePair();
        await using var client = new DapConnection(clientStream, clientStream, ownsStreams: true);
        await using var adapter = adapterStream;
        var received = new TaskCompletionSource<DapEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += received.SetResult;
        var framer = new DapMessageFramer();

        await framer.WriteAsync(
            adapter,
            Encoding.UTF8.GetBytes(
                """{"seq":9,"type":"event","event":"stopped","body":{"reason":"breakpoint"}}"""));

        var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("stopped", value.Name);
        Assert.Equal("breakpoint", value.Body!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Handles_adapter_reverse_request()
    {
        var (clientStream, adapterStream) = ChannelDuplexStream.CreatePair();
        await using var client = new DapConnection(clientStream, clientStream, ownsStreams: true);
        await using var adapter = adapterStream;
        client.ReverseRequestHandler = (request, _) =>
            ValueTask.FromResult(new DapReverseResponse(
                true,
                new JsonObject { ["handled"] = request.Command }));
        var framer = new DapMessageFramer();

        await framer.WriteAsync(
            adapter,
            Encoding.UTF8.GetBytes(
                """{"seq":7,"type":"request","command":"runInTerminal","arguments":{}}"""));
        using var response = JsonDocument.Parse((await framer.ReadAsync(adapter))!);

        Assert.Equal("response", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, response.RootElement.GetProperty("request_seq").GetInt32());
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "runInTerminal",
            response.RootElement.GetProperty("body").GetProperty("handled").GetString());
    }

    [Fact]
    public async Task Cancelling_request_does_not_break_connection()
    {
        var (clientStream, adapterStream) = ChannelDuplexStream.CreatePair();
        await using var client = new DapConnection(clientStream, clientStream, ownsStreams: true);
        await using var adapter = adapterStream;
        var framer = new DapMessageFramer();
        using var cancellation = new CancellationTokenSource();

        var cancelledTask = client.SendRequestAsync(
            "slow",
            cancellationToken: cancellation.Token);
        var cancelledRequest = Request(await framer.ReadAsync(adapter));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
        await WriteResponseAsync(framer, adapter, cancelledRequest);

        var healthyTask = client.SendRequestAsync("healthy");
        var healthyRequest = Request(await framer.ReadAsync(adapter));
        await WriteResponseAsync(framer, adapter, healthyRequest);

        Assert.True((await healthyTask).Success);
    }

    [Fact]
    public async Task Protocol_failure_faults_pending_request()
    {
        var (clientStream, adapterStream) = ChannelDuplexStream.CreatePair();
        await using var client = new DapConnection(clientStream, clientStream, ownsStreams: true);
        await using var adapter = adapterStream;
        var framer = new DapMessageFramer();

        var pending = client.SendRequestAsync("waiting");
        _ = await framer.ReadAsync(adapter);
        await framer.WriteAsync(adapter, Encoding.UTF8.GetBytes("""{"type":"unknown"}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Completion);
    }

    private static JsonElement Request(byte[]? payload)
    {
        using var document = JsonDocument.Parse(payload!);
        return document.RootElement.Clone();
    }

    private static ValueTask WriteResponseAsync(
        DapMessageFramer framer,
        Stream stream,
        JsonElement request)
    {
        var sequence = request.GetProperty("seq").GetInt32();
        var command = request.GetProperty("command").GetString();
        var response = new JsonObject
        {
            ["seq"] = sequence + 100,
            ["type"] = "response",
            ["request_seq"] = sequence,
            ["success"] = true,
            ["command"] = command,
            ["body"] = new JsonObject()
        };
        return framer.WriteAsync(
            stream,
            JsonSerializer.SerializeToUtf8Bytes(response));
    }

    private sealed class ChannelDuplexStream(
        ChannelReader<byte[]> incoming,
        ChannelWriter<byte[]> outgoing) : Stream
    {
        private byte[]? current;
        private int currentOffset;
        private int disposed;

        public static (Stream First, Stream Second) CreatePair()
        {
            var firstToSecond = Channel.CreateUnbounded<byte[]>();
            var secondToFirst = Channel.CreateUnbounded<byte[]>();
            return (
                new ChannelDuplexStream(secondToFirst.Reader, firstToSecond.Writer),
                new ChannelDuplexStream(firstToSecond.Reader, secondToFirst.Writer));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (current is null || currentOffset == current.Length)
            {
                if (!await incoming.WaitToReadAsync(cancellationToken))
                    return 0;
                if (!incoming.TryRead(out current)) continue;
                currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, current.Length - currentOffset);
            current.AsMemory(currentOffset, count).CopyTo(buffer);
            currentOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);
            return outgoing.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                outgoing.TryComplete();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
