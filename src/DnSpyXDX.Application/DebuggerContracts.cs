namespace DnSpyXDX.Application;

/// <summary>
/// UI-facing debugger API. Runtime protocols and native debugger objects stay behind this boundary.
/// </summary>
public interface IDebuggerService : IAsyncDisposable
{
    DebugSessionSnapshot Snapshot { get; }
    IReadOnlyList<DebugBreakpointBinding> Breakpoints { get; }
    event Action<DebugSessionSnapshot>? StateChanged;
    event Action<IReadOnlyList<DebugBreakpointBinding>>? BreakpointsChanged;
    event Action<DebugOutputMessage>? OutputReceived;

    Task StartAsync(DebugStartRequest request, CancellationToken cancellationToken = default);
    Task TerminateAsync(CancellationToken cancellationToken = default);
    Task DetachAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This debugger does not support detach.");
    Task ContinueAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StepAsync(DebugThreadId thread, DebugStepKind kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken = default);
    Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame = null,
        CancellationToken cancellationToken = default);
}
