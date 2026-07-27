using DnSpyXDX.Application;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class MonoSoftDebuggerEngineTests
{
    [Fact]
    public async Task Provider_exposes_direct_attach_capabilities()
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var provider = new MonoSoftDebuggerEngineProvider(
            new MonoSoftDebuggerOptions(TimeSpan.FromSeconds(3)),
            factory);
        var breakpoint = CreateBreakpoint();

        await using var engine = await provider.CreateAsync(default);
        var result = await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.Mono,
                ProcessId: 42,
                Host: " 127.0.0.1 ",
                Port: 55555)
            {
                InitialBreakpoints = [breakpoint]
            },
            default);

        Assert.Equal(DebugRuntimeKind.Mono, provider.Runtime);
        Assert.True(provider.IsAvailable);
        Assert.Null(provider.UnavailableReason);
        Assert.Equal("127.0.0.1", factory.Host);
        Assert.Equal(55555, factory.Port);
        Assert.Equal(TimeSpan.FromSeconds(3), factory.Timeout);
        Assert.Equal(42, result.ProcessId);
        Assert.False(result.Capabilities.SupportsLaunch);
        Assert.True(result.Capabilities.SupportsAttach);
        Assert.True(result.Capabilities.SupportsDecompiledCodeBreakpoints);
        Assert.False(result.Capabilities.SupportsEvaluate);
        Assert.Equal(breakpoint, Assert.Single(session.StartBreakpoints));
    }

    [Fact]
    public async Task Engine_forwards_events_and_delegates_commands()
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var provider = new MonoSoftDebuggerEngineProvider(
            new MonoSoftDebuggerOptions(),
            factory);
        var events = new List<DebugEngineEvent>();

        await using var engine = await provider.CreateAsync(default);
        engine.EventReceived += events.Add;
        await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.Mono,
                Host: "localhost",
                Port: 12345),
            default);

        var stop = new DebugStopInfo(
            DebugStopReason.Breakpoint,
            new DebugThreadId(7));
        session.Publish(new DebugEngineStopped(stop));
        await engine.PauseAsync(default);
        await engine.ContinueAsync(default);
        await engine.StepAsync(
            new DebugThreadId(7),
            DebugStepKind.Over,
            default);
        var breakpoint = CreateBreakpoint();
        var bindings = await engine.SetBreakpointsAsync([breakpoint], default);
        var threads = await engine.GetThreadsAsync(default);
        var frames = await engine.GetStackTraceAsync(new DebugThreadId(7), default);
        var scopes = await engine.GetScopesAsync(new DebugFrameId(9), default);
        var variables = await engine.GetVariablesAsync(
            new DebugVariableReference(11),
            default);
        await engine.TerminateAsync(default);

        Assert.Equal(stop, Assert.IsType<DebugEngineStopped>(Assert.Single(events)).Stop);
        Assert.Equal(1, session.PauseCount);
        Assert.Equal(1, session.ContinueCount);
        Assert.Equal((new DebugThreadId(7), DebugStepKind.Over), session.LastStep);
        Assert.Equal(breakpoint.Location, Assert.Single(bindings).BoundLocation);
        Assert.Single(threads);
        Assert.Single(frames);
        Assert.Single(scopes);
        Assert.Single(variables);
        Assert.Equal(1, session.DetachCount);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.EvaluateAsync("value", null, default));
    }

    [Theory]
    [InlineData(null, 55555)]
    [InlineData("", 55555)]
    [InlineData("localhost", null)]
    [InlineData("localhost", 0)]
    [InlineData("localhost", 65536)]
    public async Task Attach_rejects_invalid_endpoint(string? host, int? port)
    {
        var provider = new MonoSoftDebuggerEngineProvider(
            new MonoSoftDebuggerOptions(),
            new FakeSessionFactory(new FakeSession()));
        await using var engine = await provider.CreateAsync(default);

        await Assert.ThrowsAsync<ArgumentException>(
            () => engine.StartAsync(
                new DebugAttachRequest(
                    DebugRuntimeKind.Mono,
                    Host: host,
                    Port: port),
                default));
    }

    [Fact]
    public async Task Engine_rejects_launch_and_non_mono_runtime()
    {
        var provider = new MonoSoftDebuggerEngineProvider(
            new MonoSoftDebuggerOptions(),
            new FakeSessionFactory(new FakeSession()));
        await using var launchEngine = await provider.CreateAsync(default);
        await using var unityEngine = await provider.CreateAsync(default);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => launchEngine.StartAsync(
                new DebugLaunchRequest(DebugRuntimeKind.Mono, "app.exe"),
                default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => unityEngine.StartAsync(
                new DebugAttachRequest(
                    DebugRuntimeKind.UnityMono,
                    Host: "localhost",
                    Port: 55555),
                default));
    }

    private static DebugBreakpoint CreateBreakpoint() =>
        new(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000001),
                4));

    private sealed class FakeSessionFactory(FakeSession session)
        : IMonoSoftDebuggerSessionFactory
    {
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public TimeSpan Timeout { get; private set; }

        public Task<IMonoSoftDebuggerSession> ConnectAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Host = host;
            Port = port;
            Timeout = timeout;
            return Task.FromResult<IMonoSoftDebuggerSession>(session);
        }
    }

    private sealed class FakeSession : IMonoSoftDebuggerSession
    {
        public event Action<DebugEngineEvent>? EventReceived;
        public IReadOnlyList<DebugBreakpoint> StartBreakpoints { get; private set; } = [];
        public int DetachCount { get; private set; }
        public int ContinueCount { get; private set; }
        public int PauseCount { get; private set; }
        public (DebugThreadId Thread, DebugStepKind Kind)? LastStep { get; private set; }

        public Task<MonoSoftDebuggerSessionStart> StartAsync(
            IReadOnlyList<DebugBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            StartBreakpoints = breakpoints;
            return Task.FromResult(new MonoSoftDebuggerSessionStart(
                false,
                null,
                breakpoints.Select(Bind).ToArray()));
        }

        public Task DetachAsync(CancellationToken cancellationToken)
        {
            DetachCount++;
            return Task.CompletedTask;
        }

        public Task ContinueAsync(CancellationToken cancellationToken)
        {
            ContinueCount++;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task StepAsync(
            DebugThreadId thread,
            DebugStepKind kind,
            CancellationToken cancellationToken)
        {
            LastStep = (thread, kind);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
            IReadOnlyList<DebugBreakpoint> breakpoints,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugBreakpointBinding>>(
                breakpoints.Select(Bind).ToArray());

        public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugThread>>(
                [new(new DebugThreadId(7), "Main", true)]);

        public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
            DebugThreadId thread,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugStackFrame>>(
                [new(new DebugFrameId(9), thread, "Run", null)]);

        public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
            DebugFrameId frame,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugScope>>(
                [new("Locals", new DebugVariableReference(11))]);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
            DebugVariableReference reference,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DebugVariable>>(
                [new("value", "1", "System.Int32", default)]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(DebugEngineEvent value) => EventReceived?.Invoke(value);

        private static DebugBreakpointBinding Bind(DebugBreakpoint breakpoint) =>
            new(breakpoint.Id, true, breakpoint.Location);
    }
}
