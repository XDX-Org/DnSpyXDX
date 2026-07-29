using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DnSpyXDX.Application;
using Mono.Debugger.Soft;
using MonoStackFrame = Mono.Debugger.Soft.StackFrame;

namespace DnSpyXDX.Debugging;

public sealed record MonoSoftDebuggerOptions(
    TimeSpan? ConnectionTimeout = null);

public sealed class MonoSoftDebuggerEngineProvider : IDebuggerEngineProvider
{
    private readonly MonoSoftDebuggerOptions options;
    private readonly IMonoSoftDebuggerSessionFactory sessions;

    public MonoSoftDebuggerEngineProvider() : this(
        new MonoSoftDebuggerOptions(),
        new MonoSoftDebuggerSessionFactory())
    {
    }

    public MonoSoftDebuggerEngineProvider(MonoSoftDebuggerOptions options) : this(
        options,
        new MonoSoftDebuggerSessionFactory())
    {
    }

    internal MonoSoftDebuggerEngineProvider(
        MonoSoftDebuggerOptions options,
        IMonoSoftDebuggerSessionFactory sessions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessions);
        this.options = options;
        this.sessions = sessions;
    }

    public DebugRuntimeKind Runtime => DebugRuntimeKind.Mono;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public ValueTask<IDebuggerEngine> CreateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IDebuggerEngine>(
            new MonoSoftDebuggerEngine(options, sessions));
    }
}

internal sealed record MonoSoftDebuggerSessionStart(
    bool IsPaused,
    DebugStopInfo? InitialStop,
    IReadOnlyList<DebugBreakpointBinding> Breakpoints);

internal interface IMonoSoftDebuggerSessionFactory
{
    Task<IMonoSoftDebuggerSession> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IMonoSoftDebuggerSession : IAsyncDisposable
{
    event Action<DebugEngineEvent>? EventReceived;

    Task<MonoSoftDebuggerSessionStart> StartAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken);
    Task DetachAsync(CancellationToken cancellationToken);
    Task ContinueAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken);
}

internal sealed class MonoSoftDebuggerEngine(
    MonoSoftDebuggerOptions options,
    IMonoSoftDebuggerSessionFactory sessions) : IDebuggerEngine
{
    private static readonly TimeSpan DefaultConnectionTimeout =
        TimeSpan.FromSeconds(10);
    private IMonoSoftDebuggerSession? session;
    private int started;
    private int disposed;

    public event Action<DebugEngineEvent>? EventReceived;

    public async Task<DebugEngineStartResult> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Runtime != DebugRuntimeKind.Mono)
            throw new ArgumentException(
                $"Mono soft debugger cannot start runtime {request.Runtime}.",
                nameof(request));
        if (request is not DebugAttachRequest attach)
            throw new NotSupportedException(
                "Direct Mono debugging currently supports attach by host and port.");
        if (string.IsNullOrWhiteSpace(attach.Host))
            throw new ArgumentException(
                "Mono attach requires a host.",
                nameof(request));
        if (attach.Port is not (> 0 and <= 65_535))
            throw new ArgumentException(
                "Mono attach requires a port from 1 through 65535.",
                nameof(request));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException(
                "Mono debugger engine has already started.");
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);

        var timeout = options.ConnectionTimeout ?? DefaultConnectionTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Mono debugger connection timeout must be positive.");

        IMonoSoftDebuggerSession? connected = null;
        try
        {
            connected = await sessions.ConnectAsync(
                attach.Host.Trim(),
                attach.Port.Value,
                timeout,
                cancellationToken).ConfigureAwait(false);
            connected.EventReceived += ForwardEvent;
            session = connected;
            var result = await connected.StartAsync(
                request.InitialBreakpoints ?? [],
                cancellationToken).ConfigureAwait(false);
            if (result.Breakpoints.Count > 0)
                Emit(new DebugEngineBreakpointsChanged(result.Breakpoints));
            return new DebugEngineStartResult(
                attach.ProcessId,
                MonoSoftDebuggerCapabilities.Value,
                result.IsPaused,
                result.InitialStop);
        }
        catch
        {
            if (connected is not null)
            {
                connected.EventReceived -= ForwardEvent;
                await connected.DisposeAsync().ConfigureAwait(false);
            }
            session = null;
            throw;
        }
    }

    public Task TerminateAsync(CancellationToken cancellationToken) =>
        GetSession().DetachAsync(cancellationToken);

    public Task DetachAsync(CancellationToken cancellationToken) =>
        GetSession().DetachAsync(cancellationToken);

    public Task ContinueAsync(CancellationToken cancellationToken) =>
        GetSession().ContinueAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) =>
        GetSession().PauseAsync(cancellationToken);

    public Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken) =>
        GetSession().StepAsync(thread, kind, cancellationToken);

    public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        return GetSession().SetBreakpointsAsync(
            breakpoints,
            cancellationToken);
    }

    public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        GetSession().GetThreadsAsync(cancellationToken);

    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken) =>
        GetSession().GetStackTraceAsync(thread, cancellationToken);

    public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken) =>
        GetSession().GetScopesAsync(frame, cancellationToken);

    public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken) =>
        GetSession().GetVariablesAsync(reference, cancellationToken);

    public Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Expression evaluation is not implemented for direct Mono attach.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var current = session;
        session = null;
        if (current is null) return;
        current.EventReceived -= ForwardEvent;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private IMonoSoftDebuggerSession GetSession()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        return session ?? throw new InvalidOperationException(
            "Mono debugger engine has not started.");
    }

    private void ForwardEvent(DebugEngineEvent value) => Emit(value);
    private void Emit(DebugEngineEvent value) => EventReceived?.Invoke(value);
}

internal static class MonoSoftDebuggerCapabilities
{
    public static DebuggerCapabilities Value { get; } = new(
        SupportsLaunch: false,
        SupportsAttach: true,
        SupportsFunctionBreakpoints: false,
        SupportsConditionalBreakpoints: false,
        SupportsHitConditions: false,
        SupportsExceptionBreakpoints: false,
        SupportsSetVariable: false,
        SupportsEvaluate: false,
        SupportsDecompiledCodeBreakpoints: true);
}

internal sealed class MonoSoftDebuggerSessionFactory
    : IMonoSoftDebuggerSessionFactory
{
    public async Task<IMonoSoftDebuggerSession> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                host,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Could not resolve Mono debugger host '{host}'.",
                exception);
        }

        Exception? lastError = null;
        foreach (var address in addresses.Where(value =>
                     value.AddressFamily is AddressFamily.InterNetwork or
                         AddressFamily.InterNetworkV6))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);
            using var cancellation = cancellationToken.Register(socket.Dispose);
            var connect = VirtualMachineManager.ConnectInternalAsync(
                socket,
                null,
                new IPEndPoint(address, port),
                null);
            try
            {
                var virtualMachine = await connect.WaitAsync(
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                return new MonoSoftDebuggerSession(virtualMachine);
            }
            catch (Exception exception) when (
                exception is SocketException or
                    TimeoutException or
                    ObjectDisposedException or
                    AggregateException or
                    IOException or
                    VMDisconnectedException or
                    NotSupportedException)
            {
                lastError = exception;
                socket.Dispose();
                try
                {
                    await connect.ConfigureAwait(false);
                }
                catch
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to Mono debugger agent at {host}:{port}. " +
            "Start Mono with --debug and " +
            "--debugger-agent=transport=dt_socket,server=y.",
            lastError);
    }
}

internal sealed class MonoSoftDebuggerSession(VirtualMachine virtualMachine)
    : IMonoSoftDebuggerSession
{
    private const int MaximumArrayVariables = 256;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly CancellationTokenSource eventPumpCancellation = new();
    private readonly Dictionary<Guid, BreakpointEventRequest> breakpointRequests = [];
    private readonly Dictionary<long, MonoStackFrame> frames = [];
    private readonly Dictionary<long, Func<IReadOnlyList<DebugVariable>>> variableProviders = [];
    private IReadOnlyList<DebugBreakpoint> requestedBreakpoints = [];
    private IReadOnlyList<DebugBreakpointBinding> breakpointBindings = [];
    private Task? eventPump;
    private long nextFrameId;
    private long nextVariableReference;
    private int started;
    private int detached;
    private int disposed;
    private bool paused;

    public event Action<DebugEngineEvent>? EventReceived;

    public async Task<MonoSoftDebuggerSessionStart> StartAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException(
                "Mono soft-debugger session has already started.");

        var bindings = await ExecuteAsync(() =>
        {
            virtualMachine.EnableEvents(
                [
                    EventType.AssemblyLoad,
                    EventType.AssemblyUnload,
                    EventType.ThreadStart,
                    EventType.ThreadDeath
                ],
                SuspendPolicy.None);
            requestedBreakpoints = [.. breakpoints];
            return RebindBreakpoints();
        }, cancellationToken).ConfigureAwait(false);

        eventPump = Task.Run(EventPumpAsync);
        Emit(new DebugEngineOutput(new(
            "console",
            $"Connected to Mono soft debugger protocol " +
            $"{virtualMachine.Version.MajorVersion}." +
            $"{virtualMachine.Version.MinorVersion}.")));
        return new MonoSoftDebuggerSessionStart(
            IsPaused: false,
            InitialStop: null,
            bindings);
    }

    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref detached, 1) != 0) return;
        eventPumpCancellation.Cancel();
        await ExecuteAsync(() =>
        {
            try
            {
                virtualMachine.Detach();
            }
            catch (VMDisconnectedException)
            {
            }
            catch
            {
                virtualMachine.ForceDisconnect();
            }
        }, cancellationToken).ConfigureAwait(false);
        if (eventPump is not null)
        {
            try
            {
                await eventPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task ContinueAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(() =>
        {
            ClearTransientState();
            virtualMachine.Resume();
            paused = false;
        }, cancellationToken).ConfigureAwait(false);
        Emit(new DebugEngineContinued());
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var stop = await ExecuteAsync(() =>
        {
            virtualMachine.Suspend();
            paused = true;
            ClearTransientState();
            var thread = virtualMachine.GetThreads().FirstOrDefault();
            return CreateStop(
                DebugStopReason.Pause,
                thread,
                "Paused by user.",
                allThreadsStopped: true);
        }, cancellationToken).ConfigureAwait(false);
        Emit(new DebugEngineStopped(stop));
    }

    public async Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(() =>
        {
            var mirror = FindThread(thread);
            var request = virtualMachine.CreateStepRequest(mirror);
            request.Depth = kind switch
            {
                DebugStepKind.Into => StepDepth.Into,
                DebugStepKind.Over => StepDepth.Over,
                DebugStepKind.Out => StepDepth.Out,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            request.Size = StepSize.Min;
            request.Count = 1;
            request.Enable();
            ClearTransientState();
            virtualMachine.Resume();
            paused = false;
        }, cancellationToken).ConfigureAwait(false);
        Emit(new DebugEngineContinued());
    }

    public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        return ExecuteAsync<IReadOnlyList<DebugBreakpointBinding>>(() =>
        {
            requestedBreakpoints = [.. breakpoints];
            return RebindBreakpoints();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<DebugThread>>(
            () => virtualMachine.GetThreads()
                .Select(value => new DebugThread(
                    new DebugThreadId(value.Id),
                    SafeThreadName(value),
                    paused))
                .ToArray(),
            cancellationToken);

    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<DebugStackFrame>>(() =>
        {
            EnsurePaused();
            frames.Clear();
            variableProviders.Clear();
            var result = new List<DebugStackFrame>();
            foreach (var frame in FindThread(thread).GetFrames())
            {
                var frameId = new DebugFrameId(++nextFrameId);
                frames.Add(frameId.Value, frame);
                result.Add(CreateStackFrame(frameId, thread, frame));
            }
            return result;
        }, cancellationToken);

    public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<DebugScope>>(() =>
        {
            EnsurePaused();
            if (!frames.TryGetValue(frame.Value, out var found))
                throw new ArgumentException(
                    $"Unknown Mono stack frame {frame.Value}.",
                    nameof(frame));

            var scopes = new List<DebugScope>();
            var arguments = ReadArguments(found);
            if (arguments.Count > 0)
                scopes.Add(new(
                    "Arguments",
                    StoreVariables(arguments)));
            var locals = ReadLocals(found);
            if (locals.Count > 0)
                scopes.Add(new(
                    "Locals",
                    StoreVariables(locals)));
            return scopes;
        }, cancellationToken);

    public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            EnsurePaused();
            if (reference.Value == 0)
                return (IReadOnlyList<DebugVariable>)[];
            if (!variableProviders.TryGetValue(reference.Value, out var provider))
                throw new ArgumentException(
                    $"Unknown Mono variable reference {reference.Value}.",
                    nameof(reference));
            return provider();
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await DetachAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            eventPumpCancellation.Dispose();
            commandGate.Dispose();
        }
    }

    private async Task EventPumpAsync()
    {
        var cancellationToken = eventPumpCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var eventSet = virtualMachine.GetNextEventSet(250);
                if (eventSet is null) continue;
                await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    HandleEventSet(eventSet);
                }
                finally
                {
                    commandGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (VMDisconnectedException) when (
            cancellationToken.IsCancellationRequested ||
            Volatile.Read(ref detached) != 0)
        {
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref detached) == 0)
                Emit(new DebugEngineFaulted(
                    $"Mono debugger connection failed: {exception.Message}"));
        }
    }

    private void HandleEventSet(EventSet eventSet)
    {
        var reloadBreakpoints = false;
        foreach (var value in eventSet.Events)
        {
            switch (value)
            {
                case VMStartEvent start:
                    paused = eventSet.SuspendPolicy != SuspendPolicy.None;
                    if (paused)
                    {
                        ClearTransientState();
                        Emit(new DebugEngineStopped(CreateStop(
                            DebugStopReason.Entry,
                            start.Thread,
                            "Mono runtime started.",
                            eventSet.SuspendPolicy == SuspendPolicy.All)));
                    }
                    break;
                case BreakpointEvent breakpoint:
                    paused = true;
                    ClearTransientState();
                    Emit(new DebugEngineStopped(CreateStop(
                        DebugStopReason.Breakpoint,
                        breakpoint.Thread,
                        "Breakpoint hit.",
                        eventSet.SuspendPolicy == SuspendPolicy.All)));
                    break;
                case StepEvent step:
                    paused = true;
                    ClearTransientState();
                    Emit(new DebugEngineStopped(CreateStop(
                        DebugStopReason.Step,
                        step.Thread,
                        "Step completed.",
                        eventSet.SuspendPolicy == SuspendPolicy.All)));
                    break;
                case ExceptionEvent exception:
                    paused = true;
                    ClearTransientState();
                    Emit(new DebugEngineStopped(CreateStop(
                        DebugStopReason.Exception,
                        exception.Thread,
                        SafeExceptionName(exception),
                        eventSet.SuspendPolicy == SuspendPolicy.All)));
                    break;
                case UserBreakEvent userBreak:
                    paused = true;
                    ClearTransientState();
                    Emit(new DebugEngineStopped(CreateStop(
                        DebugStopReason.Pause,
                        userBreak.Thread,
                        "Debugger.Break invoked.",
                        eventSet.SuspendPolicy == SuspendPolicy.All)));
                    break;
                case AssemblyLoadEvent:
                case AssemblyUnloadEvent:
                    reloadBreakpoints = true;
                    break;
                case VMDeathEvent death:
                    eventPumpCancellation.Cancel();
                    Emit(new DebugEngineExited(SafeExitCode(death)));
                    break;
                case VMDisconnectEvent:
                    eventPumpCancellation.Cancel();
                    if (Volatile.Read(ref detached) == 0)
                        Emit(new DebugEngineExited());
                    break;
                case CrashEvent crash:
                    eventPumpCancellation.Cancel();
                    Emit(new DebugEngineFaulted(
                        $"Mono runtime crashed (0x{crash.Hash:X}): {crash.Dump}"));
                    break;
            }
        }

        if (reloadBreakpoints)
        {
            var bindings = RebindBreakpoints();
            Emit(new DebugEngineBreakpointsChanged(bindings));
        }
    }

    private IReadOnlyList<DebugBreakpointBinding> RebindBreakpoints()
    {
        foreach (var request in breakpointRequests.Values)
        {
            try
            {
                request.Disable();
            }
            catch (Exception exception) when (
                exception is VMDisconnectedException or CommandException)
            {
            }
        }
        breakpointRequests.Clear();

        var assemblies = LoadedAssemblies();
        var bindings = new List<DebugBreakpointBinding>(
            requestedBreakpoints.Count);
        foreach (var breakpoint in requestedBreakpoints)
            bindings.Add(BindBreakpoint(breakpoint, assemblies));
        breakpointBindings = bindings;
        return breakpointBindings;
    }

    private DebugBreakpointBinding BindBreakpoint(
        DebugBreakpoint breakpoint,
        IReadOnlyDictionary<Guid, AssemblyMirror> assemblies)
    {
        if (!breakpoint.Enabled)
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: "Breakpoint is disabled.");
        if (!string.IsNullOrWhiteSpace(breakpoint.Condition) ||
            !string.IsNullOrWhiteSpace(breakpoint.HitCondition) ||
            !string.IsNullOrWhiteSpace(breakpoint.LogMessage))
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: "Conditional, hit-count, and log breakpoints are not " +
                    "implemented for direct Mono attach.");
        if (breakpoint.Location.Method.MetadataToken <= 0 ||
            breakpoint.Location.Method.MetadataToken >> 24 != 0x06 ||
            breakpoint.Location.ILOffset < 0)
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: "Breakpoint requires a MethodDef token and non-negative IL offset.");
        if (!assemblies.TryGetValue(
                breakpoint.Location.Method.ModuleMvid,
                out var assembly))
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: "Pending: module is not loaded.");
        if (!virtualMachine.Version.AtLeast(2, 47))
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: "The Mono debugger agent must support protocol 2.47 or newer " +
                    "for metadata-token breakpoints.");

        try
        {
            var method = assembly.GetMethod(
                unchecked((uint)breakpoint.Location.Method.MetadataToken));
            if (method is null)
                return new(
                    breakpoint.Id,
                    IsVerified: false,
                    Message: "Method token was not found in the loaded module.");
            var boundOffset = SelectBreakpointOffset(
                method,
                breakpoint.Location.ILOffset);
            var request = virtualMachine.CreateBreakpointRequest(
                method,
                boundOffset);
            request.Enable();
            breakpointRequests.Add(breakpoint.Id, request);
            return new(
                breakpoint.Id,
                IsVerified: true,
                new(
                    breakpoint.Location.Method,
                    checked((int)boundOffset)));
        }
        catch (Exception exception) when (
            exception is CommandException or
                AbsentInformationException or
                ArgumentException or
                NotSupportedException)
        {
            return new(
                breakpoint.Id,
                IsVerified: false,
                Message: $"Mono rejected breakpoint: {exception.Message}");
        }
    }

    private IReadOnlyDictionary<Guid, AssemblyMirror> LoadedAssemblies()
    {
        var result = new Dictionary<Guid, AssemblyMirror>();
        foreach (var assembly in virtualMachine.RootDomain.GetAssemblies())
        {
            try
            {
                var mvid = assembly.ManifestModule.ModuleVersionId;
                if (mvid != Guid.Empty)
                    result.TryAdd(mvid, assembly);
            }
            catch (Exception exception) when (
                exception is CommandException or VMDisconnectedException)
            {
            }
        }
        return result;
    }

    private static long SelectBreakpointOffset(
        MethodMirror method,
        int requestedOffset)
    {
        try
        {
            var offsets = method.Locations
                .Select(value => value.ILOffset)
                .Distinct()
                .ToArray();
            if (offsets.Length == 0 || offsets.Contains(requestedOffset))
                return requestedOffset;
            return offsets
                .OrderBy(value => Math.Abs((long)value - requestedOffset))
                .ThenBy(value => value)
                .First();
        }
        catch (AbsentInformationException)
        {
            return requestedOffset;
        }
    }

    private DebugStopInfo CreateStop(
        DebugStopReason reason,
        ThreadMirror? thread,
        string? description,
        bool allThreadsStopped)
    {
        thread ??= virtualMachine.GetThreads().FirstOrDefault();
        if (thread is null)
            return new(
                reason,
                new DebugThreadId(0),
                Description: description,
                AllThreadsStopped: allThreadsStopped);
        return new(
            reason,
            new DebugThreadId(thread.Id),
            TryGetLocation(thread),
            description,
            allThreadsStopped);
    }

    private static DebugCodeLocation? TryGetLocation(ThreadMirror thread)
    {
        try
        {
            var frame = thread.GetFrames().FirstOrDefault();
            return frame is null ? null : RuntimeLocation(frame);
        }
        catch (Exception exception) when (
            exception is AbsentInformationException or
                CommandException or
                InvalidStackFrameException)
        {
            return null;
        }
    }

    private static DebugCodeLocation? RuntimeLocation(MonoStackFrame frame)
    {
        var mvid = frame.Method.DeclaringType.Module.ModuleVersionId;
        var token = frame.Method.MetadataToken;
        var offset = frame.ILOffset;
        return mvid == Guid.Empty || token <= 0 || offset < 0
            ? null
            : new DebugCodeLocation(new(mvid, token), offset);
    }

    private DebugStackFrame CreateStackFrame(
        DebugFrameId frameId,
        DebugThreadId thread,
        MonoStackFrame frame)
    {
        var method = frame.Method;
        var sourcePath = NullIfWhiteSpace(Safe(() => frame.FileName));
        var lineNumber = Safe(() => frame.LineNumber);
        int? sourceLine = lineNumber > 0
            ? lineNumber
            : null;
        var columnNumber = Safe(() => frame.ColumnNumber);
        int? sourceColumn = columnNumber > 0
            ? columnNumber
            : null;
        var typeName = Safe(() => method.DeclaringType.FullName) ??
            "<unknown type>";
        return new(
            frameId,
            thread,
            $"{typeName}.{method.Name}",
            RuntimeLocation(frame),
            sourcePath,
            sourceLine,
            sourceColumn,
            Safe(() => method.DeclaringType.Assembly.GetName().Name),
            NullIfWhiteSpace(Safe(() => method.DeclaringType.Assembly.Location)));
    }

    private IReadOnlyList<DebugVariable> ReadArguments(MonoStackFrame frame)
    {
        var result = new List<DebugVariable>();
        try
        {
            if (!frame.Method.IsStatic)
            {
                result.Add(CreateVariable(
                    "this",
                    frame.GetThis(),
                    frame.Method.DeclaringType.FullName));
            }
            foreach (var parameter in frame.Method.GetParameters())
            {
                result.Add(CreateVariable(
                    NullIfWhiteSpace(parameter.Name) ??
                        $"arg{parameter.Position}",
                    frame.GetValue(parameter),
                    parameter.ParameterType.FullName));
            }
        }
        catch (Exception exception) when (
            exception is AbsentInformationException or
                CommandException or
                InvalidStackFrameException)
        {
        }
        return result;
    }

    private IReadOnlyList<DebugVariable> ReadLocals(MonoStackFrame frame)
    {
        try
        {
            var locals = frame.GetVisibleVariables().ToArray();
            var values = frame.GetValues(locals);
            return locals.Select((local, index) => CreateVariable(
                    NullIfWhiteSpace(local.Name) ?? $"V_{local.Index}",
                    values[index],
                    local.Type.FullName,
                    NullIfWhiteSpace(local.Name) is null
                        ? DebugVariableNameOrigin.Synthetic
                        : DebugVariableNameOrigin.Runtime,
                    local.Index))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is AbsentInformationException or
                CommandException or
                InvalidStackFrameException)
        {
            return [];
        }
    }

    private DebugVariable CreateVariable(
        string name,
        Value? value,
        string? typeHint = null,
        DebugVariableNameOrigin nameOrigin = DebugVariableNameOrigin.Runtime,
        int? slot = null)
    {
        switch (value)
        {
            case null:
                return new(
                    name,
                    "null",
                    typeHint,
                    new DebugVariableReference(0),
                    NameOrigin: nameOrigin,
                    Slot: slot);
            case PrimitiveValue primitive:
                return new(
                    name,
                    FormatPrimitive(primitive.Value),
                    typeHint ?? primitive.Value?.GetType().FullName,
                    new DebugVariableReference(0),
                    NameOrigin: nameOrigin,
                    Slot: slot);
            case StringMirror text:
                return new(
                    name,
                    text.Value,
                    typeHint ?? "string",
                    new DebugVariableReference(0),
                    NameOrigin: nameOrigin,
                    Slot: slot);
            case ArrayMirror array:
            {
                var length = array.Length;
                var reference = StoreVariables(() =>
                {
                    var count = Math.Min(length, MaximumArrayVariables);
                    return array.GetValues(0, count)
                        .Select((item, index) => CreateVariable(
                            $"[{index}]",
                            item,
                            nameOrigin: DebugVariableNameOrigin.Synthetic))
                        .ToArray();
                });
                return new(
                    name,
                    $"{Safe(() => array.Type.FullName) ?? typeHint ?? "array"}" +
                    $"[{length}]",
                    Safe(() => array.Type.FullName) ?? typeHint,
                    reference,
                    NameOrigin: nameOrigin,
                    Slot: slot);
            }
            case ObjectMirror instance:
            {
                var type = Safe(() => instance.Type.FullName) ?? typeHint;
                return new(
                    name,
                    $"{{{type ?? "object"}}}",
                    type,
                    new DebugVariableReference(0),
                    NameOrigin: nameOrigin,
                    Slot: slot);
            }
            default:
                return new(
                    name,
                    value.ToString() ?? string.Empty,
                    typeHint,
                    new DebugVariableReference(0),
                    NameOrigin: nameOrigin,
                    Slot: slot);
        }
    }

    private DebugVariableReference StoreVariables(
        IReadOnlyList<DebugVariable> variables) =>
        StoreVariables(() => variables);

    private DebugVariableReference StoreVariables(
        Func<IReadOnlyList<DebugVariable>> provider)
    {
        var reference = new DebugVariableReference(++nextVariableReference);
        variableProviders.Add(reference.Value, provider);
        return reference;
    }

    private ThreadMirror FindThread(DebugThreadId thread) =>
        virtualMachine.GetThreads().FirstOrDefault(
            value => value.Id == thread.Value) ??
        throw new ArgumentException(
            $"Unknown Mono thread {thread.Value}.",
            nameof(thread));

    private void ClearTransientState()
    {
        frames.Clear();
        variableProviders.Clear();
    }

    private void EnsurePaused()
    {
        if (!paused)
            throw new InvalidOperationException(
                "Mono target must be paused for this operation.");
    }

    private async Task ExecuteAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private static string SafeThreadName(ThreadMirror thread)
    {
        try
        {
            return NullIfWhiteSpace(thread.Name) ??
                $"Thread {thread.Id}";
        }
        catch (CommandException)
        {
            return $"Thread {thread.Id}";
        }
    }

    private static string SafeExceptionName(ExceptionEvent value) =>
        Safe(() => value.Exception.Type.FullName) is { } name
            ? $"Exception: {name}"
            : "Exception thrown.";

    private static int? SafeExitCode(VMDeathEvent value)
    {
        try
        {
            return value.ExitCode;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string FormatPrimitive(object? value) => value switch
    {
        null => "null",
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(
            null,
            CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static T? Safe<T>(Func<T> value)
    {
        try
        {
            return value();
        }
        catch (Exception exception) when (
            exception is CommandException or
                VMDisconnectedException or
                AbsentInformationException or
                InvalidStackFrameException)
        {
            return default;
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Emit(DebugEngineEvent value) => EventReceived?.Invoke(value);
}
