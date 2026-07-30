using DnSpyXDX.Application;
using DnSpyXDX.UI;

namespace DnSpyXDX.Host.Mcp;

public sealed class DebuggerAutomationService : IDisposable
{
    private readonly IDebuggerService debugger;
    private readonly DebuggerWorkspace workspace;
    private readonly McpServerSettings settings;
    private readonly Timer leaseTimer;
    private readonly object gate = new();
    private readonly AsyncLocal<Guid> currentOwner = new();
    private Guid sessionId;
    private Guid sessionOwner;
    private long stopGeneration;
    private TaskCompletionSource changed = NewSignal();
    private DateTimeOffset lastAccess;
    private DebugSessionStatus lastStatus;
    private DebugStopInfo? lastStop;
    private int waiting;
    private bool disposed;

    public DebuggerAutomationService(
        IDebuggerService debugger,
        DebuggerWorkspace workspace,
        McpServerSettings settings)
    {
        this.debugger = debugger;
        this.workspace = workspace;
        this.settings = settings;
        debugger.StateChanged += OnStateChanged;
        lastStatus = debugger.Snapshot.Status;
        leaseTimer = new(_ => _ = ExpireLeaseAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<McpDebugStatus> LaunchAsync(
        string path,
        IReadOnlyList<string>? arguments,
        IReadOnlyDictionary<string, string>? environment,
        bool stopAtEntry,
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (currentOwner.Value == Guid.Empty)
                throw new InvalidOperationException("Debugger automation requires an MCP owner.");
            if (sessionId != Guid.Empty && debugger.Snapshot.Status is not
                (DebugSessionStatus.Terminated or DebugSessionStatus.Faulted))
                throw new InvalidOperationException("debug_session_active: a debugger automation session is already active.");
            sessionId = Guid.NewGuid();
            sessionOwner = currentOwner.Value;
            stopGeneration = 0;
            lastAccess = DateTimeOffset.UtcNow;
        }
        try
        {
            workspace.SetMcpControl(true);
            await workspace.SetMcpBreakpointsAsync(breakpoints, cancellationToken);
            await debugger.StartAsync(
                new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    path,
                    arguments,
                    WorkingDirectory: Path.GetDirectoryName(path),
                    Environment: environment,
                    StopAtEntry: stopAtEntry)
                {
                    InitialBreakpoints = workspace.Breakpoints
                },
                cancellationToken);
            return Status(sessionId);
        }
        catch
        {
            lock (gate) sessionId = Guid.Empty;
            workspace.ClearMcpBreakpoints();
            workspace.SetMcpControl(false);
            throw;
        }
    }

    public async Task<McpDebugStatus> AttachAsync(
        DebugRuntimeKind runtime,
        int? processId,
        string? host,
        int? port,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (currentOwner.Value == Guid.Empty)
                throw new InvalidOperationException("Debugger automation requires an MCP owner.");
            if (sessionId != Guid.Empty && debugger.Snapshot.Status is not
                (DebugSessionStatus.Terminated or DebugSessionStatus.Faulted))
                throw new InvalidOperationException("debug_session_active: a debugger automation session is already active.");
            sessionId = Guid.NewGuid();
            sessionOwner = currentOwner.Value;
            stopGeneration = 0;
            lastAccess = DateTimeOffset.UtcNow;
        }
        try
        {
            workspace.SetMcpControl(true);
            await debugger.StartAsync(
                new DebugAttachRequest(
                    runtime,
                    processId,
                    host,
                    port),
                cancellationToken);
            return Status(sessionId);
        }
        catch
        {
            lock (gate) sessionId = Guid.Empty;
            workspace.SetMcpControl(false);
            throw;
        }
    }

    public McpDebugStatus Status(Guid requestedSession)
    {
        RequireSession(requestedSession);
        var snapshot = debugger.Snapshot;
        lock (gate)
            return new(
                requestedSession,
                stopGeneration,
                snapshot.Status,
                snapshot.Runtime,
                snapshot.ProcessId,
                snapshot.Stop?.Reason,
                snapshot.Stop?.Thread.Value,
                snapshot.Stop?.Location,
                snapshot.Error,
                snapshot.Capabilities);
    }

    public async Task<McpDebugStatus> WaitForStopAsync(
        Guid requestedSession,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        if (Interlocked.CompareExchange(ref waiting, 1, 0) != 0)
            throw new InvalidOperationException(
                "debug_wait_active: a wait is already active for this debugger session.");
        if (timeoutMilliseconds is <= 0 or > 300_000)
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                "Timeout must be between 1 and 300000 milliseconds.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            while (true)
            {
                Task signal;
                lock (gate) signal = changed.Task;
                var status = Status(requestedSession);
                if (status.Status is DebugSessionStatus.Paused or
                    DebugSessionStatus.Terminated or DebugSessionStatus.Faulted)
                    return status;
                try
                {
                    await signal.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The target did not stop before the timeout.");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref waiting, 0);
        }
    }

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        Guid requestedSession,
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        return await workspace.SetMcpBreakpointsAsync(breakpoints, cancellationToken);
    }

    public async Task<IReadOnlyList<DebugThread>> ThreadsAsync(
        Guid requestedSession,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
        return await debugger.GetThreadsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DebugStackFrame>> StackAsync(
        Guid requestedSession,
        long requestedStopGeneration,
        DebugThreadId thread,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession, requestedStopGeneration);
        return await debugger.GetStackTraceAsync(thread, cancellationToken);
    }

    public async Task<IReadOnlyList<DebugScope>> ScopesAsync(
        Guid requestedSession,
        long requestedStopGeneration,
        DebugFrameId frame,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession, requestedStopGeneration);
        return await debugger.GetScopesAsync(frame, cancellationToken);
    }

    public async Task<IReadOnlyList<McpDebugScopeVariables>> VariablesAsync(
        Guid requestedSession,
        long requestedStopGeneration,
        DebugFrameId frame,
        int maximumVariables,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession, requestedStopGeneration);
        if (maximumVariables is <= 0 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(maximumVariables));
        if (maximumDepth is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        var remaining = maximumVariables;
        var visited = new HashSet<DebugVariableReference>();
        var result = new List<McpDebugScopeVariables>();
        foreach (var scope in await debugger.GetScopesAsync(frame, cancellationToken))
        {
            var variables = scope.Variables.Value == 0 || remaining == 0
                ? []
                : await ReadVariablesAsync(
                    scope.Variables,
                    frame,
                    maximumDepth,
                    visited,
                    () => remaining,
                    value => remaining = value,
                    cancellationToken);
            result.Add(new(scope.Name, variables));
        }
        return result;
    }

    public async Task<DebugEvaluationResult> EvaluateAsync(
        Guid requestedSession,
        long requestedStopGeneration,
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession, requestedStopGeneration);
        return await debugger.EvaluateAsync(expression, frame, cancellationToken);
    }

    public async Task<McpDebugStatus> ContinueAsync(
        Guid requestedSession,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
        await debugger.ContinueAsync(cancellationToken);
        return Status(requestedSession);
    }

    public async Task<McpDebugStatus> PauseAsync(
        Guid requestedSession,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        await debugger.PauseAsync(cancellationToken);
        return Status(requestedSession);
    }

    public async Task<McpDebugStatus> StepAsync(
        Guid requestedSession,
        long requestedStopGeneration,
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession, requestedStopGeneration);
        await debugger.StepAsync(thread, kind, cancellationToken);
        return Status(requestedSession);
    }

    public async Task<McpDebugStatus> StopAsync(
        Guid requestedSession,
        bool terminate,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        if (terminate)
            await debugger.TerminateAsync(cancellationToken);
        else
            await debugger.DetachAsync(cancellationToken);
        var result = Status(requestedSession);
        workspace.ClearMcpBreakpoints();
        workspace.SetMcpControl(false);
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debugger.StateChanged -= OnStateChanged;
        leaseTimer.Dispose();
        workspace.ClearMcpBreakpoints();
        workspace.SetMcpControl(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            sessionId = Guid.Empty;
            sessionOwner = Guid.Empty;
            signal = changed;
            changed = NewSignal();
        }
        signal.TrySetResult();
        try
        {
            if (debugger.Snapshot.Status is DebugSessionStatus.Starting or
                DebugSessionStatus.Running or DebugSessionStatus.Paused)
                await debugger.TerminateAsync(cancellationToken);
        }
        finally
        {
            workspace.ClearMcpBreakpoints();
            workspace.SetMcpControl(false);
        }
    }

    public IDisposable EnterOwner(Guid owner)
    {
        if (owner == Guid.Empty) throw new ArgumentException("MCP owner cannot be empty.", nameof(owner));
        var previous = currentOwner.Value;
        currentOwner.Value = owner;
        return new OwnerScope(() => currentOwner.Value = previous);
    }

    private void OnStateChanged(DebugSessionSnapshot snapshot)
    {
        TaskCompletionSource signal;
        var releaseControl = false;
        lock (gate)
        {
            if (sessionId == Guid.Empty) return;
            if ((snapshot.Status == DebugSessionStatus.Paused &&
                    (lastStatus != DebugSessionStatus.Paused || snapshot.Stop != lastStop)) ||
                snapshot.Status is DebugSessionStatus.Terminated or DebugSessionStatus.Faulted ||
                (lastStatus == DebugSessionStatus.Paused &&
                    snapshot.Status != DebugSessionStatus.Paused))
                stopGeneration++;
            lastStatus = snapshot.Status;
            lastStop = snapshot.Stop;
            if (snapshot.Status is DebugSessionStatus.Terminated or DebugSessionStatus.Faulted)
                releaseControl = true;
            signal = changed;
            changed = NewSignal();
        }
        if (releaseControl)
        {
            workspace.ClearMcpBreakpoints();
            workspace.SetMcpControl(false);
        }
        signal.TrySetResult();
    }

    private void RequirePaused(Guid requestedSession, long? requestedStopGeneration = null)
    {
        RequireSession(requestedSession);
        lock (gate)
            if (requestedStopGeneration is { } value && value != stopGeneration)
                throw new InvalidOperationException(
                    "stale_reference: the target has resumed or stopped again.");
        if (debugger.Snapshot.Status != DebugSessionStatus.Paused)
            throw new InvalidOperationException("debug_target_not_paused: the debug target must be paused.");
    }

    private void RequireSession(Guid requestedSession)
    {
        lock (gate)
        {
            if (requestedSession == Guid.Empty || requestedSession != sessionId)
                throw new KeyNotFoundException("The debugger automation session is unknown or expired.");
            if (currentOwner.Value == Guid.Empty || currentOwner.Value != sessionOwner)
                throw new UnauthorizedAccessException(
                    "debug_session_owned: the debugger automation session belongs to another MCP client.");
            lastAccess = DateTimeOffset.UtcNow;
        }
    }

    private async Task<IReadOnlyList<McpDebugVariable>> ReadVariablesAsync(
        DebugVariableReference reference,
        DebugFrameId frame,
        int depth,
        HashSet<DebugVariableReference> visited,
        Func<int> getRemaining,
        Action<int> setRemaining,
        CancellationToken cancellationToken)
    {
        if (reference.Value == 0 || getRemaining() == 0 || !visited.Add(reference)) return [];
        var source = await debugger.GetVariablesAsync(reference, cancellationToken);
        var result = new List<McpDebugVariable>();
        foreach (var raw in source)
        {
            var remaining = getRemaining();
            if (remaining == 0) break;
            setRemaining(remaining - 1);
            var variable = raw;
            if (depth > 0 && variable.Variables.Value == 0 &&
                !string.IsNullOrWhiteSpace(variable.EvaluateName) &&
                !DebugVariableTypes.IsScalar(variable.Type))
            {
                try
                {
                    var evaluated = await debugger.EvaluateAsync(
                        variable.EvaluateName,
                        frame,
                        cancellationToken);
                    if (evaluated.Variables.Value != 0)
                        variable = variable with { Variables = evaluated.Variables };
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Some adapters cannot re-evaluate synthetic variables.
                }
            }
            var children = depth > 0
                ? await ReadVariablesAsync(
                    variable.Variables,
                    frame,
                    depth - 1,
                    visited,
                    getRemaining,
                    setRemaining,
                    cancellationToken)
                : [];
            result.Add(McpDebugVariable.From(variable, children));
        }
        return result;
    }

    private async Task ExpireLeaseAsync()
    {
        lock (gate)
        {
            if (disposed || sessionId == Guid.Empty ||
                settings.DebugSessionLease <= TimeSpan.Zero ||
                DateTimeOffset.UtcNow - lastAccess < settings.DebugSessionLease)
                return;
            sessionId = Guid.Empty;
        }
        try
        {
            if (debugger.Snapshot.Status is DebugSessionStatus.Running or DebugSessionStatus.Paused)
                await debugger.TerminateAsync();
            workspace.ClearMcpBreakpoints();
            workspace.SetMcpControl(false);
        }
        catch
        {
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OwnerScope(Action dispose) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) dispose();
        }
    }
}

public sealed record McpDebugStatus(
    Guid SessionId,
    long StopGeneration,
    DebugSessionStatus Status,
    DebugRuntimeKind? Runtime,
    int? ProcessId,
    DebugStopReason? StopReason,
    long? ThreadId,
    DebugCodeLocation? Location,
    string? Error,
    DebuggerCapabilities Capabilities);

public sealed record McpDebugVariable(
    string Name,
    string Value,
    string? Type,
    long VariablesReference,
    string? EvaluateName,
    IReadOnlyList<McpDebugVariable> Children)
{
    public static McpDebugVariable From(
        DebugVariable value,
        IReadOnlyList<McpDebugVariable>? children = null) => new(
        value.Name,
        value.Value.Length <= 4096 ? value.Value : value.Value[..4096],
        value.Type,
        value.Variables.Value,
        value.EvaluateName,
        children ?? []);
}

public sealed record McpDebugScopeVariables(
    string Scope,
    IReadOnlyList<McpDebugVariable> Variables);
