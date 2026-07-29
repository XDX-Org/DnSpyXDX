using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed class DebuggerService(IDebuggerEngineRegistry engines) : IDebuggerService
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly object stateGate = new();
    private IDebuggerEngine? engine;
    private Action<DebugEngineEvent>? engineHandler;
    private long engineGeneration;
    private bool disposed;
    private DebugSessionSnapshot snapshot = DebugSessionSnapshot.Initial;
    private IReadOnlyList<DebugBreakpointBinding> breakpoints = [];

    public DebugSessionSnapshot Snapshot
    {
        get
        {
            lock (stateGate) return snapshot;
        }
    }

    public IReadOnlyList<DebugBreakpointBinding> Breakpoints
    {
        get
        {
            lock (stateGate) return breakpoints;
        }
    }

    public event Action<DebugSessionSnapshot>? StateChanged;
    public event Action<IReadOnlyList<DebugBreakpointBinding>>? BreakpointsChanged;
    public event Action<DebugOutputMessage>? OutputReceived;

    public async Task StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStatus(
                nameof(StartAsync),
                DebugSessionStatus.Created,
                DebugSessionStatus.Terminated,
                DebugSessionStatus.Faulted);

            var generation = Interlocked.Increment(ref engineGeneration);
            await DisposeEngineAsync().ConfigureAwait(false);
            SetSnapshot(new(
                Guid.NewGuid(),
                request.Runtime,
                DebugSessionStatus.Starting,
                null,
                DebuggerCapabilitySets.None,
                null,
                null));

            IDebuggerEngine? nextEngine = null;
            try
            {
                nextEngine = await engines.CreateAsync(request.Runtime, cancellationToken)
                    .ConfigureAwait(false);
                engineHandler = value => OnEngineEvent(generation, value);
                nextEngine.EventReceived += engineHandler;
                engine = nextEngine;

                var result = await nextEngine.StartAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                var currentSnapshot = Snapshot;
                var eventArrivedDuringStartup =
                    currentSnapshot.Status != DebugSessionStatus.Starting;
                SetSnapshot(currentSnapshot with
                {
                    ProcessId = result.ProcessId,
                    Capabilities = result.Capabilities,
                    Status = eventArrivedDuringStartup
                        ? currentSnapshot.Status
                        : result.IsPaused
                            ? DebugSessionStatus.Paused
                            : DebugSessionStatus.Running,
                    Stop = eventArrivedDuringStartup
                        ? currentSnapshot.Stop
                        : result.InitialStop
                });
            }
            catch (Exception exception)
            {
                if (nextEngine is not null)
                {
                    await DisposeEngineAsync().ConfigureAwait(false);
                }

                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Faulted,
                    Error = exception.Message
                });
                throw;
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default) =>
        StopAsync(detach: false, cancellationToken);

    public Task DetachAsync(CancellationToken cancellationToken = default) =>
        StopAsync(detach: true, cancellationToken);

    private async Task StopAsync(bool detach, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = engine;
            if (current is null)
                return;
            if (Snapshot.Status == DebugSessionStatus.Terminated)
            {
                await DisposeEngineAsync().ConfigureAwait(false);
                return;
            }

            SetSnapshot(Snapshot with { Status = DebugSessionStatus.Stopping, Stop = null });
            try
            {
                if (detach)
                    await current.DetachAsync(cancellationToken).ConfigureAwait(false);
                else
                    await current.TerminateAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref engineGeneration);
                await DisposeEngineAsync().ConfigureAwait(false);
                SetBreakpoints([]);
                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Terminated,
                    Stop = null
                });
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    public Task ContinueAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            nameof(ContinueAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.ContinueAsync(token),
            DebugSessionStatus.Running,
            cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            nameof(PauseAsync),
            [DebugSessionStatus.Running],
            (value, token) => value.PauseAsync(token),
            resultingStatus: null,
            cancellationToken);

    public Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            nameof(StepAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.StepAsync(thread, kind, token),
            DebugSessionStatus.Running,
            cancellationToken);

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> requestedBreakpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBreakpoints);
        var result = await QueryEngineAsync(
            nameof(SetBreakpointsAsync),
            ActiveStatuses,
            (value, token) => value.SetBreakpointsAsync(requestedBreakpoints, token),
            cancellationToken).ConfigureAwait(false);
        SetBreakpoints(result);
        return result;
    }

    public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken = default) =>
        QueryEngineAsync(
            nameof(GetThreadsAsync),
            ActiveStatuses,
            (value, token) => value.GetThreadsAsync(token),
            cancellationToken);

    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken = default) =>
        QueryEngineAsync(
            nameof(GetStackTraceAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.GetStackTraceAsync(thread, token),
            cancellationToken);

    public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken = default) =>
        QueryEngineAsync(
            nameof(GetScopesAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.GetScopesAsync(frame, token),
            cancellationToken);

    public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken = default) =>
        QueryEngineAsync(
            nameof(GetVariablesAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.GetVariablesAsync(reference, token),
            cancellationToken);

    public Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return QueryEngineAsync(
            nameof(EvaluateAsync),
            [DebugSessionStatus.Paused],
            (value, token) => value.EvaluateAsync(expression, frame, token),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Increment(ref engineGeneration);
            await DisposeEngineAsync().ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private static DebugSessionStatus[] ActiveStatuses { get; } =
        [DebugSessionStatus.Running, DebugSessionStatus.Paused];

    private async Task RunCommandAsync(
        string command,
        IReadOnlyList<DebugSessionStatus> allowedStatuses,
        Func<IDebuggerEngine, CancellationToken, Task> action,
        DebugSessionStatus? resultingStatus,
        CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = GetEngine(command, allowedStatuses);
            await action(current, cancellationToken).ConfigureAwait(false);
            if (resultingStatus is { } status)
                SetSnapshot(Snapshot with { Status = status, Stop = null });
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<T> QueryEngineAsync<T>(
        string command,
        IReadOnlyList<DebugSessionStatus> allowedStatuses,
        Func<IDebuggerEngine, CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = GetEngine(command, allowedStatuses);
            return await query(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private IDebuggerEngine GetEngine(
        string command,
        IReadOnlyList<DebugSessionStatus> allowedStatuses)
    {
        ThrowIfDisposed();
        EnsureStatus(command, [.. allowedStatuses]);
        return engine ?? throw new InvalidOperationException(
            $"{command} requires an active debugger engine.");
    }

    private void EnsureStatus(string command, params DebugSessionStatus[] allowedStatuses)
    {
        var status = Snapshot.Status;
        if (!allowedStatuses.Contains(status))
            throw new InvalidOperationException(
                $"{command} is invalid while debugger status is {status}.");
    }

    private void OnEngineEvent(long generation, DebugEngineEvent value)
    {
        if (generation != Volatile.Read(ref engineGeneration)) return;

        switch (value)
        {
            case DebugEngineStopped stopped:
                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Paused,
                    Stop = stopped.Stop,
                    Error = null
                });
                break;
            case DebugEngineContinued:
                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Running,
                    Stop = null
                });
                break;
            case DebugEngineExited:
                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Terminated,
                    Stop = null
                });
                break;
            case DebugEngineFaulted faulted:
                SetSnapshot(Snapshot with
                {
                    Status = DebugSessionStatus.Faulted,
                    Stop = null,
                    Error = faulted.Message
                });
                break;
            case DebugEngineOutput output:
                OutputReceived?.Invoke(output.Output);
                break;
            case DebugEngineBreakpointsChanged changed:
                SetBreakpoints(changed.Breakpoints);
                break;
        }
    }

    private void SetSnapshot(DebugSessionSnapshot value)
    {
        lock (stateGate) snapshot = value;
        StateChanged?.Invoke(value);
    }

    private void SetBreakpoints(IReadOnlyList<DebugBreakpointBinding> value)
    {
        lock (stateGate) breakpoints = value;
        BreakpointsChanged?.Invoke(value);
    }

    private async ValueTask DisposeEngineAsync()
    {
        var current = engine;
        var handler = engineHandler;
        engine = null;
        engineHandler = null;
        if (current is null) return;
        if (handler is not null) current.EventReceived -= handler;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
