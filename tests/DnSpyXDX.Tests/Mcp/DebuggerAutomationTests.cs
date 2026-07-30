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
            return Task.CompletedTask;
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
            Task.FromResult<IReadOnlyList<DebugVariable>>([new("value", "42", "int", default)]);

        public Task<DebugEvaluationResult> EvaluateAsync(string expression, DebugFrameId? frame = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DebugEvaluationResult("42", "int", default));

        public void Publish(DebugSessionStatus status, DebugStopInfo? stop = null)
        {
            Snapshot = Snapshot with { Status = status, Stop = stop };
            StateChanged?.Invoke(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
