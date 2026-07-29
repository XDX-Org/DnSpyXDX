using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DnSpyXDX.Debugging;

public sealed record DapResponse(
    int Sequence,
    int RequestSequence,
    string Command,
    bool Success,
    string? Message,
    JsonElement? Body);

public sealed record DapEvent(
    int Sequence,
    string Name,
    JsonElement? Body);

public sealed record DapReverseRequest(
    int Sequence,
    string Command,
    JsonElement? Arguments);

public sealed record DapReverseResponse(
    bool Success,
    JsonNode? Body = null,
    string? Message = null);

public delegate ValueTask<DapReverseResponse> DapReverseRequestCallback(
    DapReverseRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Correlates requests and responses over one DAP stream pair. Exactly one read loop owns input;
/// the framer serializes concurrent writes.
/// </summary>
public sealed class DapConnection : IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly DapMessageFramer framer;
    private readonly bool ownsStreams;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> pending = new();
    private readonly Task readLoop;
    private int nextSequence;
    private int disposeState;

    public DapConnection(
        Stream input,
        Stream output,
        DapMessageFramer? framer = null,
        bool ownsStreams = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("DAP input stream must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("DAP output stream must be writable.", nameof(output));
        this.input = input;
        this.output = output;
        this.framer = framer ?? new DapMessageFramer();
        this.ownsStreams = ownsStreams;
        readLoop = ReadLoopAsync();
    }

    public event Action<DapEvent>? EventReceived;
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Handles requests initiated by the adapter, such as <c>runInTerminal</c>. An absent handler
    /// produces a protocol-compliant unsuccessful response.
    /// </summary>
    public DapReverseRequestCallback? ReverseRequestHandler { get; set; }

    public Task Completion => readLoop;

    public async Task<DapResponse> SendRequestAsync(
        string command,
        JsonNode? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ThrowIfDisposed();

        var sequence = Interlocked.Increment(ref nextSequence);
        var completion = new TaskCompletionSource<DapResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(sequence, completion))
            throw new InvalidOperationException($"DAP sequence {sequence} is already pending.");

        var message = new JsonObject
        {
            ["seq"] = sequence,
            ["type"] = "request",
            ["command"] = command
        };
        if (arguments is not null) message["arguments"] = arguments.DeepClone();

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message);
            await framer.WriteAsync(output, payload, cancellationToken).ConfigureAwait(false);
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
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        shutdown.Cancel();
        if (ownsStreams)
        {
            await input.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(input, output))
                await output.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch
        {
            // Protocol failures are already exposed through Completion and Faulted. Disposal only
            // releases ownership and must not report the same failure a second time.
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(DapConnection)));
            shutdown.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var payload = await framer.ReadAsync(input, shutdown.Token).ConfigureAwait(false);
                if (payload is null) break;
                using var document = JsonDocument.Parse(payload);
                Dispatch(document.RootElement);
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
                    "DAP connection ended while requests were pending."));
        }
    }

    private void Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("DAP message root must be a JSON object.");
        var type = RequiredString(root, "type");
        switch (type)
        {
            case "response":
                DispatchResponse(root);
                break;
            case "event":
                DispatchEvent(root);
                break;
            case "request":
                var request = new DapReverseRequest(
                    RequiredInt32(root, "seq"),
                    RequiredString(root, "command"),
                    OptionalClone(root, "arguments"));
                _ = HandleReverseRequestAsync(request);
                break;
            default:
                throw new InvalidDataException($"Unknown DAP message type '{type}'.");
        }
    }

    private void DispatchResponse(JsonElement root)
    {
        var response = new DapResponse(
            RequiredInt32(root, "seq"),
            RequiredInt32(root, "request_seq"),
            RequiredString(root, "command"),
            RequiredBoolean(root, "success"),
            OptionalString(root, "message"),
            OptionalClone(root, "body"));
        if (pending.TryRemove(response.RequestSequence, out var completion))
            completion.TrySetResult(response);
    }

    private void DispatchEvent(JsonElement root)
    {
        var value = new DapEvent(
            RequiredInt32(root, "seq"),
            RequiredString(root, "event"),
            OptionalClone(root, "body"));
        InvokeSafely(EventReceived, value);
    }

    private async Task HandleReverseRequestAsync(DapReverseRequest request)
    {
        DapReverseResponse response;
        try
        {
            var handler = ReverseRequestHandler;
            response = handler is null
                ? new(false, Message: $"Reverse request '{request.Command}' is not supported.")
                : await handler(request, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            response = new(false, Message: exception.Message);
        }

        var message = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref nextSequence),
            ["type"] = "response",
            ["request_seq"] = request.Sequence,
            ["success"] = response.Success,
            ["command"] = request.Command
        };
        if (response.Message is not null) message["message"] = response.Message;
        if (response.Body is not null) message["body"] = response.Body.DeepClone();

        try
        {
            await framer.WriteAsync(
                output,
                JsonSerializer.SerializeToUtf8Bytes(message),
                shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            InvokeSafely(Faulted, exception);
        }
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"DAP message requires string property '{property}'.");
        return found.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static int RequiredInt32(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            !found.TryGetInt32(out var result))
            throw new InvalidDataException($"DAP message requires integer property '{property}'.");
        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"DAP message requires boolean property '{property}'.");
        return found.GetBoolean();
    }

    private static JsonElement? OptionalClone(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? found.Clone() : null;

    private void FailPending(Exception exception)
    {
        foreach (var request in pending)
        {
            if (pending.TryRemove(request.Key, out var completion))
                completion.TrySetException(exception);
        }
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
                // Consumer callbacks cannot terminate protocol dispatch.
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
}
