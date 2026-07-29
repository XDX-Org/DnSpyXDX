using System.Text.Json;
using System.Threading.Channels;
using DnSpyXDX.Application;
using DnSpyXDX.Debugging;
using DnSpyXDX.Debugging.Protocol;

return await new DebuggerWorkerServer(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    Console.Error).RunAsync();

internal sealed class DebuggerWorkerServer(
    Stream input,
    Stream output,
    TextWriter error)
{
    private readonly DebuggerWorkerFramer framer = new();
    private readonly Channel<(string Name, object? Body, long? Revision)> events =
        Channel.CreateUnbounded<(string, object?, long?)>(new()
        {
            SingleReader = true,
            SingleWriter = false
        });
    private IDebuggerEngine? engine;
    private Guid sessionId;
    private long generation;
    private long sequence;
    private long breakpointRevision;
    private bool shutdown;

    public async Task<int> RunAsync()
    {
        var eventPump = PumpEventsAsync();
        try
        {
            while (!shutdown && await framer.ReadAsync(input) is { } request)
            {
                if (request.Kind != DebuggerWorkerMessageKind.Request)
                    throw new InvalidDataException("Debugger worker accepts request messages only.");
                if (sessionId == Guid.Empty)
                {
                    sessionId = request.SessionId;
                    generation = request.Generation;
                }
                else if (request.SessionId != sessionId || request.Generation != generation)
                {
                    throw new InvalidDataException(
                        "Debugger worker request belongs to another session or generation.");
                }
                await HandleAsync(request);
            }
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.ToString());
            return 1;
        }
        finally
        {
            events.Writer.TryComplete();
            await eventPump;
            if (engine is not null)
                await engine.DisposeAsync();
        }
    }

    private async Task HandleAsync(DebuggerWorkerMessage request)
    {
        try
        {
            object? result = request.Name switch
            {
                DebuggerWorkerCommands.Start => await StartAsync(
                    Body<DebuggerWorkerStartRequest>(request)),
                DebuggerWorkerCommands.Terminate => await ExecuteAsync(
                    value => value.TerminateAsync(CancellationToken.None)),
                DebuggerWorkerCommands.Continue => await ExecuteAsync(
                    value => value.ContinueAsync(CancellationToken.None)),
                DebuggerWorkerCommands.Pause => await ExecuteAsync(
                    value => value.PauseAsync(CancellationToken.None)),
                DebuggerWorkerCommands.Step => await StepAsync(
                    Body<DebuggerWorkerStepRequest>(request)),
                DebuggerWorkerCommands.ReplaceBreakpoints => await ReplaceBreakpointsAsync(
                    request,
                    Body<DebuggerWorkerBreakpointRequest>(request)),
                DebuggerWorkerCommands.Threads => await GetEngine().GetThreadsAsync(
                    CancellationToken.None),
                DebuggerWorkerCommands.StackTrace => await StackTraceAsync(
                    Body<DebuggerWorkerThreadRequest>(request)),
                DebuggerWorkerCommands.Scopes => await ScopesAsync(
                    Body<DebuggerWorkerFrameRequest>(request)),
                DebuggerWorkerCommands.Variables => await VariablesAsync(
                    Body<DebuggerWorkerVariablesRequest>(request)),
                DebuggerWorkerCommands.Evaluate => await EvaluateAsync(
                    Body<DebuggerWorkerEvaluateRequest>(request)),
                DebuggerWorkerCommands.Shutdown => Shutdown(),
                _ => throw new NotSupportedException(
                    $"Debugger worker command '{request.Name}' is unsupported.")
            };
            await RespondAsync(request, true, result);
        }
        catch (Exception exception)
        {
            await RespondAsync(
                request,
                false,
                error: new(exception.GetType().Name, exception.Message));
        }
    }

    private async Task<DebugEngineStartResult> StartAsync(DebuggerWorkerStartRequest command)
    {
        if (engine is not null)
            throw new InvalidOperationException("Debugger worker engine is already started.");
        IDebuggerEngineProvider provider = command.Runtime switch
        {
            DebugRuntimeKind.CoreClr => new NetCoreDbgEngineProvider(
                new CoreClrDebuggerOptions(
                    command.Backend?.NetCoreDbgPath,
                    command.Backend?.NetCoreDbgArguments,
                    Milliseconds(command.Backend?.ShutdownTimeoutMilliseconds),
                    Milliseconds(command.Backend?.StartupTimeoutMilliseconds))),
            DebugRuntimeKind.Mono => new MonoSoftDebuggerEngineProvider(),
            DebugRuntimeKind.UnityMono => new UnityMonoDebuggerEngineProvider(),
            _ => throw new NotSupportedException(
                $"Debugger worker runtime '{command.Runtime}' is unsupported.")
        };
        engine = await provider.CreateAsync(CancellationToken.None);
        engine.EventReceived += OnEngineEvent;
        return await engine.StartAsync(command.GetRequest(), CancellationToken.None);
    }

    private async Task<object?> ExecuteAsync(Func<IDebuggerEngine, Task> action)
    {
        await action(GetEngine());
        return null;
    }

    private async Task<object?> StepAsync(DebuggerWorkerStepRequest request)
    {
        await GetEngine().StepAsync(request.Thread, request.Kind, CancellationToken.None);
        return null;
    }

    private async Task<IReadOnlyList<DebugBreakpointBinding>> ReplaceBreakpointsAsync(
        DebuggerWorkerMessage message,
        DebuggerWorkerBreakpointRequest request)
    {
        var revision = message.BreakpointRevision ??
            throw new InvalidDataException("Breakpoint replacement requires a revision.");
        if (revision <= breakpointRevision)
            throw new InvalidDataException(
                $"Breakpoint revision {revision} is not newer than {breakpointRevision}.");
        breakpointRevision = revision;
        return await GetEngine().SetBreakpointsAsync(
            request.Breakpoints,
            CancellationToken.None);
    }

    private Task<IReadOnlyList<DebugStackFrame>> StackTraceAsync(
        DebuggerWorkerThreadRequest request) =>
        GetEngine().GetStackTraceAsync(request.Thread, CancellationToken.None);

    private Task<IReadOnlyList<DebugScope>> ScopesAsync(DebuggerWorkerFrameRequest request) =>
        GetEngine().GetScopesAsync(request.Frame, CancellationToken.None);

    private Task<IReadOnlyList<DebugVariable>> VariablesAsync(
        DebuggerWorkerVariablesRequest request) =>
        GetEngine().GetVariablesAsync(request.Reference, CancellationToken.None);

    private Task<DebugEvaluationResult> EvaluateAsync(
        DebuggerWorkerEvaluateRequest request) =>
        GetEngine().EvaluateAsync(request.Expression, request.Frame, CancellationToken.None);

    private object? Shutdown()
    {
        shutdown = true;
        return null;
    }

    private void OnEngineEvent(DebugEngineEvent value)
    {
        var translated = value switch
        {
            DebugEngineStopped stopped => (DebuggerWorkerEvents.Stopped, (object?)stopped.Stop, (long?)null),
            DebugEngineContinued => (DebuggerWorkerEvents.Continued, null, null),
            DebugEngineExited exited => (
                DebuggerWorkerEvents.Exited,
                (object?)new DebuggerWorkerExitEvent(exited.ExitCode),
                null),
            DebugEngineFaulted faulted => (
                DebuggerWorkerEvents.Faulted,
                (object?)new DebuggerWorkerFaultEvent(faulted.Message),
                null),
            DebugEngineOutput outputEvent => (
                DebuggerWorkerEvents.Output,
                (object?)outputEvent.Output,
                null),
            DebugEngineBreakpointsChanged changed => (
                DebuggerWorkerEvents.BreakpointsChanged,
                (object?)changed.Breakpoints,
                (long?)breakpointRevision),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        events.Writer.TryWrite(translated);
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var value in events.Reader.ReadAllAsync())
        {
            var message = new DebuggerWorkerMessage(
                DebuggerWorkerProtocol.Version,
                DebuggerWorkerMessageKind.Event,
                sessionId,
                generation,
                Interlocked.Increment(ref sequence),
                value.Name,
                BreakpointRevision: value.Revision,
                Body: ToBody(value.Body));
            await framer.WriteAsync(output, message);
        }
    }

    private Task RespondAsync(
        DebuggerWorkerMessage request,
        bool success,
        object? body = null,
        DebuggerWorkerError? error = null) =>
        framer.WriteAsync(
            output,
            new DebuggerWorkerMessage(
                DebuggerWorkerProtocol.Version,
                DebuggerWorkerMessageKind.Response,
                sessionId,
                generation,
                Interlocked.Increment(ref sequence),
                request.Name,
                ReplyTo: request.Sequence,
                BreakpointRevision: request.BreakpointRevision,
                Success: success,
                Body: ToBody(body),
                Error: error)).AsTask();

    private static JsonElement? ToBody(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToElement(value, DebuggerWorkerProtocol.SerializerOptions);

    private static T Body<T>(DebuggerWorkerMessage request) =>
        request.Body is { } body
            ? body.Deserialize<T>(DebuggerWorkerProtocol.SerializerOptions) ??
                throw new InvalidDataException(
                    $"Debugger worker command '{request.Name}' has an empty body.")
            : throw new InvalidDataException(
                $"Debugger worker command '{request.Name}' requires a body.");

    private IDebuggerEngine GetEngine() => engine ??
        throw new InvalidOperationException("Debugger worker engine is not started.");

    private static TimeSpan? Milliseconds(int? value) => value is null
        ? null
        : TimeSpan.FromMilliseconds(value.Value);
}
