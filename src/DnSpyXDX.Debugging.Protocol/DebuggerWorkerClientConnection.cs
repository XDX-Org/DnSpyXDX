using System.Collections.Concurrent;
using System.Text.Json;

namespace DnSpyXDX.Debugging.Protocol;

public sealed class DebuggerWorkerClientConnection : IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly DebuggerWorkerFramer framer;
    private readonly bool ownsStreams;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DebuggerWorkerMessage>> pending = [];
    private readonly object readGate = new();
    private Task? readLoop;
    private long nextSequence;
    private int disposed;

    public DebuggerWorkerClientConnection(
        Stream input,
        Stream output,
        Guid sessionId,
        long generation,
        DebuggerWorkerFramer? framer = null,
        bool ownsStreams = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("Worker input must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("Worker output must be writable.", nameof(output));
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        this.input = input;
        this.output = output;
        this.framer = framer ?? new DebuggerWorkerFramer();
        this.ownsStreams = ownsStreams;
        SessionId = sessionId;
        Generation = generation;
    }

    public Guid SessionId { get; }
    public long Generation { get; }
    public Task Completion
    {
        get
        {
            lock (readGate) return readLoop ?? Task.CompletedTask;
        }
    }
    public event Action<DebuggerWorkerMessage>? EventReceived;
    public event Action<DebuggerWorkerMessage>? MessageSent;
    public event Action<DebuggerWorkerMessage>? MessageReceived;
    public event Action<Exception>? Faulted;

    public async Task<DebuggerWorkerMessage> SendRequestAsync(
        string name,
        JsonElement? body = null,
        long? breakpointRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var sequence = Interlocked.Increment(ref nextSequence);
        var completion = new TaskCompletionSource<DebuggerWorkerMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(sequence, completion))
            throw new InvalidOperationException($"Worker sequence {sequence} is already pending.");
        var request = new DebuggerWorkerMessage(
            DebuggerWorkerProtocol.Version,
            DebuggerWorkerMessageKind.Request,
            SessionId,
            Generation,
            sequence,
            name,
            BreakpointRevision: breakpointRevision,
            Body: body);
        EnsureReadLoop();
        try
        {
            await framer.WriteAsync(output, request, cancellationToken).ConfigureAwait(false);
            InvokeSafely(MessageSent, request);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pending.TryRemove(sequence, out _);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        if (ownsStreams)
        {
            await input.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(input, output))
                await output.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(DebuggerWorkerClientConnection)));
            shutdown.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var message = await framer.ReadAsync(input, shutdown.Token).ConfigureAwait(false);
                if (message is null) break;
                InvokeSafely(MessageReceived, message);
                if (message.SessionId != SessionId || message.Generation != Generation)
                    throw new InvalidDataException(
                        "Debugger worker message belongs to another session or generation.");
                switch (message.Kind)
                {
                    case DebuggerWorkerMessageKind.Response:
                        if (pending.TryRemove(message.ReplyTo!.Value, out var completion))
                            completion.TrySetResult(message);
                        break;
                    case DebuggerWorkerMessageKind.Event:
                        InvokeSafely(EventReceived, message);
                        break;
                    default:
                        throw new InvalidDataException(
                            "Debugger worker sent a request to a client connection.");
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            InvokeSafely(Faulted, exception);
            throw;
        }
        finally
        {
            if (!shutdown.IsCancellationRequested && !pending.IsEmpty)
                FailPending(new EndOfStreamException(
                    "Debugger worker connection ended while requests were pending."));
        }
    }

    private void EnsureReadLoop()
    {
        lock (readGate)
            readLoop ??= ReadLoopAsync();
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in pending.ToArray())
            if (pending.TryRemove(pair.Key, out var completion))
                completion.TrySetException(exception);
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
            }
        }
    }
}
