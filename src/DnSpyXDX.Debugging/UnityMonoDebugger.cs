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
        if (attach.ScriptingBackend == DebugScriptingBackend.Il2Cpp)
            throw new NotSupportedException(
                "Unity IL2CPP is native code and cannot use the managed Mono debugger.");
        _ = UnityMonoCompatibilityProfile.Select(attach.RuntimeVersion);
        if (attach.DebuggerProtocolVersion is <= 0)
            throw new NotSupportedException(
                "Unity reported an invalid Mono debugger protocol version.");
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
    public Task DetachAsync(CancellationToken cancellationToken) =>
        inner.DetachAsync(cancellationToken);
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

public sealed record UnityMonoCompatibilityProfile(
    string Name,
    int MinimumMajor,
    int MaximumMajor)
{
    private static readonly UnityMonoCompatibilityProfile[] Profiles =
    [
        new("legacy", 2018, 2020),
        new("lts", 2021, 2023),
        new("unity6", 6000, 6999)
    ];

    public static UnityMonoCompatibilityProfile? Select(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var majorText = version.Split('.', 2)[0];
        if (!int.TryParse(majorText, out var major))
            throw new NotSupportedException(
                $"Unity version '{version}' cannot be negotiated.");
        return Profiles.FirstOrDefault(value =>
                value.MinimumMajor <= major && major <= value.MaximumMajor) ??
            throw new NotSupportedException(
                $"Unity version '{version}' has no supported Mono compatibility profile.");
    }
}
