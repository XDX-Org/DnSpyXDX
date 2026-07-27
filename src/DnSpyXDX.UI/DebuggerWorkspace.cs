using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

/// <summary>
/// UI projection of one debugger session. Runtime handles are discarded whenever execution
/// resumes; IL-native breakpoint identities remain stable across session restarts.
/// </summary>
public sealed class DebuggerWorkspace : IDisposable
{
    private const int MaximumOutputMessages = 1_000;
    private readonly IDebuggerService debugger;
    private CancellationTokenSource? refresh;
    private IReadOnlyList<DebugBreakpoint> breakpoints = [];
    private IReadOnlyList<DebugBreakpointBinding> bindings = [];
    private IReadOnlyList<DebugThread> threads = [];
    private IReadOnlyList<DebugStackFrame> frames = [];
    private IReadOnlyList<DebugVariable> variables = [];
    private IReadOnlyList<DebugOutputMessage> output = [];
    private DebugFrameId? selectedFrame;
    private bool disposed;

    public DebuggerWorkspace(IDebuggerService debugger)
    {
        this.debugger = debugger;
        debugger.StateChanged += OnStateChanged;
        debugger.BreakpointsChanged += OnBreakpointsChanged;
        debugger.OutputReceived += OnOutputReceived;
    }

    public DebugSessionSnapshot Snapshot => debugger.Snapshot;
    public IReadOnlyList<DebugBreakpoint> Breakpoints => breakpoints;
    public IReadOnlyList<DebugBreakpointBinding> Bindings => bindings;
    public IReadOnlyList<DebugThread> Threads => threads;
    public IReadOnlyList<DebugStackFrame> Frames => frames;
    public IReadOnlyList<DebugVariable> Variables => variables;
    public IReadOnlyList<DebugOutputMessage> Output => output;
    public DebugFrameId? SelectedFrame => selectedFrame;
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;

    public DebugBreakpointBinding? BindingFor(Guid breakpointId) =>
        bindings.FirstOrDefault(value => value.BreakpointId == breakpointId);

    public DebugBreakpoint? BreakpointAt(DebugCodeLocation location) =>
        breakpoints.FirstOrDefault(value => value.Location == location);

    public Task LaunchAsync(
        string executablePath,
        IReadOnlyList<string>? arguments,
        bool stopAtEntry,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            await debugger.StartAsync(
                new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    executablePath,
                    arguments,
                    StopAtEntry: stopAtEntry)
                {
                    InitialBreakpoints = breakpoints
                },
                token);
        }, cancellationToken, requireActiveSession: false);

    public Task AttachAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            await debugger.StartAsync(
                new DebugAttachRequest(
                    DebugRuntimeKind.CoreClr,
                    ProcessId: processId)
                {
                    InitialBreakpoints = breakpoints
                },
                token);
        }, cancellationToken, requireActiveSession: false);

    public Task AttachMonoAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            await debugger.StartAsync(
                new DebugAttachRequest(
                    DebugRuntimeKind.Mono,
                    Host: host,
                    Port: port)
                {
                    InitialBreakpoints = breakpoints
                },
                token);
        }, cancellationToken, requireActiveSession: false);

    public Task ContinueAsync(CancellationToken cancellationToken = default) =>
        RunAsync(debugger.ContinueAsync, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        RunAsync(debugger.PauseAsync, cancellationToken);

    public Task TerminateAsync(CancellationToken cancellationToken = default) =>
        RunAsync(debugger.TerminateAsync, cancellationToken);

    public Task StepAsync(
        DebugStepKind kind,
        CancellationToken cancellationToken = default)
    {
        var thread = Snapshot.Stop?.Thread ?? threads.FirstOrDefault()?.Id;
        return thread is { } id
            ? RunAsync(token => debugger.StepAsync(id, kind, token), cancellationToken)
            : SetErrorAsync("No stopped thread is available for stepping.");
    }

    public Task ToggleBreakpointAsync(
        DebugCodeLocation location,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var existing = BreakpointAt(location);
            if (existing is null)
                breakpoints =
                [
                    .. breakpoints,
                    new DebugBreakpoint(Guid.NewGuid(), location)
                ];
            else
                breakpoints = breakpoints
                    .Where(value => value.Id != existing.Id)
                    .ToArray();
            await SynchronizeBreakpointsAsync(token);
        }, cancellationToken, requireActiveSession: false);

    public Task SelectFrameAsync(
        DebugStackFrame frame,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => LoadVariablesAsync(frame, token),
            cancellationToken,
            requireActiveSession: false);

    public void ClearOutput()
    {
        output = [];
        NotifyChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refresh?.Cancel();
        refresh?.Dispose();
        debugger.StateChanged -= OnStateChanged;
        debugger.BreakpointsChanged -= OnBreakpointsChanged;
        debugger.OutputReceived -= OnOutputReceived;
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool requireActiveSession = true)
    {
        if (disposed) throw new ObjectDisposedException(nameof(DebuggerWorkspace));
        IsBusy = true;
        Error = null;
        NotifyChanged();
        try
        {
            if (requireActiveSession &&
                Snapshot.Status is not (DebugSessionStatus.Running or
                    DebugSessionStatus.Paused))
                throw new InvalidOperationException("No active debug session.");
            await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    private Task SetErrorAsync(string message)
    {
        Error = message;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private async Task SynchronizeBreakpointsAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.Status is not (DebugSessionStatus.Running or
            DebugSessionStatus.Paused))
        {
            bindings = breakpoints.Select(value => new DebugBreakpointBinding(
                value.Id,
                false,
                Message: "Pending until a debug session starts.")).ToArray();
            NotifyChanged();
            return;
        }

        bindings = await debugger.SetBreakpointsAsync(
            breakpoints,
            cancellationToken);
        NotifyChanged();
    }

    private void OnStateChanged(DebugSessionSnapshot snapshot)
    {
        if (snapshot.Status != DebugSessionStatus.Paused)
        {
            refresh?.Cancel();
            threads = [];
            frames = [];
            variables = [];
            selectedFrame = null;
        }
        NotifyChanged();
        if (snapshot.Status == DebugSessionStatus.Paused)
            QueuePausedRefresh(snapshot.SessionId);
    }

    private void QueuePausedRefresh(Guid sessionId)
    {
        refresh?.Cancel();
        refresh?.Dispose();
        refresh = new CancellationTokenSource();
        _ = RefreshPausedAsync(sessionId, refresh.Token);
    }

    private async Task RefreshPausedAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var loadedThreads = await debugger.GetThreadsAsync(cancellationToken);
            var stoppedThread = Snapshot.Stop?.Thread;
            var selectedThread = loadedThreads.FirstOrDefault(
                    value => value.Id == stoppedThread) ??
                loadedThreads.FirstOrDefault();
            var loadedFrames = selectedThread is null
                ? []
                : await debugger.GetStackTraceAsync(
                    selectedThread.Id,
                    cancellationToken);
            if (Snapshot.SessionId != sessionId ||
                Snapshot.Status != DebugSessionStatus.Paused)
                return;
            threads = loadedThreads;
            frames = loadedFrames;
            var firstFrame = loadedFrames.FirstOrDefault();
            if (firstFrame is not null)
                await LoadVariablesAsync(firstFrame, cancellationToken);
            else
                NotifyChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            NotifyChanged();
        }
    }

    private async Task LoadVariablesAsync(
        DebugStackFrame frame,
        CancellationToken cancellationToken)
    {
        var scopes = await debugger.GetScopesAsync(frame.Id, cancellationToken);
        var loaded = new List<DebugVariable>();
        foreach (var scope in scopes.Where(value => !value.IsExpensive))
        {
            loaded.AddRange(
                await debugger.GetVariablesAsync(
                    scope.Variables,
                    cancellationToken));
        }
        selectedFrame = frame.Id;
        variables = loaded;
        NotifyChanged();
    }

    private void OnBreakpointsChanged(
        IReadOnlyList<DebugBreakpointBinding> updated)
    {
        bindings = updated;
        NotifyChanged();
    }

    private void OnOutputReceived(DebugOutputMessage message)
    {
        output = output
            .Append(message)
            .TakeLast(MaximumOutputMessages)
            .ToArray();
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}
