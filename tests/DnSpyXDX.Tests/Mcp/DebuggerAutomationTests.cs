using DnSpyXDX.Application;
using DnSpyXDX.Host.Mcp;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests.Mcp;

public sealed class DebuggerAutomationTests
{
    [Fact]
    public async Task Session_is_owned_and_paused_handles_expire_after_resume()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger,
            workspace,
            new McpServerSettings { DebugSessionLease = TimeSpan.FromMinutes(1) });
        var owner = Guid.NewGuid();
        McpDebugStatus launched;
        using (automation.EnterOwner(owner))
            launched = await automation.LaunchAsync("sample.dll", [], null, false, [], default);

        Assert.True(workspace.IsMcpControlled);
        using (automation.EnterOwner(Guid.NewGuid()))
            Assert.Throws<UnauthorizedAccessException>(() => automation.Status(launched.SessionId));

        debugger.Publish(DebugSessionStatus.Paused, new(DebugStopReason.Entry, new(7)));
        McpDebugStatus paused;
        using (automation.EnterOwner(owner)) paused = automation.Status(launched.SessionId);
        Assert.True(paused.StopGeneration > 0);

        using (automation.EnterOwner(owner))
            Assert.Single(await automation.StackAsync(
                launched.SessionId,
                paused.StopGeneration,
                new(7),
                default));

        debugger.Publish(DebugSessionStatus.Running);
        debugger.Publish(DebugSessionStatus.Paused, new(DebugStopReason.Breakpoint, new(7)));
        using (automation.EnterOwner(owner))
            await Assert.ThrowsAsync<InvalidOperationException>(() => automation.StackAsync(
                launched.SessionId,
                paused.StopGeneration,
                new(7),
                default));

        using (automation.EnterOwner(owner))
            await automation.StopAsync(launched.SessionId, terminate: false, default);
        Assert.Equal(1, debugger.DetachCount);
        Assert.False(workspace.IsMcpControlled);
    }

    [Fact]
    public async Task Wait_observes_a_stop_without_polling_and_rejects_concurrent_waits()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger,
            workspace,
            new McpServerSettings());
        var owner = Guid.NewGuid();
        McpDebugStatus launched;
        using (automation.EnterOwner(owner))
            launched = await automation.LaunchAsync("sample.dll", [], null, false, [], default);

        Task<McpDebugStatus> wait;
        using (automation.EnterOwner(owner))
            wait = automation.WaitForStopAsync(launched.SessionId, 5_000, default);
        await Task.Yield();
        using (automation.EnterOwner(owner))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                automation.WaitForStopAsync(launched.SessionId, 5_000, default));

        debugger.Publish(DebugSessionStatus.Paused, new(DebugStopReason.Pause, new(3)));
        Assert.Equal(DebugSessionStatus.Paused, (await wait).Status);
    }

    [Fact]
    public async Task Variables_expand_objects_by_evaluating_missing_references()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger, workspace, new McpServerSettings());
        var owner = Guid.NewGuid();
        McpDebugStatus launched;
        using (automation.EnterOwner(owner))
            launched = await automation.LaunchAsync("sample.dll", [], null, false, [], default);
        debugger.Publish(DebugSessionStatus.Paused, new(DebugStopReason.Breakpoint, new(7)));
        McpDebugStatus paused;
        using (automation.EnterOwner(owner)) paused = automation.Status(launched.SessionId);

        IReadOnlyList<McpDebugScopeVariables> scopes;
        using (automation.EnterOwner(owner))
            scopes = await automation.VariablesAsync(
                launched.SessionId, paused.StopGeneration, new(9), 20, 2, default);

        var instance = Assert.Single(Assert.Single(scopes).Variables);
        Assert.Equal("this", instance.Name);
        Assert.Equal(12, instance.VariablesReference);
        Assert.Equal("field", Assert.Single(instance.Children).Name);
    }

    [Fact]
    public async Task Mcp_breakpoints_are_visible_and_removed_with_the_session()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger, workspace, new McpServerSettings());
        var owner = Guid.NewGuid();
        var breakpoint = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(new DebugMethodId(Guid.NewGuid(), 0x06000001), 7));
        McpDebugStatus launched;
        using (automation.EnterOwner(owner))
            launched = await automation.LaunchAsync(
                "sample.dll", [], null, false, [breakpoint], default);

        Assert.Equal(breakpoint, Assert.Single(workspace.Breakpoints));
        Assert.True(Assert.Single(workspace.Bindings).IsVerified);

        using (automation.EnterOwner(owner))
            await automation.StopAsync(launched.SessionId, terminate: true, default);

        Assert.Empty(workspace.Breakpoints);
        Assert.Empty(workspace.Bindings);
    }

    private sealed class FakeDebuggerService : IDebuggerService
    {
        public DebugSessionSnapshot Snapshot { get; private set; } = DebugSessionSnapshot.Initial;
        public int DetachCount { get; private set; }
        public IReadOnlyList<DebugBreakpointBinding> Breakpoints { get; private set; } = [];
        public event Action<DebugSessionSnapshot>? StateChanged;
        public event Action<IReadOnlyList<DebugBreakpointBinding>>? BreakpointsChanged;
        public event Action<DebugOutputMessage>? OutputReceived { add { } remove { } }

        public Task StartAsync(DebugStartRequest request, CancellationToken cancellationToken = default)
        {
            Snapshot = new(
                Guid.NewGuid(),
                request.Runtime,
                DebugSessionStatus.Running,
                123,
                new(true, true, false, true, true, false, false, true, true),
                null,
                null);
            StateChanged?.Invoke(Snapshot);
            return request.InitialBreakpoints is { } breakpoints
                ? SetBreakpointsAsync(breakpoints, cancellationToken)
                : Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            Publish(DebugSessionStatus.Terminated);
            return Task.CompletedTask;
        }

        public Task DetachAsync(CancellationToken cancellationToken = default)
        {
            DetachCount++;
            Publish(DebugSessionStatus.Terminated);
            return Task.CompletedTask;
        }

        public Task ContinueAsync(CancellationToken cancellationToken = default)
        {
            Publish(DebugSessionStatus.Running);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepAsync(DebugThreadId thread, DebugStepKind kind, CancellationToken cancellationToken = default) => ContinueAsync(cancellationToken);

        public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
            IReadOnlyList<DebugBreakpoint> breakpoints,
            CancellationToken cancellationToken = default)
        {
            Breakpoints = breakpoints.Select(value => new DebugBreakpointBinding(value.Id, true, value.Location)).ToArray();
            BreakpointsChanged?.Invoke(Breakpoints);
            return Task.FromResult(Breakpoints);
        }

        public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DebugThread>>([new(new(7), "Main", true)]);

        public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(DebugThreadId thread, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DebugStackFrame>>([new(new(9), thread, "Program.Main", null)]);

        public Task<IReadOnlyList<DebugScope>> GetScopesAsync(DebugFrameId frame, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DebugScope>>([new("Locals", new(11))]);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(DebugVariableReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DebugVariable>>(reference.Value == 12
                ? [new("field", "42", "int", default, "this.field")]
                : [new("this", "{Sample}", "Sample", default, "this")]);

        public Task<DebugEvaluationResult> EvaluateAsync(string expression, DebugFrameId? frame = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(expression == "this"
                ? new DebugEvaluationResult("{Sample}", "Sample", new(12))
                : new DebugEvaluationResult("42", "int", default));

        public void Publish(DebugSessionStatus status, DebugStopInfo? stop = null)
        {
            Snapshot = Snapshot with { Status = status, Stop = stop };
            StateChanged?.Invoke(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
