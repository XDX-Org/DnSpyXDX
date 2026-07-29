using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed class UnityMonoDebuggerEngineProvider(
    MonoSoftDebuggerOptions? options = null) : IDebuggerEngineProvider
{
    private readonly MonoSoftDebuggerOptions options = options ?? new();

    public DebugRuntimeKind Runtime => DebugRuntimeKind.UnityMono;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public async ValueTask<IDebuggerEngine> CreateAsync(
        CancellationToken cancellationToken)
    {
        var provider = new MonoSoftDebuggerEngineProvider(options);
        return new UnityMonoDebuggerEngine(
            await provider.CreateAsync(cancellationToken));
    }
}

internal sealed class UnityMonoDebuggerEngine(IDebuggerEngine inner) : IDebuggerEngine
{
    public event Action<DebugEngineEvent>? EventReceived
    {
        add => inner.EventReceived += value;
        remove => inner.EventReceived -= value;
    }

    public Task<DebugEngineStartResult> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken)
    {
        if (request is not DebugAttachRequest
            {
                Runtime: DebugRuntimeKind.UnityMono,
                Host: not null,
                Port: > 0 and <= 65535
            } attach)
            throw new ArgumentException(
                "Unity Mono debugging requires an explicit discovered host and port.",
                nameof(request));
        var monoRequest = new DebugAttachRequest(
            DebugRuntimeKind.Mono,
            attach.ProcessId,
            attach.Host,
            attach.Port)
        {
            InitialBreakpoints = attach.InitialBreakpoints
        };
        return inner.StartAsync(monoRequest, cancellationToken);
    }

    public Task TerminateAsync(CancellationToken cancellationToken) =>
        inner.TerminateAsync(cancellationToken);
    public Task ContinueAsync(CancellationToken cancellationToken) =>
        inner.ContinueAsync(cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken) =>
        inner.PauseAsync(cancellationToken);
    public Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken) =>
        inner.StepAsync(thread, kind, cancellationToken);
    public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken) =>
        inner.SetBreakpointsAsync(breakpoints, cancellationToken);
    public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        inner.GetThreadsAsync(cancellationToken);
    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken) =>
        inner.GetStackTraceAsync(thread, cancellationToken);
    public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken) =>
        inner.GetScopesAsync(frame, cancellationToken);
    public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken) =>
        inner.GetVariablesAsync(reference, cancellationToken);
    public Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken) =>
        inner.EvaluateAsync(expression, frame, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
