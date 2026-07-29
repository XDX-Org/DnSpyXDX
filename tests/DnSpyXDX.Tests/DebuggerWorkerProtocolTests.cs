using System.Text.Json;
using DnSpyXDX.Debugging.Protocol;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerWorkerProtocolTests
{
    [Fact]
    public void MessageRoundTripsRequiredIdentity()
    {
        var session = Guid.NewGuid();
        var message = new DebuggerWorkerMessage(
            DebuggerWorkerProtocol.Version,
            DebuggerWorkerMessageKind.Request,
            session,
            Generation: 3,
            Sequence: 7,
            Name: "replaceBreakpoints",
            BreakpointRevision: 12,
            Body: JsonSerializer.SerializeToElement(new { count = 2 }));

        var result = DebuggerWorkerProtocol.Deserialize(
            DebuggerWorkerProtocol.Serialize(message));

        Assert.Equal(session, result.SessionId);
        Assert.Equal(3, result.Generation);
        Assert.Equal(7, result.Sequence);
        Assert.Equal(12, result.BreakpointRevision);
        Assert.Equal(2, result.Body!.Value.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void MessageRejectsInvalidIdentity(int version, long generation, long sequence)
    {
        var message = new DebuggerWorkerMessage(
            version,
            DebuggerWorkerMessageKind.Event,
            Guid.NewGuid(),
            generation,
            sequence,
            "stopped");

        Assert.Throws<InvalidDataException>(() => DebuggerWorkerProtocol.Serialize(message));
    }

    [Fact]
    public async Task FramerRoundTripsMessage()
    {
        var message = new DebuggerWorkerMessage(
            DebuggerWorkerProtocol.Version,
            DebuggerWorkerMessageKind.Event,
            Guid.NewGuid(),
            Generation: 1,
            Sequence: 1,
            Name: "initialized");
        var stream = new MemoryStream();
        var framer = new DebuggerWorkerFramer();

        await framer.WriteAsync(stream, message);
        stream.Position = 0;
        var result = await framer.ReadAsync(stream);

        Assert.Equal(message, result);
    }

    [Fact]
    public async Task FramerRejectsOversizedPayloadBeforeAllocation()
    {
        var stream = new MemoryStream([0, 0, 4, 1]);
        var framer = new DebuggerWorkerFramer(1024);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await framer.ReadAsync(stream));
    }

    [Fact]
    public async Task ClientCorrelatesResponse()
    {
        var session = Guid.NewGuid();
        var response = new DebuggerWorkerMessage(
            DebuggerWorkerProtocol.Version,
            DebuggerWorkerMessageKind.Response,
            session,
            Generation: 2,
            Sequence: 9,
            Name: "initialize",
            ReplyTo: 1,
            Success: true);
        var input = new MemoryStream();
        await new DebuggerWorkerFramer().WriteAsync(input, response);
        input.Position = 0;
        var output = new MemoryStream();
        await using var connection = new DebuggerWorkerClientConnection(
            input,
            output,
            session,
            generation: 2);

        var result = await connection.SendRequestAsync("initialize");

        Assert.True(result.Success);
        Assert.Equal(1, result.ReplyTo);
    }

    [Fact]
    public async Task ClientRejectsAnotherGeneration()
    {
        var session = Guid.NewGuid();
        var response = new DebuggerWorkerMessage(
            DebuggerWorkerProtocol.Version,
            DebuggerWorkerMessageKind.Response,
            session,
            Generation: 1,
            Sequence: 2,
            Name: "initialize",
            ReplyTo: 1,
            Success: true);
        var input = new MemoryStream();
        await new DebuggerWorkerFramer().WriteAsync(input, response);
        input.Position = 0;
        await using var connection = new DebuggerWorkerClientConnection(
            input,
            new MemoryStream(),
            session,
            generation: 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            connection.SendRequestAsync("initialize"));
    }
}
