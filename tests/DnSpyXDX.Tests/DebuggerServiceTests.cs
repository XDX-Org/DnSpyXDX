using DnSpyXDX.Application;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerServiceTests
{
    [Fact]
    public async Task Start_selects_runtime_engine_and_enters_running_state()
    {
        var engine = new FakeEngine();
        await using var debugger = CreateDebugger(DebugRuntimeKind.CoreClr, engine);

        await debugger.StartAsync(new DebugLaunchRequest(
            DebugRuntimeKind.CoreClr,
            "sample.dll"));

        Assert.Equal(DebugSessionStatus.Running, debugger.Snapshot.Status);
        Assert.Equal(DebugRuntimeKind.CoreClr, debugger.Snapshot.Runtime);
        Assert.Equal(1234, debugger.Snapshot.ProcessId);
        Assert.True(debugger.Snapshot.Capabilities.SupportsDecompiledCodeBreakpoints);
        Assert.IsType<DebugLaunchRequest>(engine.StartRequest);
    }

    [Fact]
    public async Task Engine_stop_event_exposes_runtime_location_and_allows_continue()
    {
        var engine = new FakeEngine();
        await using var debugger = CreateDebugger(DebugRuntimeKind.CoreClr, engine);
        await debugger.StartAsync(new DebugLaunchRequest(
            DebugRuntimeKind.CoreClr,
            "sample.dll"));
        var location = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000001),
            12);

        engine.Publish(new DebugEngineStopped(new(
            DebugStopReason.Breakpoint,
            new DebugThreadId(7),
            location)));

        Assert.Equal(DebugSessionStatus.Paused, debugger.Snapshot.Status);
        Assert.Equal(location, debugger.Snapshot.Stop!.Location);

        await debugger.ContinueAsync();

        Assert.Equal(1, engine.ContinueCount);
        Assert.Equal(DebugSessionStatus.Running, debugger.Snapshot.Status);
        Assert.Null(debugger.Snapshot.Stop);
    }

    [Fact]
    public async Task Stop_event_during_startup_is_not_overwritten_by_start_result()
    {
        var engine = new FakeEngine
        {
            EventDuringStart = new DebugEngineStopped(new(
                DebugStopReason.Entry,
                new DebugThreadId(3)))
        };
        await using var debugger = CreateDebugger(DebugRuntimeKind.Mono, engine);

        await debugger.StartAsync(new DebugAttachRequest(
            DebugRuntimeKind.Mono,
            Host: "localhost",
            Port: 55555));

        Assert.Equal(DebugSessionStatus.Paused, debugger.Snapshot.Status);
        Assert.Equal(DebugStopReason.Entry, debugger.Snapshot.Stop?.Reason);
        Assert.Equal(1234, debugger.Snapshot.ProcessId);
    }

    [Fact]
    public async Task Stack_and_variables_require_paused_session()
    {
        var engine = new FakeEngine();
        await using var debugger = CreateDebugger(DebugRuntimeKind.UnityMono, engine);
        await debugger.StartAsync(new DebugAttachRequest(
            DebugRuntimeKind.UnityMono,
            Host: "127.0.0.1",
            Port: 56000));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => debugger.GetStackTraceAsync(new DebugThreadId(1)));

        Assert.Contains("Running", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Breakpoint_bindings_are_kept_as_session_state()
    {
        var engine = new FakeEngine();
        await using var debugger = CreateDebugger(DebugRuntimeKind.Mono, engine);
        await debugger.StartAsync(new DebugAttachRequest(
            DebugRuntimeKind.Mono,
            Host: "localhost",
            Port: 12345));
        var breakpoint = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000002),
                4));

        var bindings = await debugger.SetBreakpointsAsync([breakpoint]);

        var binding = Assert.Single(bindings);
        Assert.True(binding.IsVerified);
        Assert.Equal(breakpoint.Location, binding.BoundLocation);
        Assert.Same(bindings, debugger.Breakpoints);
    }

    [Fact]
    public async Task Unregistered_runtime_fails_explicitly()
    {
        await using var debugger = new DebuggerService(new DebuggerEngineRegistry([]));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => debugger.StartAsync(new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                "sample.dll")));

        Assert.Contains("CoreClr", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DebugSessionStatus.Faulted, debugger.Snapshot.Status);
    }

    [Fact]
    public async Task Termination_disposes_engine_and_ignores_late_events()
    {
        var engine = new FakeEngine();
        await using var debugger = CreateDebugger(DebugRuntimeKind.CoreClr, engine);
        await debugger.StartAsync(new DebugLaunchRequest(
            DebugRuntimeKind.CoreClr,
            "sample.dll"));

        await debugger.TerminateAsync();
        engine.Publish(new DebugEngineStopped(new(
            DebugStopReason.Pause,
            new DebugThreadId(1))));

        Assert.Equal(DebugSessionStatus.Terminated, debugger.Snapshot.Status);
        Assert.True(engine.IsDisposed);
    }

    [Fact]
    public void Debug_document_map_resolves_both_directions()
    {
        var method = new DebugMethodId(Guid.NewGuid(), 0x06000001);
        var point = new DebugDocumentSequencePoint(
            10,
            20,
            new DebugCodeLocation(method, 4),
            12);
        var map = new DebugDocumentMap(
            new SymbolId(method.ModuleMvid, method.MetadataToken),
            [point]);

        Assert.Equal(point, map.FindByDocumentOffset(15));
        Assert.Null(map.FindByDocumentOffset(30));
        Assert.Equal(point, map.FindByRuntimeLocation(new DebugCodeLocation(method, 8)));
        Assert.Null(map.FindByRuntimeLocation(new DebugCodeLocation(method, 12)));
    }

    private static DebuggerService CreateDebugger(
        DebugRuntimeKind runtime,
        FakeEngine engine) =>
        new(new DebuggerEngineRegistry([new FakeProvider(runtime, engine)]));

    private sealed class FakeProvider(
        DebugRuntimeKind runtime,
        FakeEngine engine) : IDebuggerEngineProvider
    {
        public DebugRuntimeKind Runtime { get; } = runtime;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public ValueTask<IDebuggerEngine> CreateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDebuggerEngine>(engine);
    }

    private sealed class FakeEngine : IDebuggerEngine
    {
        private static DebuggerCapabilities Capabilities { get; } = new(
            SupportsLaunch: true,
            SupportsAttach: true,
            SupportsFunctionBreakpoints: true,
            SupportsConditionalBreakpoints: true,
            SupportsHitConditions: true,
            SupportsExceptionBreakpoints: true,
            SupportsSetVariable: true,
            SupportsEvaluate: true,
            SupportsDecompiledCodeBreakpoints: true);

        public event Action<DebugEngineEvent>? EventReceived;
        public DebugStartRequest? StartRequest { get; private set; }
        public int ContinueCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public DebugEngineEvent? EventDuringStart { get; init; }

        public Task<DebugEngineStartResult> StartAsync(
            DebugStartRequest request,
            CancellationToken cancellationToken)
        {
            StartRequest = request;
            if (EventDuringStart is { } value)
                Publish(value);
            return Task.FromResult(new DebugEngineStartResult(1234, Capabilities));
        }

        public Task TerminateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ContinueAsync(CancellationToken cancellationToken)
        {
            ContinueCount++;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StepAsync(
            DebugThreadId thread,
            DebugStepKind kind,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
            IReadOnlyList<DebugBreakpoint> breakpoints,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugBreakpointBinding>>(
                breakpoints.Select(breakpoint => new DebugBreakpointBinding(
                    breakpoint.Id,
                    true,
                    breakpoint.Location)).ToArray());

        public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugThread>>([]);

        public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
            DebugThreadId thread,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugStackFrame>>([]);

        public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
            DebugFrameId frame,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugScope>>([]);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
            DebugVariableReference reference,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugVariable>>([]);

        public Task<DebugEvaluationResult> EvaluateAsync(
            string expression,
            DebugFrameId? frame,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DebugEvaluationResult(expression, "string", default));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Publish(DebugEngineEvent value) => EventReceived?.Invoke(value);
    }
}
