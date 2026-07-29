using DnSpyXDX.Application;
using DnSpyXDX.UI;

namespace DnSpyXDX.Host.Mcp;

public sealed class DebuggerAutomationService : IDisposable
{
    private readonly IDebuggerService debugger;
    private readonly DebuggerWorkspace workspace;
    private readonly object gate = new();
    private Guid sessionId;
    private long stopGeneration;
    private TaskCompletionSource changed = NewSignal();
    private bool disposed;

    public DebuggerAutomationService(
        IDebuggerService debugger,
        DebuggerWorkspace workspace)
    {
        this.debugger = debugger;
        this.workspace = workspace;
        debugger.StateChanged += OnStateChanged;
    }

    public async Task<McpDebugStatus> LaunchAsync(
        string path,
        IReadOnlyList<string>? arguments,
        bool stopAtEntry,
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (sessionId != Guid.Empty && debugger.Snapshot.Status is not
                (DebugSessionStatus.Terminated or DebugSessionStatus.Faulted))
                throw new InvalidOperationException("A debugger automation session is already active.");
            sessionId = Guid.NewGuid();
            stopGeneration = 0;
        }
        try
        {
            await debugger.StartAsync(
                new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    path,
                    arguments,
                    WorkingDirectory: Path.GetDirectoryName(path),
                    StopAtEntry: stopAtEntry)
                {
                    InitialBreakpoints = breakpoints
                },
                cancellationToken);
            return Status(sessionId);
        }
        catch
        {
            lock (gate) sessionId = Guid.Empty;
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
            if (sessionId != Guid.Empty && debugger.Snapshot.Status is not
                (DebugSessionStatus.Terminated or DebugSessionStatus.Faulted))
                throw new InvalidOperationException("A debugger automation session is already active.");
            sessionId = Guid.NewGuid();
            stopGeneration = 0;
        }
        try
        {
            await debugger.StartAsync(
                new DebugAttachRequest(runtime, processId, host, port),
                cancellationToken);
            return Status(sessionId);
        }
        catch
        {
            lock (gate) sessionId = Guid.Empty;
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
        if (timeoutMilliseconds is <= 0 or > 300_000)
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                "Timeout must be between 1 and 300000 milliseconds.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        while (true)
        {
            var status = Status(requestedSession);
            if (status.Status is DebugSessionStatus.Paused or
                DebugSessionStatus.Terminated or DebugSessionStatus.Faulted)
                return status;
            Task signal;
            lock (gate) signal = changed.Task;
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

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        Guid requestedSession,
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        return await debugger.SetBreakpointsAsync(breakpoints, cancellationToken);
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
        DebugThreadId thread,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
        return await debugger.GetStackTraceAsync(thread, cancellationToken);
    }

    public async Task<IReadOnlyList<McpDebugScopeVariables>> VariablesAsync(
        Guid requestedSession,
        DebugFrameId frame,
        int maximumVariables,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
        if (maximumVariables is <= 0 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(maximumVariables));
        var result = new List<McpDebugScopeVariables>();
        foreach (var scope in await debugger.GetScopesAsync(frame, cancellationToken))
        {
            var variables = scope.Variables.Value == 0
                ? []
                : (await debugger.GetVariablesAsync(scope.Variables, cancellationToken))
                    .Take(maximumVariables)
                    .Select(workspace.DisplayVariable)
                    .Select(McpDebugVariable.From)
                    .ToArray();
            result.Add(new(scope.Name, variables));
        }
        return result;
    }

    public async Task<DebugEvaluationResult> EvaluateAsync(
        Guid requestedSession,
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
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
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        RequirePaused(requestedSession);
        await debugger.StepAsync(thread, kind, cancellationToken);
        return Status(requestedSession);
    }

    public async Task<McpDebugStatus> StopAsync(
        Guid requestedSession,
        bool terminate,
        CancellationToken cancellationToken)
    {
        RequireSession(requestedSession);
        if (!terminate && debugger.Snapshot.Runtime == DebugRuntimeKind.CoreClr)
            throw new NotSupportedException(
                "CoreCLR detach is not implemented; pass terminate=true explicitly.");
        await debugger.TerminateAsync(cancellationToken);
        return Status(requestedSession);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debugger.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(DebugSessionSnapshot snapshot)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            if (sessionId == Guid.Empty) return;
            if (snapshot.Status is DebugSessionStatus.Paused or
                DebugSessionStatus.Terminated or DebugSessionStatus.Faulted)
                stopGeneration++;
            signal = changed;
            changed = NewSignal();
        }
        signal.TrySetResult();
    }

    private void RequirePaused(Guid requestedSession)
    {
        RequireSession(requestedSession);
        if (debugger.Snapshot.Status != DebugSessionStatus.Paused)
            throw new InvalidOperationException("The debug target must be paused.");
    }

    private void RequireSession(Guid requestedSession)
    {
        lock (gate)
            if (requestedSession == Guid.Empty || requestedSession != sessionId)
                throw new KeyNotFoundException("The debugger automation session is unknown or expired.");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    DebugVariableNameOrigin NameOrigin,
    int? Slot)
{
    public static McpDebugVariable From(DebugVariable value) => new(
        value.Name,
        value.Value.Length <= 4096 ? value.Value : value.Value[..4096],
        value.Type,
        value.Variables.Value,
        value.EvaluateName,
        value.NameOrigin,
        value.Slot);
}

public sealed record McpDebugScopeVariables(
    string Scope,
    IReadOnlyList<McpDebugVariable> Variables);
