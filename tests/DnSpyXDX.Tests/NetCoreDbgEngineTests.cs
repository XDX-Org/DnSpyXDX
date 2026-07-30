using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading.Channels;
using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

[Collection(LiveDebuggerTestCollection.Name)]
public sealed class NetCoreDbgEngineTests
{
    [Fact]
    public void Launch_target_inspector_rejects_class_libraries()
    {
        var classLibrary = typeof(DebugLaunchTargetInspector)
            .Assembly.Location;

        var rejected = DebugLaunchTargetInspector.Inspect(classLibrary);
        var accepted = DebugLaunchTargetInspector.Inspect(TestWorkerPath());

        Assert.False(rejected.IsLaunchable);
        Assert.Contains(
            "class library",
            rejected.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(accepted.IsLaunchable, accepted.Error);
    }

    [Fact]
    public async Task Launch_rejects_class_library_before_adapter_start()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.StartAsync(
                new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    typeof(DebugLaunchTargetInspector).Assembly.Location),
                CancellationToken.None));

        Assert.Contains(
            "class library",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_reports_explicit_missing_adapter()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"missing-netcoredbg-{Guid.NewGuid():N}");
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(path));

        Assert.False(provider.IsAvailable);
        Assert.Contains(
            NetCoreDbgEngineProvider.PathEnvironmentVariable,
            provider.UnavailableReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_initializes_configures_and_reports_capabilities()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath(),
                ["target-argument"],
                Environment: new Dictionary<string, string>
                {
                    ["XDX_TEST"] = "1"
                },
                StopAtEntry: true),
            CancellationToken.None);

        Assert.Equal(4242, result.ProcessId);
        Assert.True(result.IsPaused);
        Assert.Equal(DebugStopReason.Entry, result.InitialStop!.Reason);
        Assert.True(result.Capabilities.SupportsLaunch);
        Assert.True(result.Capabilities.SupportsAttach);
        Assert.True(result.Capabilities.SupportsFunctionBreakpoints);
        Assert.True(result.Capabilities.SupportsConditionalBreakpoints);
        Assert.True(result.Capabilities.SupportsExceptionBreakpoints);
        Assert.True(result.Capabilities.SupportsSetVariable);
        Assert.True(result.Capabilities.SupportsEvaluate);
        Assert.False(result.Capabilities.SupportsHitConditions);
        Assert.False(result.Capabilities.SupportsDecompiledCodeBreakpoints);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Extended_adapter_stops_at_managed_entry_without_symbols()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-il-entry"],
                TimeSpan.FromSeconds(2)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath(),
                StopAtEntry: true),
            CancellationToken.None);

        var expected = EntryPointLocation(TestWorkerPath());
        Assert.True(result.IsPaused);
        Assert.Equal(
            DebugStopReason.Entry,
            Assert.IsType<DebugStopInfo>(result.InitialStop).Reason);
        Assert.Equal(expected, result.InitialStop.Location);
        var received = new List<DebugEngineEvent>();
        while (events.Reader.TryRead(out var value))
            received.Add(value);
        Assert.DoesNotContain(
            received,
            value => value is DebugEngineFaulted);
        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Translates_threads_frames_scopes_variables_and_evaluation()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath(),
                StopAtEntry: true),
            CancellationToken.None);

        var thread = Assert.Single(
            await engine.GetThreadsAsync(CancellationToken.None));
        var frame = Assert.Single(
            await engine.GetStackTraceAsync(
                thread.Id,
                CancellationToken.None));
        var scope = Assert.Single(
            await engine.GetScopesAsync(
                frame.Id,
                CancellationToken.None));
        var variable = Assert.Single(
            await engine.GetVariablesAsync(
                scope.Variables,
                CancellationToken.None));
        var evaluation = await engine.EvaluateAsync(
            "answer",
            frame.Id,
            CancellationToken.None);

        Assert.Equal("Main Thread", thread.Name);
        Assert.True(thread.IsStopped);
        Assert.Equal("Sample.Program.Main()", frame.Name);
        Assert.Equal("/src/Program.cs", frame.SourcePath);
        Assert.Equal(12, frame.SourceLine);
        Assert.Equal(5, frame.SourceColumn);
        Assert.Equal("Sample.dll", frame.ModuleName);
        Assert.Equal("/debug/Sample.dll", frame.ModulePath);
        Assert.Equal("Locals", scope.Name);
        Assert.Equal("answer", variable.Name);
        Assert.Equal("42", variable.Value);
        Assert.Equal("int", variable.Type);
        Assert.True(variable.CanSetValue);
        Assert.Equal("42", evaluation.Value);
        Assert.Equal("int", evaluation.Type);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Translates_continue_pause_and_step_events()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);
        await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath(),
                StopAtEntry: true),
            CancellationToken.None);
        _ = await ReadEventAsync<DebugEngineStopped>(events.Reader);

        await engine.ContinueAsync(CancellationToken.None);
        _ = await ReadEventAsync<DebugEngineContinued>(events.Reader);
        await engine.PauseAsync(CancellationToken.None);
        var paused = await ReadEventAsync<DebugEngineStopped>(events.Reader);
        await engine.StepAsync(
            paused.Stop.Thread,
            DebugStepKind.Over,
            CancellationToken.None);
        var stepped = await ReadEventAsync<DebugEngineStopped>(events.Reader);

        Assert.Equal(DebugStopReason.Pause, paused.Stop.Reason);
        Assert.Equal(DebugStopReason.Step, stepped.Stop.Reason);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Attach_uses_requested_process_id()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(CancellationToken.None);

        var result = await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.CoreClr,
                ProcessId: 2468),
            CancellationToken.None);

        Assert.Equal(2468, result.ProcessId);
        Assert.False(result.IsPaused);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stock_adapter_rejects_decompiled_il_breakpoints_explicitly()
    {
        var provider = Provider();
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.CoreClr,
                ProcessId: 2468),
            CancellationToken.None);
        var breakpoint = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(Guid.NewGuid(), 0x06000001),
                4));

        var binding = Assert.Single(
            await engine.SetBreakpointsAsync(
                [breakpoint],
                CancellationToken.None));

        Assert.False(binding.IsVerified);
        Assert.Contains(
            "xdx/setIlBreakpoints",
            binding.Message,
            StringComparison.Ordinal);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Extended_adapter_binds_il_breakpoints_and_reports_runtime_locations()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-il"],
                TimeSpan.FromSeconds(2)));
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);
        var result = await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.CoreClr,
                ProcessId: 2468),
            CancellationToken.None);
        var location = new DebugCodeLocation(
            new DebugMethodId(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                0x06000001),
            4);
        var breakpoint = new DebugBreakpoint(Guid.NewGuid(), location);

        var binding = Assert.Single(
            await engine.SetBreakpointsAsync(
                [breakpoint],
                CancellationToken.None));
        await engine.PauseAsync(CancellationToken.None);
        var stopped = await ReadEventAsync<DebugEngineStopped>(events.Reader);
        var frame = Assert.Single(
            await engine.GetStackTraceAsync(
                stopped.Stop.Thread,
                CancellationToken.None));

        Assert.True(result.Capabilities.SupportsDecompiledCodeBreakpoints);
        Assert.True(binding.IsVerified);
        Assert.Equal(location, binding.BoundLocation);
        Assert.Equal(location, stopped.Stop.Location);
        Assert.Equal(location, frame.Location);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Initial_il_breakpoints_bind_before_configuration_completes()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-il"],
                TimeSpan.FromSeconds(2)));
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);
        var location = new DebugCodeLocation(
            new DebugMethodId(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                0x06000001),
            4);
        var breakpoint = new DebugBreakpoint(Guid.NewGuid(), location);

        await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.CoreClr,
                ProcessId: 2468)
            {
                InitialBreakpoints = [breakpoint]
            },
            CancellationToken.None);
        var changed = await ReadEventAsync<DebugEngineBreakpointsChanged>(
            events.Reader);

        var binding = Assert.Single(changed.Breakpoints);
        Assert.True(binding.IsVerified);
        Assert.Equal(location, binding.BoundLocation);

        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_timeout_stops_unresponsive_adapter()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-no-initialized"],
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(200)));
        await using var engine = await provider.CreateAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => engine.StartAsync(
                new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    TestWorkerPath()),
                CancellationToken.None));

        Assert.Contains("startup", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_netcoredbg_launches_and_stops_at_entry_when_configured()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        Assert.True(provider.IsAvailable, provider.UnavailableReason);
        await using var engine = await provider.CreateAsync(CancellationToken.None);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath(),
                ["target"],
                StopAtEntry: true),
            CancellationToken.None);
        try
        {
            var initialStop = Assert.IsType<DebugStopInfo>(result.InitialStop);
            var threads = await engine.GetThreadsAsync(CancellationToken.None);
            var frames = await engine.GetStackTraceAsync(
                initialStop.Thread,
                CancellationToken.None);

            Assert.True(result.IsPaused);
            Assert.Equal(DebugStopReason.Entry, initialStop.Reason);
            Assert.Contains(
                threads,
                thread => thread.Id == initialStop.Thread);
            Assert.NotEmpty(frames);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Live_netcoredbg_stops_at_entry_without_pdb()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        using var target = CreateSymbolLessTestWorker();
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                target.ExecutablePath,
                ["target"],
                StopAtEntry: true),
            CancellationToken.None);

        try
        {
            Assert.True(result.IsPaused);
            Assert.Equal(
                DebugStopReason.Entry,
                Assert.IsType<DebugStopInfo>(result.InitialStop).Reason);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Live_external_target_stops_at_entry_when_configured()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        var target = Environment.GetEnvironmentVariable(
            "DNSPYXDX_EXTERNAL_DEBUG_TARGET");
        if (string.IsNullOrWhiteSpace(adapter) ||
            string.IsNullOrWhiteSpace(target))
            return;

        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                target,
                StopAtEntry: true),
            CancellationToken.None);

        try
        {
            Assert.True(result.IsPaused);
            var stop = Assert.IsType<DebugStopInfo>(result.InitialStop);
            Assert.Equal(DebugStopReason.Entry, stop.Reason);
            Assert.NotEmpty(
                await engine.GetStackTraceAsync(
                    stop.Thread,
                    CancellationToken.None));
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Live_external_target_populates_decompiled_frame_variables()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        var target = Environment.GetEnvironmentVariable(
            "DNSPYXDX_EXTERNAL_DEBUG_TARGET");
        var methodName = Environment.GetEnvironmentVariable(
            "DNSPYXDX_EXTERNAL_DEBUG_METHOD");
        var statement = Environment.GetEnvironmentVariable(
            "DNSPYXDX_EXTERNAL_BREAKPOINT_TEXT");
        var expectedVariable = Environment.GetEnvironmentVariable(
            "DNSPYXDX_EXTERNAL_EXPECTED_VARIABLE");
        if (string.IsNullOrWhiteSpace(adapter) ||
            string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(methodName) ||
            string.IsNullOrWhiteSpace(statement))
            return;

        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(target);
        var method = Assert.Single(
            await backend.SearchAsync(methodName),
            result =>
                result.Kind == "Method" &&
                result.QualifiedName == methodName);
        var document = await backend.DecompileAsync(
            method.Symbol,
            DecompilerLanguage.CSharp);
        var point = Assert.Single(
            document.DebugMap!.SequencePoints,
            candidate =>
                document.Text.Substring(
                    candidate.StartOffset,
                    candidate.Length)
                    .Contains(statement, StringComparison.Ordinal));
        var breakpoint = new DebugBreakpoint(
            Guid.NewGuid(),
            point.BreakpointLocation ?? point.Location);
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                target)
            {
                InitialBreakpoints = [breakpoint]
            },
            CancellationToken.None);
        try
        {
            var stop = await ReadStopAsync(
                events.Reader,
                DebugStopReason.Breakpoint);
            var frame = Assert.IsType<DebugStackFrame>(
                (await engine.GetStackTraceAsync(
                    stop.Thread,
                    CancellationToken.None)).FirstOrDefault());
            var scope = Assert.Single(
                await engine.GetScopesAsync(
                    frame.Id,
                    CancellationToken.None));
            Assert.NotEqual(0, scope.Variables.Value);
            var variables = await engine.GetVariablesAsync(
                scope.Variables,
                CancellationToken.None);
            Assert.NotEmpty(variables);
            if (!string.IsNullOrWhiteSpace(expectedVariable))
                Assert.Contains(
                    variables,
                    variable => variable.Name == expectedVariable);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Initial_breakpoints_hold_launch_at_entry_then_resume()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-il"],
                TimeSpan.FromSeconds(2)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);
        var breakpoint = new DebugBreakpoint(
            Guid.NewGuid(),
            new DebugCodeLocation(
                new DebugMethodId(
                    Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    0x06000001),
                4));

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                TestWorkerPath())
            {
                InitialBreakpoints = [breakpoint]
            },
            CancellationToken.None);

        Assert.False(result.IsPaused);
        Assert.Null(result.InitialStop);
        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pending_il_breakpoint_updates_when_module_loads()
    {
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                DotnetHost(),
                [TestWorkerPath(), "netcoredbg-il-rebind"],
                TimeSpan.FromSeconds(2)));
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);
        var location = new DebugCodeLocation(
            new DebugMethodId(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                0x06000001),
            4);
        var breakpoint = new DebugBreakpoint(Guid.NewGuid(), location);

        await engine.StartAsync(
            new DebugAttachRequest(
                DebugRuntimeKind.CoreClr,
                ProcessId: 2468)
            {
                InitialBreakpoints = [breakpoint]
            },
            CancellationToken.None);

        DebugBreakpointBinding? verified = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (verified is null &&
               await events.Reader.WaitToReadAsync(timeout.Token))
        {
            while (events.Reader.TryRead(out var value))
            {
                if (value is not DebugEngineBreakpointsChanged changed)
                    continue;
                var binding = Assert.Single(changed.Breakpoints);
                if (binding.IsVerified) verified = binding;
            }
        }

        Assert.NotNull(verified);
        Assert.Equal(location, verified.BoundLocation);
        await engine.TerminateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Live_extended_netcoredbg_binds_and_hits_il_breakpoint()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        var worker = TestWorkerPath();
        var location = EntryPointLocation(worker);
        var requested = new DebugBreakpoint(Guid.NewGuid(), location);
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                worker,
                ["target"])
            {
                InitialBreakpoints = [requested]
            },
            CancellationToken.None);

        try
        {
            Assert.True(result.Capabilities.SupportsDecompiledCodeBreakpoints);
            DebugBreakpointBinding? binding = null;
            DebugStopInfo? stop = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((binding is null || stop is null) &&
                   await events.Reader.WaitToReadAsync(timeout.Token))
            {
                while (events.Reader.TryRead(out var value))
                {
                    if (value is DebugEngineBreakpointsChanged changed)
                        binding = Assert.Single(changed.Breakpoints);
                    else if (value is DebugEngineStopped stopped)
                        stop = stopped.Stop;
                }
            }

            Assert.NotNull(binding);
            Assert.True(binding.IsVerified, binding.Message);
            Assert.Equal(location, binding.BoundLocation);
            Assert.NotNull(stop);
            Assert.Equal(DebugStopReason.Breakpoint, stop.Reason);
            Assert.Equal(location, stop.Location);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Live_extended_netcoredbg_hits_decompiler_mapped_breakpoint()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        var worker = TestWorkerPath();
        using var stream = File.OpenRead(worker);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var targetType = metadata.TypeDefinitions
            .Select(handle => (Handle: handle, Definition:
                metadata.GetTypeDefinition(handle)))
            .Single(value =>
                metadata.GetString(value.Definition.Name) ==
                "DebuggerBreakpointTarget");
        var targetMethod = targetType.Definition.GetMethods()
            .Single(handle =>
                metadata.GetString(
                    metadata.GetMethodDefinition(handle).Name) == "RunAsync");
        var method = new DebugMethodId(
            mvid,
            MetadataTokens.GetToken(targetMethod));
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(worker);
        var document = await backend.DecompileAsync(
            new SymbolId(mvid, MetadataTokens.GetToken(targetType.Handle)),
            DecompilerLanguage.CSharp);
        var requestedPoint = document.DebugMap!.SequencePoints
            .Where(point =>
                document.Text.Substring(point.StartOffset, point.Length)
                    .Contains("await Task.Yield", StringComparison.Ordinal))
            .OrderBy(point => point.Length)
            .FirstOrDefault();
        Assert.NotNull(requestedPoint);
        var requested = new DebugBreakpoint(
            Guid.NewGuid(),
            requestedPoint.BreakpointLocation ??
                requestedPoint.Location);
        Assert.NotEqual(method.MetadataToken, requested.Location.Method.MetadataToken);
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                worker,
                ["target"])
            {
                InitialBreakpoints = [requested]
            },
            CancellationToken.None);

        try
        {
            Assert.True(result.Capabilities.SupportsDecompiledCodeBreakpoints);
            DebugBreakpointBinding? binding = null;
            DebugStopInfo? stop = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((binding?.IsVerified != true || stop is null) &&
                   await events.Reader.WaitToReadAsync(timeout.Token))
            {
                while (events.Reader.TryRead(out var value))
                {
                    if (value is DebugEngineBreakpointsChanged changed)
                        binding = Assert.Single(changed.Breakpoints);
                    else if (value is DebugEngineStopped
                        {
                            Stop.Reason: DebugStopReason.Breakpoint
                        } stopped)
                        stop = stopped.Stop;
                }
            }

            Assert.NotNull(binding);
            Assert.True(binding.IsVerified, binding.Message);
            Assert.NotNull(stop);
            Assert.Equal(DebugStopReason.Breakpoint, stop.Reason);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Theory]
    [InlineData(DebugStepKind.Into)]
    [InlineData(DebugStepKind.Over)]
    [InlineData(DebugStepKind.Out)]
    public async Task Live_extended_netcoredbg_steps_in_decompiled_module_without_pdb(
        DebugStepKind stepKind)
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        using var target = CreateSymbolLessTestWorker();
        using var stream = File.OpenRead(target.AssemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var targetType = metadata.TypeDefinitions
            .Select(handle => (Handle: handle, Definition:
                metadata.GetTypeDefinition(handle)))
            .Single(value =>
                metadata.GetString(value.Definition.Name) ==
                "DebuggerBreakpointTarget");
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(target.AssemblyPath);
        var document = await backend.DecompileAsync(
            new SymbolId(mvid, MetadataTokens.GetToken(targetType.Handle)),
            DecompilerLanguage.CSharp);
        var requestedPoint = document.DebugMap!.SequencePoints
            .Where(point =>
                document.Text.Substring(point.StartOffset, point.Length)
                    .Contains("return value", StringComparison.Ordinal))
            .OrderBy(point => point.Length)
            .FirstOrDefault();
        Assert.NotNull(requestedPoint);
        var requested = new DebugBreakpoint(
            Guid.NewGuid(),
            requestedPoint.BreakpointLocation ??
                requestedPoint.Location);
        var provider = new NetCoreDbgEngineProvider(
            new CoreClrDebuggerOptions(
                adapter,
                GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
        await using var engine = await provider.CreateAsync(
            CancellationToken.None);
        var events = Channel.CreateUnbounded<DebugEngineEvent>();
        engine.EventReceived += value => events.Writer.TryWrite(value);

        var result = await engine.StartAsync(
            new DebugLaunchRequest(
                DebugRuntimeKind.CoreClr,
                target.ExecutablePath,
                ["target"])
            {
                InitialBreakpoints = [requested]
            },
            CancellationToken.None);
        try
        {
            var stopped = await ReadStopAsync(
                events.Reader,
                DebugStopReason.Breakpoint);
            var frame = Assert.IsType<DebugStackFrame>(
                (await engine.GetStackTraceAsync(
                    stopped.Thread,
                    CancellationToken.None)).FirstOrDefault());
            var scope = Assert.Single(
                await engine.GetScopesAsync(
                    frame.Id,
                    CancellationToken.None));
            Assert.NotEqual(0, scope.Variables.Value);
            var variables = await engine.GetVariablesAsync(
                scope.Variables,
                CancellationToken.None);
            var value = Assert.Single(
                variables,
                variable => variable.Name == "value");
            Assert.Equal("42", value.Value);
            var original = Assert.Single(
                variables,
                variable => variable.Name == "V_0");
            Assert.Equal("40", original.Value);

            await engine.StepAsync(
                stopped.Thread,
                stepKind,
                CancellationToken.None);
            var stepped = await ReadStopAsync(
                events.Reader,
                DebugStopReason.Step);

            Assert.Equal(stopped.Thread, stepped.Thread);
        }
        finally
        {
            await TerminateLaunchAsync(engine, result.ProcessId);
        }
    }

    [Fact]
    public async Task Live_netcoredbg_attaches_to_running_process_when_configured()
    {
        var adapter = Environment.GetEnvironmentVariable(
            NetCoreDbgEngineProvider.PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adapter)) return;

        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = DotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { TestWorkerPath(), "target" }
        }) ?? throw new InvalidOperationException("Could not start CoreCLR attach target.");
        try
        {
            var provider = new NetCoreDbgEngineProvider(
                new CoreClrDebuggerOptions(
                    adapter,
                    GracefulShutdownTimeout: TimeSpan.FromSeconds(5)));
            await using var engine = await provider.CreateAsync(CancellationToken.None);
            var events = Channel.CreateUnbounded<DebugEngineEvent>();
            engine.EventReceived += value => events.Writer.TryWrite(value);

            var result = await engine.StartAsync(
                new DebugAttachRequest(
                    DebugRuntimeKind.CoreClr,
                    ProcessId: target.Id),
                CancellationToken.None);
            await engine.PauseAsync(CancellationToken.None);
            _ = await ReadEventAsync<DebugEngineStopped>(events.Reader);
            var threads = await engine.GetThreadsAsync(CancellationToken.None);

            Assert.Equal(target.Id, result.ProcessId);
            Assert.NotEmpty(threads);

            await engine.TerminateAsync(CancellationToken.None);
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
        }
    }

    private static NetCoreDbgEngineProvider Provider() => new(
        new CoreClrDebuggerOptions(
            DotnetHost(),
            [TestWorkerPath(), "netcoredbg"],
            TimeSpan.FromSeconds(2)));

    private static string TestWorkerPath()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "DnSpyXDX.Debugger.TestWorker.dll");
        Assert.True(File.Exists(path), $"Missing test worker: {path}");
        return path;
    }

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    private static DebugCodeLocation EntryPointLocation(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var token = peReader.PEHeaders.CorHeader?
            .EntryPointTokenOrRelativeVirtualAddress ?? 0;
        Assert.Equal(0x06, token >> 24);
        return new DebugCodeLocation(new DebugMethodId(mvid, token), 0);
    }

    private static SymbolLessTestWorker CreateSymbolLessTestWorker()
    {
        var sourceAssembly = TestWorkerPath();
        var sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"DnSpyXDX-symbol-less-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var fileName in new[]
        {
            "DnSpyXDX.Debugger.TestWorker.exe",
            "DnSpyXDX.Debugger.TestWorker.dll",
            "DnSpyXDX.Debugger.TestWorker.deps.json",
            "DnSpyXDX.Debugger.TestWorker.runtimeconfig.json"
        })
        {
            File.Copy(
                Path.Combine(sourceDirectory, fileName),
                Path.Combine(directory, fileName));
        }
        return new SymbolLessTestWorker(directory);
    }

    private static async Task<DebugStopInfo> ReadStopAsync(
        ChannelReader<DebugEngineEvent> events,
        DebugStopReason reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await events.WaitToReadAsync(timeout.Token))
        {
            while (events.TryRead(out var value))
            {
                if (value is DebugEngineStopped stopped &&
                    stopped.Stop.Reason == reason)
                    return stopped.Stop;
            }
        }

        throw new InvalidOperationException(
            $"Debugger stop reason {reason} was not received.");
    }

    private static async Task TerminateLaunchAsync(
        IDebuggerEngine engine,
        int? processId)
    {
        try
        {
            await engine.TerminateAsync(CancellationToken.None);
        }
        finally
        {
            if (processId is { } value)
                await WaitForProcessExitAsync(value);
        }
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (ArgumentException)
        {
            // The target exited before it could be reopened.
        }
    }

    private static async Task<T> ReadEventAsync<T>(
        ChannelReader<DebugEngineEvent> events)
        where T : DebugEngineEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await events.WaitToReadAsync(timeout.Token))
        {
            while (events.TryRead(out var value))
            {
                if (value is T found) return found;
            }
        }

        throw new InvalidOperationException(
            $"Debugger event {typeof(T).Name} was not received.");
    }

    private sealed class SymbolLessTestWorker(string directory) : IDisposable
    {
        public string AssemblyPath { get; } = Path.Combine(
            directory,
            "DnSpyXDX.Debugger.TestWorker.dll");
        public string ExecutablePath { get; } = Path.Combine(
            directory,
            "DnSpyXDX.Debugger.TestWorker.exe");

        public void Dispose()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (Exception exception)
                    when (attempt < 4 &&
                          exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                }
            }
        }
    }
}
