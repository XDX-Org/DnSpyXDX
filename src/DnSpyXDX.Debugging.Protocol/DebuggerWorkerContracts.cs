using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging.Protocol;

public static class DebuggerWorkerCommands
{
    public const string Start = "start";
    public const string Terminate = "terminate";
    public const string Continue = "continue";
    public const string Pause = "pause";
    public const string Step = "step";
    public const string ReplaceBreakpoints = "replaceBreakpoints";
    public const string Threads = "threads";
    public const string StackTrace = "stackTrace";
    public const string Scopes = "scopes";
    public const string Variables = "variables";
    public const string Evaluate = "evaluate";
    public const string Shutdown = "shutdown";
}

public static class DebuggerWorkerEvents
{
    public const string Stopped = "stopped";
    public const string Continued = "continued";
    public const string Exited = "exited";
    public const string Faulted = "faulted";
    public const string Output = "output";
    public const string BreakpointsChanged = "breakpointsChanged";
}

public sealed record DebuggerWorkerStartRequest(
    DebugRuntimeKind Runtime,
    DebugLaunchRequest? Launch = null,
    DebugAttachRequest? Attach = null,
    DebuggerWorkerBackendConfiguration? Backend = null)
{
    public DebugStartRequest GetRequest() => (Launch, Attach) switch
    {
        ({ } launch, null) when launch.Runtime == Runtime => launch,
        (null, { } attach) when attach.Runtime == Runtime => attach,
        _ => throw new InvalidDataException(
            "Worker start requires exactly one matching launch or attach request.")
    };

    public static DebuggerWorkerStartRequest From(DebugStartRequest request) => request switch
    {
        DebugLaunchRequest launch => new(request.Runtime, Launch: launch),
        DebugAttachRequest attach => new(request.Runtime, Attach: attach),
        _ => throw new NotSupportedException(
            $"Debugger start request '{request.GetType().Name}' is unsupported.")
    };
}

public sealed record DebuggerWorkerBackendConfiguration(
    string? NetCoreDbgPath = null,
    IReadOnlyList<string>? NetCoreDbgArguments = null,
    int? ShutdownTimeoutMilliseconds = null,
    int? StartupTimeoutMilliseconds = null);

public sealed record DebuggerWorkerStepRequest(DebugThreadId Thread, DebugStepKind Kind);
public sealed record DebuggerWorkerThreadRequest(DebugThreadId Thread);
public sealed record DebuggerWorkerFrameRequest(DebugFrameId Frame);
public sealed record DebuggerWorkerVariablesRequest(DebugVariableReference Reference);
public sealed record DebuggerWorkerEvaluateRequest(string Expression, DebugFrameId? Frame);
public sealed record DebuggerWorkerBreakpointRequest(IReadOnlyList<DebugBreakpoint> Breakpoints);
public sealed record DebuggerWorkerExitEvent(int? ExitCode);
public sealed record DebuggerWorkerFaultEvent(string Message);
