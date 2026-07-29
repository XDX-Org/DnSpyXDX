using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed record DebugEngineStartResult(
    int? ProcessId,
    DebuggerCapabilities Capabilities,
    bool IsPaused = false,
    DebugStopInfo? InitialStop = null);

public abstract record DebugEngineEvent;
public sealed record DebugEngineStopped(DebugStopInfo Stop) : DebugEngineEvent;
public sealed record DebugEngineContinued : DebugEngineEvent;
public sealed record DebugEngineExited(int? ExitCode = null) : DebugEngineEvent;
public sealed record DebugEngineFaulted(string Message) : DebugEngineEvent;
public sealed record DebugEngineOutput(DebugOutputMessage Output) : DebugEngineEvent;
public sealed record DebugEngineBreakpointsChanged(
    IReadOnlyList<DebugBreakpointBinding> Breakpoints) : DebugEngineEvent;

/// <summary>
/// One runtime-specific debugger connection. Implementations must translate native runtime
/// identities into MVID/token/IL-offset identities before raising events.
/// </summary>
public interface IDebuggerEngine : IAsyncDisposable
{
    event Action<DebugEngineEvent>? EventReceived;

    Task<DebugEngineStartResult> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken);
    Task TerminateAsync(CancellationToken cancellationToken);
    Task ContinueAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StepAsync(DebugThreadId thread, DebugStepKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken);
    Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken);
}

public interface IDebuggerEngineProvider
{
    DebugRuntimeKind Runtime { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    ValueTask<IDebuggerEngine> CreateAsync(CancellationToken cancellationToken);
}

public interface IDebuggerEngineRegistry
{
    ValueTask<IDebuggerEngine> CreateAsync(
        DebugRuntimeKind runtime,
        CancellationToken cancellationToken);
}

public sealed class DebuggerEngineRegistry(IEnumerable<IDebuggerEngineProvider> providers)
    : IDebuggerEngineRegistry
{
    private readonly IReadOnlyDictionary<DebugRuntimeKind, IDebuggerEngineProvider> providers =
        providers.ToDictionary(provider => provider.Runtime);

    public ValueTask<IDebuggerEngine> CreateAsync(
        DebugRuntimeKind runtime,
        CancellationToken cancellationToken)
    {
        if (!providers.TryGetValue(runtime, out var provider))
            throw new NotSupportedException($"No debugger engine is registered for {runtime}.");
        if (!provider.IsAvailable)
            throw new NotSupportedException(
                provider.UnavailableReason ?? $"Debugger engine for {runtime} is unavailable.");
        return provider.CreateAsync(cancellationToken);
    }
}
