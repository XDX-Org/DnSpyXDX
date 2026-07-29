using DnSpyXDX.Application;
using DnSpyXDX.Host.Mcp;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerAutomationServiceTests
{
    [Fact]
    public async Task Session_is_owned_by_one_mcp_connection()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger, workspace, new McpServerSettings());
        var firstOwner = Guid.NewGuid();
        McpDebugStatus started;
        using (automation.EnterOwner(firstOwner))
            started = await automation.LaunchAsync(
                "/allowed/app.dll", null, null, false, [], default);
        Assert.True(workspace.IsMcpControlled);

        using (automation.EnterOwner(Guid.NewGuid()))
            Assert.Throws<UnauthorizedAccessException>(() =>
                automation.Status(started.SessionId));
        using (automation.EnterOwner(firstOwner))
            Assert.Equal(started.SessionId, automation.Status(started.SessionId).SessionId);
        using (automation.EnterOwner(firstOwner))
            await automation.StopAsync(started.SessionId, terminate: true, default);
        Assert.False(workspace.IsMcpControlled);
    }

    [Fact]
    public async Task Resuming_invalidates_mcp_stop_generation()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        using var automation = new DebuggerAutomationService(
            debugger, workspace, new McpServerSettings());
        using var owner = automation.EnterOwner(Guid.NewGuid());
        var started = await automation.LaunchAsync(
            "/allowed/app.dll", null, null, false, [], default);
        debugger.Publish(DebugSessionStatus.Paused);
        var paused = automation.Status(started.SessionId);
        debugger.Publish(DebugSessionStatus.Running);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            automation.StackAsync(
                started.SessionId,
                paused.StopGeneration,
                new DebugThreadId(1),
                default));
        Assert.StartsWith("stale_reference:", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeDebuggerService : IDebuggerService
    {
        public DebugSessionSnapshot Snapshot { get; private set; } = DebugSessionSnapshot.Initial;
        public IReadOnlyList<DebugBreakpointBinding> Breakpoints => [];
        public event Action<DebugSessionSnapshot>? StateChanged;
        public event Action<IReadOnlyList<DebugBreakpointBinding>>? BreakpointsChanged
        {
            add { }
            remove { }
        }
        public event Action<DebugOutputMessage>? OutputReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(DebugStartRequest request, CancellationToken cancellationToken = default)
        {
            Snapshot = new(
                Guid.NewGuid(), request.Runtime, DebugSessionStatus.Running, 123,
                DebuggerCapabilitySets.None, null, null);
            StateChanged?.Invoke(Snapshot);
            return Task.CompletedTask;
        }

        public void Publish(DebugSessionStatus status)
        {
            Snapshot = Snapshot with
            {
                Status = status,
                Stop = status == DebugSessionStatus.Paused
                    ? new(DebugStopReason.Breakpoint, new DebugThreadId(1))
                    : null
            };
            StateChanged?.Invoke(Snapshot);
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContinueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepAsync(DebugThreadId thread, DebugStepKind kind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DebugBreakpointBinding>>([]);
        public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DebugThread>>([]);
        public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(DebugThreadId thread, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DebugStackFrame>>([]);
        public Task<IReadOnlyList<DebugScope>> GetScopesAsync(DebugFrameId frame, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DebugScope>>([]);
        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(DebugVariableReference reference, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DebugVariable>>([]);
        public Task<DebugEvaluationResult> EvaluateAsync(string expression, DebugFrameId? frame = null, CancellationToken cancellationToken = default) => Task.FromResult(new DebugEvaluationResult("", null, default));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
