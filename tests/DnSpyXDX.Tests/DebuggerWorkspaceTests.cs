using DnSpyXDX.Application;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerWorkspaceTests
{
    [Fact]
    public async Task Mcp_control_blocks_ui_session_commands()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        workspace.SetMcpControl(true);

        await workspace.LaunchAsync("sample.dll", [], stopAtEntry: false);

        Assert.Equal(0, debugger.StartCount);
        Assert.Equal("This debug session is controlled by an MCP client.", workspace.Error);
    }

    [Fact]
    public async Task Breakpoint_added_before_launch_is_pending_then_synchronized()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var location = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000001),
            4);

        await workspace.ToggleBreakpointAsync(location);

        var pending = Assert.Single(workspace.Bindings);
        Assert.False(pending.IsVerified);
        Assert.Empty(debugger.LastBreakpoints);

        await workspace.LaunchAsync("sample.dll", [], stopAtEntry: false);

        Assert.Equal(DebugSessionStatus.Running, workspace.Snapshot.Status);
        Assert.Equal(location, Assert.Single(debugger.LastBreakpoints).Location);
        Assert.True(Assert.Single(workspace.Bindings).IsVerified);
        Assert.Equal(
            "sample.dll",
            Assert.IsType<DebugLaunchRequest>(
                workspace.StartRequest).ExecutablePath);
    }

    [Fact]
    public async Task Source_map_selects_executable_line_and_current_statement()
    {
        var mvid = Guid.NewGuid();
        var document = new DecompilerDocument(
            new SymbolId(mvid, 0x02000001),
            "Sample",
            "csharp",
            "class C\n{\n    void M() { }\n}\n",
            [],
            []);
        var model = SourceDocumentModel.Create(document);
        var statementOffset = document.Text.IndexOf("void", StringComparison.Ordinal);
        var location = new DebugCodeLocation(
            new DebugMethodId(mvid, 0x06000001),
            0);
        var point = new DebugDocumentSequencePoint(
            statementOffset,
            12,
            location,
            8);
        var map = new DebugDocumentMap(document.Symbol, [point]);

        Assert.Null(DebuggerSourceMap.FindForLine(map, model, 1));
        Assert.Equal(point, DebuggerSourceMap.FindForLine(map, model, 2));
        Assert.Equal(2, DebuggerSourceMap.FindLine(map, model, location));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Mono_attach_uses_host_port_and_preserves_breakpoints()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var location = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000001),
            8);
        await workspace.ToggleBreakpointAsync(location);

        await workspace.AttachMonoAsync("127.0.0.1", 55555);

        var request = Assert.IsType<DebugAttachRequest>(debugger.LastStartRequest);
        Assert.Equal(DebugRuntimeKind.Mono, request.Runtime);
        Assert.Equal("127.0.0.1", request.Host);
        Assert.Equal(55555, request.Port);
        Assert.Equal(location, Assert.Single(request.InitialBreakpoints!).Location);
    }

    [Fact]
    public async Task Paused_workspace_selects_threads_frames_and_expandable_variables()
    {
        var firstThread = new DebugThread(new DebugThreadId(1), "Worker", true);
        var stoppedThread = new DebugThread(new DebugThreadId(2), "Main", true);
        var firstFrame = new DebugStackFrame(
            new DebugFrameId(10),
            firstThread.Id,
            "Worker.Run",
            null);
        var stoppedFrame = new DebugStackFrame(
            new DebugFrameId(20),
            stoppedThread.Id,
            "Program.Main",
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000001),
                0));
        var rootReference = new DebugVariableReference(100);
        var childReference = new DebugVariableReference(101);
        var root = new DebugVariable(
            "items",
            "System.Int32[1]",
            "System.Int32[]",
            childReference);
        var debugger = new FakeDebuggerService
        {
            ThreadResults = [firstThread, stoppedThread]
        };
        debugger.FrameResults[firstThread.Id] = [firstFrame];
        debugger.FrameResults[stoppedThread.Id] = [stoppedFrame];
        debugger.ScopeResults[firstFrame.Id] =
            [new DebugScope("Locals", rootReference)];
        debugger.ScopeResults[stoppedFrame.Id] =
            [new DebugScope("Locals", rootReference)];
        debugger.VariableResults[rootReference] = [root];
        debugger.VariableResults[childReference] =
            [new DebugVariable("[0]", "42", "System.Int32", default)];
        using var workspace = new DebuggerWorkspace(debugger);

        debugger.PublishPaused(stoppedThread.Id);

        Assert.Equal(stoppedThread.Id, workspace.SelectedThread);
        Assert.Equal(stoppedFrame, Assert.Single(workspace.Frames));
        Assert.Equal(root, Assert.Single(workspace.Variables));
        Assert.Equal(stoppedFrame.Location, workspace.CurrentLocation);

        await workspace.ToggleVariableAsync(root);

        Assert.True(workspace.TryGetVariableChildren(
            childReference,
            out var children));
        Assert.Equal("42", Assert.Single(children).Value);

        await workspace.ToggleVariableAsync(root);

        Assert.False(workspace.TryGetVariableChildren(
            childReference,
            out _));

        await workspace.ToggleVariableAsync(root);
        await workspace.SelectThreadAsync(firstThread);

        Assert.Equal(firstThread.Id, workspace.SelectedThread);
        Assert.Equal(firstFrame, Assert.Single(workspace.Frames));
        Assert.False(workspace.TryGetVariableChildren(
            childReference,
            out _));
    }

    [Fact]
    public void Empty_scope_does_not_request_reserved_zero_variable_reference()
    {
        var thread = new DebugThread(new DebugThreadId(2), "Main", true);
        var frame = new DebugStackFrame(
            new DebugFrameId(20),
            thread.Id,
            "Program.Main",
            null);
        var debugger = new FakeDebuggerService
        {
            ThreadResults = [thread]
        };
        debugger.FrameResults[thread.Id] = [frame];
        debugger.ScopeResults[frame.Id] =
            [new DebugScope("Locals", default)];
        using var workspace = new DebuggerWorkspace(debugger);

        debugger.PublishPaused(thread.Id);

        Assert.Empty(debugger.RequestedVariableReferences);
        Assert.Empty(workspace.Variables);
        Assert.Null(workspace.Error);
    }

    [Fact]
    public async Task Breakpoints_can_be_disabled_and_removed_from_panel()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var location = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000001),
            8);
        await workspace.ToggleBreakpointAsync(location);
        await workspace.LaunchAsync("sample.dll", [], stopAtEntry: false);
        var breakpoint = Assert.Single(workspace.Breakpoints);

        await workspace.SetBreakpointEnabledAsync(
            breakpoint.Id,
            enabled: false);

        Assert.False(Assert.Single(workspace.Breakpoints).Enabled);
        Assert.False(Assert.Single(debugger.LastBreakpoints).Enabled);

        await workspace.RemoveBreakpointAsync(breakpoint.Id);

        Assert.Empty(workspace.Breakpoints);
        Assert.Empty(debugger.LastBreakpoints);
    }

    [Fact]
    public async Task Restart_reuses_request_and_current_breakpoints()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var first = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000001),
            4);
        var second = new DebugCodeLocation(
            new DebugMethodId(Guid.NewGuid(), 0x06000002),
            8);
        await workspace.ToggleBreakpointAsync(first);
        await workspace.LaunchAsync(
            "sample.dll",
            ["--mode", "debug"],
            stopAtEntry: true);
        await workspace.ToggleBreakpointAsync(second);

        await workspace.RestartAsync();

        Assert.Equal(2, debugger.StartCount);
        Assert.Equal(1, debugger.TerminateCount);
        var request = Assert.IsType<DebugLaunchRequest>(
            debugger.LastStartRequest);
        Assert.Equal("sample.dll", request.ExecutablePath);
        Assert.Equal(["--mode", "debug"], request.Arguments);
        Assert.True(request.StopAtEntry);
        Assert.Equal(
            [first, second],
            request.InitialBreakpoints!
                .Select(value => value.Location)
                .ToArray());
    }

    [Fact]
    public async Task Restart_without_previous_request_reports_error()
    {
        using var workspace = new DebuggerWorkspace(
            new FakeDebuggerService());

        await workspace.RestartAsync();

        Assert.Equal(
            "No previous debug launch or attach is available.",
            workspace.Error);
    }

    [Fact]
    public async Task Launch_forwards_working_directory_and_environment()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development"
        };

        await workspace.LaunchAsync(
            "sample.dll",
            ["--verbose"],
            stopAtEntry: false,
            "work",
            environment);

        var request = Assert.IsType<DebugLaunchRequest>(
            debugger.LastStartRequest);
        Assert.Equal("work", request.WorkingDirectory);
        Assert.Equal(
            "Development",
            request.Environment!["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public async Task Scope_groups_and_watches_refresh_for_selected_frame()
    {
        var thread = new DebugThread(new DebugThreadId(2), "Main", true);
        var frame = new DebugStackFrame(
            new DebugFrameId(20),
            thread.Id,
            "Program.Main",
            null);
        var argumentsReference = new DebugVariableReference(100);
        var localsReference = new DebugVariableReference(101);
        var debugger = new FakeDebuggerService
        {
            ThreadResults = [thread]
        };
        debugger.FrameResults[thread.Id] = [frame];
        debugger.ScopeResults[frame.Id] =
        [
            new DebugScope("Arguments", argumentsReference),
            new DebugScope("Locals", localsReference)
        ];
        debugger.VariableResults[argumentsReference] =
            [new DebugVariable("args", "string[0]", "string[]", default)];
        debugger.VariableResults[localsReference] =
            [new DebugVariable("count", "41", "int", default, "count")];
        debugger.EvaluationResults["count"] =
            new DebugEvaluationResult("41", "int", default);
        using var workspace = new DebuggerWorkspace(debugger);
        workspace.RestorePersistentState([], ["count"]);

        debugger.PublishPaused(thread.Id);

        Assert.Equal(
            ["Arguments", "Locals"],
            workspace.ScopeGroups.Select(value => value.Name));
        Assert.Equal(
            ["args", "count"],
            workspace.Variables.Select(value => value.Name));
        Assert.Equal("41", Assert.Single(workspace.Watches).Result?.Value);
        Assert.Equal(
            [(Expression: "count", Frame: (DebugFrameId?)frame.Id)],
            debugger.Evaluations);

        debugger.EvaluationResults["count"] =
            new DebugEvaluationResult("42", "int", default);
        await workspace.SelectFrameAsync(frame);

        Assert.Equal("42", Assert.Single(workspace.Watches).Result?.Value);

        debugger.PublishRunning();

        var persisted = Assert.Single(workspace.Watches);
        Assert.Equal("count", persisted.Expression);
        Assert.Null(persisted.Result);
        Assert.Empty(workspace.ScopeGroups);
    }

    [Fact]
    public async Task Breakpoint_editor_bulk_actions_and_restore_preserve_state()
    {
        var debugger = new FakeDebuggerService();
        using var workspace = new DebuggerWorkspace(debugger);
        var first = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000001),
                4));
        var second = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000002),
                8));
        var persistentChanges = 0;
        workspace.PersistentStateChanged += () => persistentChanges++;
        workspace.RestorePersistentState(
            [first, second],
            ["count", "name", "count"]);

        await workspace.UpdateBreakpointAsync(
            first.Id,
            "count > 0",
            "5",
            "count={count}");
        await workspace.SetAllBreakpointsEnabledAsync(false);

        var updated = workspace.Breakpoints.Single(value =>
            value.Id == first.Id);
        Assert.Equal("count > 0", updated.Condition);
        Assert.Equal("5", updated.HitCondition);
        Assert.Equal("count={count}", updated.LogMessage);
        Assert.All(workspace.Breakpoints, value => Assert.False(value.Enabled));
        Assert.Equal(["count", "name"], workspace.Watches.Select(value =>
            value.Expression));

        await workspace.RemoveAllBreakpointsAsync();

        Assert.Empty(workspace.Breakpoints);
        Assert.Equal(3, persistentChanges);
    }

    private sealed class FakeDebuggerService : IDebuggerService
    {
        public DebugSessionSnapshot Snapshot { get; private set; } =
            DebugSessionSnapshot.Initial;
        public IReadOnlyList<DebugBreakpointBinding> Breakpoints { get; private set; } = [];
        public IReadOnlyList<DebugBreakpoint> LastBreakpoints { get; private set; } = [];
        public DebugStartRequest? LastStartRequest { get; private set; }
        public int StartCount { get; private set; }
        public int TerminateCount { get; private set; }
        public IReadOnlyList<DebugThread> ThreadResults { get; init; } = [];
        public Dictionary<DebugThreadId, IReadOnlyList<DebugStackFrame>>
            FrameResults { get; } = [];
        public Dictionary<DebugFrameId, IReadOnlyList<DebugScope>>
            ScopeResults { get; } = [];
        public Dictionary<DebugVariableReference, IReadOnlyList<DebugVariable>>
            VariableResults { get; } = [];
        public Dictionary<string, DebugEvaluationResult>
            EvaluationResults { get; } = [];
        public List<(string Expression, DebugFrameId? Frame)>
            Evaluations { get; } = [];
        public List<DebugVariableReference> RequestedVariableReferences { get; } = [];

        public event Action<DebugSessionSnapshot>? StateChanged;
        public event Action<IReadOnlyList<DebugBreakpointBinding>>? BreakpointsChanged;
        public event Action<DebugOutputMessage>? OutputReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(
            DebugStartRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastStartRequest = request;
            Snapshot = new DebugSessionSnapshot(
                Guid.NewGuid(),
                request.Runtime,
                DebugSessionStatus.Running,
                1234,
                new DebuggerCapabilities(
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true),
                null,
                null);
            StateChanged?.Invoke(Snapshot);
            return request.InitialBreakpoints is { } breakpoints
                ? SetBreakpointsAsync(breakpoints, cancellationToken)
                : Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            TerminateCount++;
            Snapshot = Snapshot with
            {
                Status = DebugSessionStatus.Terminated,
                Stop = null
            };
            StateChanged?.Invoke(Snapshot);
            return Task.CompletedTask;
        }

        public Task DetachAsync(CancellationToken cancellationToken = default) =>
            TerminateAsync(cancellationToken);

        public Task ContinueAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StepAsync(
            DebugThreadId thread,
            DebugStepKind kind,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
            IReadOnlyList<DebugBreakpoint> breakpoints,
            CancellationToken cancellationToken = default)
        {
            LastBreakpoints = breakpoints;
            Breakpoints = breakpoints.Select(value => new DebugBreakpointBinding(
                value.Id,
                true,
                value.Location)).ToArray();
            BreakpointsChanged?.Invoke(Breakpoints);
            return Task.FromResult(Breakpoints);
        }

        public Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ThreadResults);

        public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
            DebugThreadId thread,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                FrameResults.GetValueOrDefault(thread) ??
                (IReadOnlyList<DebugStackFrame>)[]);

        public Task<IReadOnlyList<DebugScope>> GetScopesAsync(
            DebugFrameId frame,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                ScopeResults.GetValueOrDefault(frame) ??
                (IReadOnlyList<DebugScope>)[]);

        public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
            DebugVariableReference reference,
            CancellationToken cancellationToken = default)
        {
            RequestedVariableReferences.Add(reference);
            return Task.FromResult(
                VariableResults.GetValueOrDefault(reference) ??
                (IReadOnlyList<DebugVariable>)[]);
        }

        public Task<DebugEvaluationResult> EvaluateAsync(
            string expression,
            DebugFrameId? frame = null,
            CancellationToken cancellationToken = default)
        {
            Evaluations.Add((expression, frame));
            return Task.FromResult(
                EvaluationResults.GetValueOrDefault(expression) ??
                new DebugEvaluationResult("", null, default));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void PublishPaused(DebugThreadId thread)
        {
            Snapshot = new DebugSessionSnapshot(
                Guid.NewGuid(),
                DebugRuntimeKind.Mono,
                DebugSessionStatus.Paused,
                1234,
                new DebuggerCapabilities(
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true),
                new DebugStopInfo(
                    DebugStopReason.Breakpoint,
                    thread),
                null);
            StateChanged?.Invoke(Snapshot);
        }

        public void PublishRunning()
        {
            Snapshot = Snapshot with
            {
                Status = DebugSessionStatus.Running,
                Stop = null
            };
            StateChanged?.Invoke(Snapshot);
        }
    }
}
