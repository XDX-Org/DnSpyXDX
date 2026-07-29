using System.ComponentModel;
using System.Net;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Tools;

[McpServerToolType]
public sealed class DebuggerTools(
    DebuggerAutomationService debugger,
    McpServerSettings settings,
    McpActivityLog activity)
{
    [McpServerTool(Name = "debug_launch", Destructive = true, UseStructuredContent = true)]
    [Description("Launches an allowed managed DLL or executable in a detached CoreCLR debugger worker.")]
    public Task<McpDebugStatus> LaunchAsync(
        string path,
        IReadOnlyList<string>? arguments = null,
        bool stopAtEntry = false,
        IReadOnlyList<McpDebugBreakpoint>? breakpoints = null,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_launch", Path.GetFileName(path), async token =>
        {
            var target = AssemblyTools.ValidatePath(path, settings.AllowedRoots);
            return await debugger.LaunchAsync(
                target,
                arguments,
                stopAtEntry,
                (breakpoints ?? []).Select(value => value.ToBreakpoint()).ToArray(),
                token);
        }, cancellationToken);

    [McpServerTool(Name = "debug_attach", Destructive = true, UseStructuredContent = true)]
    [Description("Attaches a detached debugger worker to a CoreCLR PID or loopback Mono/Unity Mono endpoint.")]
    public Task<McpDebugStatus> AttachAsync(
        DebugRuntimeKind runtime,
        int? processId = null,
        string? host = null,
        int? port = null,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_attach", runtime.ToString(), async token =>
        {
            if (runtime is DebugRuntimeKind.Mono or DebugRuntimeKind.UnityMono &&
                (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address)))
                throw new UnauthorizedAccessException(
                    "MCP Mono and Unity endpoints are restricted to explicit loopback addresses.");
            return await debugger.AttachAsync(runtime, processId, host, port, token);
        }, cancellationToken);

    [McpServerTool(Name = "debug_set_breakpoints", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Replaces all breakpoints for the active debugger session using exact MVID, MethodDef token and IL offset identities.")]
    public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        Guid sessionId,
        IReadOnlyList<McpDebugBreakpoint> breakpoints,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "debug_set_breakpoints",
            sessionId.ToString("D"),
            token => debugger.SetBreakpointsAsync(
                sessionId,
                breakpoints.Select(value => value.ToBreakpoint()).ToArray(),
                token),
            cancellationToken);

    [McpServerTool(Name = "debug_wait_for_stop", ReadOnly = true, UseStructuredContent = true)]
    [Description("Waits for the target to pause, exit or fault without polling the runtime worker.")]
    public Task<McpDebugStatus> WaitForStopAsync(
        Guid sessionId,
        int timeoutMilliseconds = 30_000,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "debug_wait_for_stop",
            sessionId.ToString("D"),
            token => debugger.WaitForStopAsync(sessionId, timeoutMilliseconds, token),
            cancellationToken);

    [McpServerTool(Name = "debug_status", ReadOnly = true, UseStructuredContent = true)]
    public McpDebugStatus Status(Guid sessionId) => debugger.Status(sessionId);

    [McpServerTool(Name = "debug_get_threads", ReadOnly = true, UseStructuredContent = true)]
    public Task<IReadOnlyList<DebugThread>> ThreadsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_get_threads", sessionId.ToString("D"),
            token => debugger.ThreadsAsync(sessionId, token), cancellationToken);

    [McpServerTool(Name = "debug_get_stack", ReadOnly = true, UseStructuredContent = true)]
    public Task<IReadOnlyList<DebugStackFrame>> StackAsync(
        Guid sessionId,
        long threadId,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_get_stack", sessionId.ToString("D"),
            token => debugger.StackAsync(sessionId, new(threadId), token), cancellationToken);

    [McpServerTool(Name = "debug_get_variables", ReadOnly = true, UseStructuredContent = true)]
    public Task<IReadOnlyList<McpDebugScopeVariables>> VariablesAsync(
        Guid sessionId,
        long frameId,
        int maximumVariables = 200,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_get_variables", sessionId.ToString("D"),
            token => debugger.VariablesAsync(sessionId, new(frameId), maximumVariables, token),
            cancellationToken);

    [McpServerTool(Name = "debug_evaluate", Destructive = true, UseStructuredContent = true)]
    public Task<DebugEvaluationResult> EvaluateAsync(
        Guid sessionId,
        string expression,
        long? frameId = null,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_evaluate", sessionId.ToString("D"),
            token => debugger.EvaluateAsync(
                sessionId,
                expression,
                frameId is { } value ? new(value) : null,
                token),
            cancellationToken);

    [McpServerTool(Name = "debug_continue", Destructive = true, UseStructuredContent = true)]
    public Task<McpDebugStatus> ContinueAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_continue", sessionId.ToString("D"),
            token => debugger.ContinueAsync(sessionId, token), cancellationToken);

    [McpServerTool(Name = "debug_pause", Destructive = true, UseStructuredContent = true)]
    public Task<McpDebugStatus> PauseAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_pause", sessionId.ToString("D"),
            token => debugger.PauseAsync(sessionId, token), cancellationToken);

    [McpServerTool(Name = "debug_step", Destructive = true, UseStructuredContent = true)]
    public Task<McpDebugStatus> StepAsync(
        Guid sessionId,
        long threadId,
        DebugStepKind kind,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_step", sessionId.ToString("D"),
            token => debugger.StepAsync(sessionId, new(threadId), kind, token), cancellationToken);

    [McpServerTool(Name = "debug_stop", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Stops a debugger session. CoreCLR requires terminate=true; Mono detach uses terminate=false.")]
    public Task<McpDebugStatus> StopAsync(
        Guid sessionId,
        bool terminate,
        CancellationToken cancellationToken = default) =>
        RunAsync("debug_stop", sessionId.ToString("D"),
            token => debugger.StopAsync(sessionId, terminate, token), cancellationToken);

    private async Task<T> RunAsync<T>(
        string operation,
        string target,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin(operation, target, countRequest: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operation == "debug_wait_for_stop"
            ? TimeSpan.FromMinutes(5)
            : settings.RequestTimeout);
        try
        {
            var result = await action(timeout.Token);
            activity.Add(new(started, operation, target, "succeeded", DateTimeOffset.UtcNow - started));
            return result;
        }
        catch (Exception exception)
        {
            activity.Add(new(
                started,
                operation,
                target,
                exception is OperationCanceledException ? "cancelled" : "failed",
                DateTimeOffset.UtcNow - started,
                exception.Message));
            if (exception is OperationCanceledException) throw;
            throw McpErrors.Debugger(exception);
        }
    }
}

public sealed record McpDebugBreakpoint(
    Guid Id,
    Guid ModuleMvid,
    int MethodToken,
    int ILOffset,
    bool Enabled = true,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null)
{
    public DebugBreakpoint ToBreakpoint()
    {
        if (Id == Guid.Empty || ModuleMvid == Guid.Empty ||
            MethodToken <= 0 || ILOffset < 0)
            throw new ArgumentException("Breakpoint identity is invalid.");
        return new(
            Id,
            new(new(ModuleMvid, MethodToken), ILOffset),
            Enabled,
            Condition,
            HitCondition,
            LogMessage);
    }
}
