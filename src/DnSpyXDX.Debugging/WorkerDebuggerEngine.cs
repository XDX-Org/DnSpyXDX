using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DnSpyXDX.Application;
using DnSpyXDX.Debugging.Protocol;

namespace DnSpyXDX.Debugging;

public sealed record WorkerDebuggerOptions(
    string? WorkerPath = null,
    TimeSpan? ShutdownTimeout = null,
    string? NetCoreDbgPath = null,
    IReadOnlyList<string>? NetCoreDbgArguments = null,
    TimeSpan? NetCoreDbgStartupTimeout = null);

public sealed class WorkerDebuggerEngineProvider : IDebuggerEngineProvider
{
    public const string PathEnvironmentVariable = "DNSPYXDX_DEBUGGER_WORKER_PATH";
    private readonly WorkerDebuggerOptions options;
    private readonly string? workerPath;

    public WorkerDebuggerEngineProvider(
        DebugRuntimeKind runtime,
        WorkerDebuggerOptions? options = null)
    {
        if (runtime is not (DebugRuntimeKind.CoreClr or DebugRuntimeKind.Mono or
            DebugRuntimeKind.UnityMono))
            throw new ArgumentOutOfRangeException(nameof(runtime));
        Runtime = runtime;
        this.options = options ?? new();
        workerPath = ResolveWorker(this.options.WorkerPath);
    }

    public DebugRuntimeKind Runtime { get; }
    public bool IsAvailable => workerPath is not null;
    public string? UnavailableReason => IsAvailable
        ? null
        : $"DnSpyXDX debugger worker was not found. Set {PathEnvironmentVariable} or reinstall the debugger payload.";

    public ValueTask<IDebuggerEngine> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workerPath is null) throw new NotSupportedException(UnavailableReason);
        return ValueTask.FromResult<IDebuggerEngine>(
            new WorkerDebuggerEngine(Runtime, workerPath, options));
    }

    private static string? ResolveWorker(string? configured)
    {
        var name = OperatingSystem.IsWindows()
            ? "DnSpyXDX.Debugger.Worker.exe"
            : "DnSpyXDX.Debugger.Worker";
        var rid = $"{(OperatingSystem.IsWindows() ? "win" : "linux")}-" +
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        foreach (var candidate in new[]
        {
            configured,
            Environment.GetEnvironmentVariable(PathEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, "debuggers", "worker", rid, name),
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "DnSpyXDX.Debugger.Worker.dll")
        })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath)) return fullPath;
        }
        return null;
    }
}

internal sealed class WorkerDebuggerEngine(
    DebugRuntimeKind runtime,
    string workerPath,
    WorkerDebuggerOptions options) : IDebuggerEngine
{
    private static long nextGeneration;
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly long generation = Interlocked.Increment(ref nextGeneration);
    private DebuggerWorkerProcess? process;
    private long breakpointRevision;
    private int started;
    private int disposed;
    private int terminated;

    public event Action<DebugEngineEvent>? EventReceived;

    public async Task<DebugEngineStartResult> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Runtime != runtime)
            throw new ArgumentException(
                $"Worker provider for {runtime} cannot start {request.Runtime}.",
                nameof(request));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Debugger worker engine is already started.");
        process = await DebuggerWorkerProcess.StartAsync(
            workerPath,
            sessionId,
            generation,
            options.ShutdownTimeout,
            cancellationToken);
        process.Connection.EventReceived += OnWorkerEvent;
        process.Faulted += OnWorkerFaulted;
        var response = await process.Connection.SendRequestAsync(
            DebuggerWorkerCommands.Start,
            Body(DebuggerWorkerStartRequest.From(request) with
            {
                Backend = new(
                    options.NetCoreDbgPath,
                    options.NetCoreDbgArguments,
                    ToMilliseconds(options.ShutdownTimeout),
                    ToMilliseconds(options.NetCoreDbgStartupTimeout))
            }),
            cancellationToken: cancellationToken);
        return Result<DebugEngineStartResult>(response);
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref terminated, 1) != 0) return;
        _ = Result<object?>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.Terminate,
            cancellationToken: cancellationToken));
        await GetProcess().StopAsync(cancellationToken);
    }

    public Task ContinueAsync(CancellationToken cancellationToken) =>
        CommandAsync(DebuggerWorkerCommands.Continue, null, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) =>
        CommandAsync(DebuggerWorkerCommands.Pause, null, cancellationToken);

    public Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken) =>
        CommandAsync(
            DebuggerWorkerCommands.Step,
            new DebuggerWorkerStepRequest(thread, kind),
            cancellationToken);

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        var revision = Interlocked.Increment(ref breakpointRevision);
        var response = await Connection.SendRequestAsync(
            DebuggerWorkerCommands.ReplaceBreakpoints,
            Body(new DebuggerWorkerBreakpointRequest(breakpoints)),
            revision,
            cancellationToken);
        if (response.BreakpointRevision != revision)
            throw new InvalidDataException("Debugger worker returned the wrong breakpoint revision.");
        return Result<IReadOnlyList<DebugBreakpointBinding>>(response);
    }

    public async Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        Result<IReadOnlyList<DebugThread>>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.Threads,
            cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken) =>
        Result<IReadOnlyList<DebugStackFrame>>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.StackTrace,
            Body(new DebuggerWorkerThreadRequest(thread)),
            cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken) =>
        Result<IReadOnlyList<DebugScope>>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.Scopes,
            Body(new DebuggerWorkerFrameRequest(frame)),
            cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken) =>
        Result<IReadOnlyList<DebugVariable>>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.Variables,
            Body(new DebuggerWorkerVariablesRequest(reference)),
            cancellationToken: cancellationToken));

    public async Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken) =>
        Result<DebugEvaluationResult>(await Connection.SendRequestAsync(
            DebuggerWorkerCommands.Evaluate,
            Body(new DebuggerWorkerEvaluateRequest(expression, frame)),
            cancellationToken: cancellationToken));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (process is not null)
        {
            process.Connection.EventReceived -= OnWorkerEvent;
            process.Faulted -= OnWorkerFaulted;
            await process.DisposeAsync();
            process = null;
        }
    }

    private async Task CommandAsync(
        string command,
        object? body,
        CancellationToken cancellationToken) =>
        _ = Result<object?>(await Connection.SendRequestAsync(
            command,
            Body(body),
            cancellationToken: cancellationToken));

    private void OnWorkerEvent(DebuggerWorkerMessage message)
    {
        if (message.Name == DebuggerWorkerEvents.BreakpointsChanged &&
            message.BreakpointRevision != Volatile.Read(ref breakpointRevision))
            return;
        DebugEngineEvent value = message.Name switch
        {
            DebuggerWorkerEvents.Stopped =>
                new DebugEngineStopped(EventBody<DebugStopInfo>(message)),
            DebuggerWorkerEvents.Continued => new DebugEngineContinued(),
            DebuggerWorkerEvents.Exited => new DebugEngineExited(
                EventBody<DebuggerWorkerExitEvent>(message).ExitCode),
            DebuggerWorkerEvents.Faulted => new DebugEngineFaulted(
                EventBody<DebuggerWorkerFaultEvent>(message).Message),
            DebuggerWorkerEvents.Output => new DebugEngineOutput(
                EventBody<DebugOutputMessage>(message)),
            DebuggerWorkerEvents.BreakpointsChanged =>
                new DebugEngineBreakpointsChanged(
                    EventBody<IReadOnlyList<DebugBreakpointBinding>>(message)),
            _ => new DebugEngineFaulted(
                $"Debugger worker sent unknown event '{message.Name}'.")
        };
        Emit(value);
    }

    private void OnWorkerFaulted(string message) =>
        Emit(new DebugEngineFaulted(message));

    private void Emit(DebugEngineEvent value)
    {
        var handlers = EventReceived;
        if (handlers is null) return;
        foreach (Action<DebugEngineEvent> handler in handlers.GetInvocationList())
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

    private DebuggerWorkerClientConnection Connection =>
        GetProcess().Connection;

    private DebuggerWorkerProcess GetProcess() => process ??
        throw new InvalidOperationException("Debugger worker is not started.");

    private static JsonElement? Body(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToElement(value, DebuggerWorkerProtocol.SerializerOptions);

    private static T EventBody<T>(DebuggerWorkerMessage message) =>
        message.Body is { } body
            ? body.Deserialize<T>(DebuggerWorkerProtocol.SerializerOptions) ??
                throw new InvalidDataException(
                    $"Debugger worker event '{message.Name}' has an empty body.")
            : throw new InvalidDataException(
                $"Debugger worker event '{message.Name}' requires a body.");

    private static T Result<T>(DebuggerWorkerMessage response)
    {
        if (response.Success != true)
            throw new InvalidOperationException(
                $"Debugger worker command '{response.Name}' failed " +
                $"({response.Error?.Code ?? "unknown"}): " +
                (response.Error?.Message ?? "unknown worker error"));
        if (typeof(T) == typeof(object)) return default!;
        if (response.Body is not { } body) return default!;
        return body.Deserialize<T>(DebuggerWorkerProtocol.SerializerOptions) ??
            throw new InvalidDataException(
                $"Debugger worker command '{response.Name}' returned an empty body.");
    }

    private static int? ToMilliseconds(TimeSpan? value) => value is null
        ? null
        : checked((int)value.Value.TotalMilliseconds);
}

internal sealed class DebuggerWorkerProcess : IAsyncDisposable
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly Process process;
    private readonly Task stderrLoop;
    private readonly TimeSpan shutdownTimeout;
    private int stopping;
    private int disposed;

    private DebuggerWorkerProcess(
        Process process,
        Guid sessionId,
        long generation,
        TimeSpan timeout)
    {
        this.process = process;
        shutdownTimeout = timeout;
        Connection = new(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            sessionId,
            generation);
        Connection.Faulted += exception =>
            Faulted?.Invoke($"Debugger worker protocol failed: {exception.Message}");
        stderrLoop = ReadStandardErrorAsync();
        _ = ObserveExitAsync();
    }

    public DebuggerWorkerClientConnection Connection { get; }
    public event Action<string>? Faulted;

    public static Task<DebuggerWorkerProcess> StartAsync(
        string workerPath,
        Guid sessionId,
        long generation,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shutdownTimeout = timeout ?? DefaultShutdownTimeout;
        if (shutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var info = new ProcessStartInfo
        {
            FileName = Path.GetExtension(workerPath).Equals(
                ".dll",
                StringComparison.OrdinalIgnoreCase)
                ? Environment.ProcessPath is { } current &&
                    Path.GetFileNameWithoutExtension(current).Equals(
                        "dotnet",
                        StringComparison.OrdinalIgnoreCase)
                    ? current
                    : "dotnet"
                : workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (Path.GetExtension(workerPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(workerPath);
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Debugger worker did not start.");
            return Task.FromResult(new DebuggerWorkerProcess(
                process,
                sessionId,
                generation,
                shutdownTimeout));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        if (!process.HasExited)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(shutdownTimeout);
            try
            {
                _ = await Connection.SendRequestAsync(
                    DebuggerWorkerCommands.Shutdown,
                    cancellationToken: timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                EndOfStreamException or IOException or ObjectDisposedException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        await Connection.DisposeAsync();
        await stderrLoop;
        process.Dispose();
    }

    private async Task ReadStandardErrorAsync()
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            Faulted?.Invoke($"Debugger worker: {line}");
    }

    private async Task ObserveExitAsync()
    {
        await process.WaitForExitAsync();
        if (Volatile.Read(ref stopping) == 0)
            Faulted?.Invoke(
                $"Debugger worker exited unexpectedly with code {process.ExitCode}.");
    }
}
