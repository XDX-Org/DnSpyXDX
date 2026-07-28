using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed record CoreClrDebuggerOptions(
    string? NetCoreDbgPath = null,
    IReadOnlyList<string>? AdapterArguments = null,
    TimeSpan? GracefulShutdownTimeout = null,
    TimeSpan? StartupTimeout = null);

public sealed class NetCoreDbgEngineProvider : IDebuggerEngineProvider
{
    public const string PathEnvironmentVariable = "DNSPYXDX_NETCOREDBG_PATH";

    private readonly CoreClrDebuggerOptions options;
    private readonly string? executablePath;

    public NetCoreDbgEngineProvider() : this(new CoreClrDebuggerOptions())
    {
    }

    public NetCoreDbgEngineProvider(CoreClrDebuggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        executablePath = ResolveExecutable(options.NetCoreDbgPath);
    }

    public DebugRuntimeKind Runtime => DebugRuntimeKind.CoreClr;
    public bool IsAvailable => executablePath is not null;
    public string? UnavailableReason => IsAvailable
        ? null
        : $"NetCoreDbg was not found. Set {PathEnvironmentVariable}, install netcoredbg on PATH, " +
          "or place it in the packaged debugger directory.";

    public ValueTask<IDebuggerEngine> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (executablePath is null)
            throw new NotSupportedException(UnavailableReason);
        return ValueTask.FromResult<IDebuggerEngine>(
            new NetCoreDbgEngine(executablePath, options));
    }

    private static string? ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return ResolveCandidate(configuredPath);
        var executableName = OperatingSystem.IsWindows()
            ? "netcoredbg.exe"
            : "netcoredbg";
        var runtimeIdentifier = $"{(OperatingSystem.IsWindows() ? "win" : "linux")}-" +
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable(PathEnvironmentVariable),
            Path.Combine(
                AppContext.BaseDirectory,
                "debuggers",
                "netcoredbg",
                runtimeIdentifier,
                executableName),
            Path.Combine(AppContext.BaseDirectory, "debuggers", "netcoredbg", executableName),
            Path.Combine(AppContext.BaseDirectory, executableName),
            executableName
        };

        foreach (var candidate in candidates)
        {
            var resolved = ResolveCandidate(candidate);
            if (resolved is not null) return resolved;
        }

        return null;
    }

    private static string? ResolveCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (Path.IsPathRooted(candidate) ||
            candidate.Contains(Path.DirectorySeparatorChar) ||
            candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(candidate);
            return File.Exists(fullPath) ? PrepareExecutable(fullPath) : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path)) return PrepareExecutable(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows() &&
                Path.GetExtension(candidate).Length == 0 &&
                File.Exists(path + ".exe"))
                return PrepareExecutable(Path.GetFullPath(path + ".exe"));
        }

        return null;
    }

    private static string PrepareExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return path;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
            // Process startup reports a clearer error if a read-only deployment cannot be fixed.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return path;
    }
}

internal sealed class NetCoreDbgEngine(
    string executablePath,
    CoreClrDebuggerOptions options) : IDebuggerEngine
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly string DecompiledBreakpointMessage =
        "Stock NetCoreDbg cannot bind MVID/token/IL-offset breakpoints. " +
        "The xdx/setIlBreakpoints extension is required.";

    private readonly TaskCompletionSource initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DebugStopInfo> initialStop =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object breakpointsGate = new();
    private IReadOnlyDictionary<Guid, DebugBreakpoint> requestedBreakpoints =
        new Dictionary<Guid, DebugBreakpoint>();
    private IReadOnlyList<Guid> breakpointOrder = [];
    private Dictionary<Guid, DebugBreakpointBinding> breakpointBindings = [];
    private DebuggerWorker? worker;
    private DebuggerCapabilities capabilities = DebuggerCapabilitySets.None;
    private DebugThreadId? lastStoppedThread;
    private DebugStopInfo? lastStop;
    private int? targetProcessId;
    private int started;
    private int disposed;
    private int terminationRequested;
    private int exitReported;
    private bool isStopped;

    public event Action<DebugEngineEvent>? EventReceived;

    public async Task<DebugEngineStartResult> StartAsync(
        DebugStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Runtime != DebugRuntimeKind.CoreClr)
            throw new ArgumentException(
                $"NetCoreDbg cannot start runtime {request.Runtime}.",
                nameof(request));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("CoreCLR debugger engine has already started.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var holdAtEntryForInitialBreakpoints =
            request is DebugLaunchRequest { StopAtEntry: false } &&
            request.InitialBreakpoints?.Any(
                breakpoint => breakpoint.Enabled) == true;
        var startArguments = BuildStartArguments(
            request,
            holdAtEntryForInitialBreakpoints);
        var startCommand = request is DebugLaunchRequest ? "launch" : "attach";
        var startupTimeout = options.StartupTimeout ?? DefaultStartupTimeout;
        if (startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CoreCLR debugger startup timeout must be positive.");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        startup.CancelAfter(startupTimeout);
        var startupToken = startup.Token;

        try
        {
            var arguments = new List<string>();
            arguments.AddRange(options.AdapterArguments ?? []);
            arguments.Add("--interpreter=vscode");
            worker = await DebuggerWorker.StartAsync(
                new DebuggerWorkerOptions(
                    executablePath,
                    arguments,
                    GracefulShutdownTimeout: options.GracefulShutdownTimeout),
                startupToken).ConfigureAwait(false);
            worker.Connection.EventReceived += OnDapEvent;
            worker.Connection.Faulted += OnConnectionFaulted;
            worker.OutputReceived += OnWorkerOutput;
            _ = ObserveWorkerExitAsync(worker);

            var initialize = await worker.Connection.SendRequestAsync(
                "initialize",
                new JsonObject
                {
                    ["adapterID"] = "coreclr",
                    ["clientID"] = "DnSpyXDX",
                    ["clientName"] = "DnSpyXDX",
                    ["locale"] = "en-US",
                    ["linesStartAt1"] = true,
                    ["columnsStartAt1"] = true,
                    ["pathFormat"] = "path",
                    ["supportsVariableType"] = true,
                    ["supportsVariablePaging"] = true,
                    ["supportsRunInTerminalRequest"] = false
                },
                startupToken).ConfigureAwait(false);
            capabilities = ParseCapabilities(RequireSuccess(initialize));

            var startResponse = worker.Connection.SendRequestAsync(
                startCommand,
                startArguments,
                startupToken);

            await WaitForInitializedAsync(startResponse, startupToken)
                .ConfigureAwait(false);
            if (request.InitialBreakpoints is { } initialBreakpoints)
            {
                var initialBindings = await SetBreakpointsAsync(
                    initialBreakpoints,
                    startupToken).ConfigureAwait(false);
                Emit(new DebugEngineBreakpointsChanged(initialBindings));
            }
            RequireSuccess(await worker.Connection.SendRequestAsync(
                "configurationDone",
                new JsonObject(),
                startupToken).ConfigureAwait(false));
            RequireSuccess(await startResponse.ConfigureAwait(false));

            if (request is DebugLaunchRequest launch &&
                (launch.StopAtEntry || holdAtEntryForInitialBreakpoints))
            {
                lastStop = await initialStop.Task.WaitAsync(startupToken)
                    .ConfigureAwait(false);
                if (holdAtEntryForInitialBreakpoints &&
                    lastStop.Reason == DebugStopReason.Entry)
                    await ContinueAsync(startupToken).ConfigureAwait(false);
            }

            return new DebugEngineStartResult(
                targetProcessId,
                capabilities,
                IsPaused: lastStop is not null,
                InitialStop: lastStop);
        }
        catch (Exception exception)
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                worker = null;
            }

            if (exception is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested &&
                startup.IsCancellationRequested)
                throw new TimeoutException(
                    $"NetCoreDbg did not finish startup within {startupTimeout}.",
                    exception);
            throw;
        }
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref terminationRequested, 1);
        var current = GetWorker();
        await current.StopAsync(
            terminateDebuggee: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ContinueAsync(CancellationToken cancellationToken)
    {
        var thread = await GetControlThreadAsync(cancellationToken).ConfigureAwait(false);
        RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "continue",
            new JsonObject { ["threadId"] = thread.Value },
            cancellationToken).ConfigureAwait(false));
        MarkRunning();
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var thread = await GetControlThreadAsync(cancellationToken).ConfigureAwait(false);
        RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "pause",
            new JsonObject { ["threadId"] = thread.Value },
            cancellationToken).ConfigureAwait(false));
    }

    public async Task StepAsync(
        DebugThreadId thread,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        var command = kind switch
        {
            DebugStepKind.Into => "stepIn",
            DebugStepKind.Over => "next",
            DebugStepKind.Out => "stepOut",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            command,
            new JsonObject { ["threadId"] = thread.Value },
            cancellationToken).ConfigureAwait(false));
        MarkRunning();
    }

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        cancellationToken.ThrowIfCancellationRequested();
        var requestedById = breakpoints.ToDictionary(breakpoint => breakpoint.Id);
        lock (breakpointsGate)
        {
            requestedBreakpoints = requestedById;
            breakpointOrder = breakpoints.Select(value => value.Id).ToArray();
            breakpointBindings = breakpointBindings
                .Where(value => requestedById.ContainsKey(value.Key))
                .ToDictionary();
        }
        if (!capabilities.SupportsDecompiledCodeBreakpoints)
        {
            var unsupported = breakpoints
                .Select(breakpoint => new DebugBreakpointBinding(
                    breakpoint.Id,
                    IsVerified: false,
                    Message: breakpoint.Enabled
                        ? DecompiledBreakpointMessage
                        : "Breakpoint is disabled."))
                .ToArray();
            RememberBreakpointBindings(unsupported);
            return unsupported;
        }

        var request = new JsonObject
        {
            ["breakpoints"] = new JsonArray(
                breakpoints.Select(SerializeIlBreakpoint).ToArray())
        };
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "xdx/setIlBreakpoints",
            request,
            cancellationToken).ConfigureAwait(false));
        var response = RequiredArray(body, "breakpoints");
        var bindings = response.EnumerateArray()
            .Select(value => ParseIlBreakpointBinding(value, requestedById))
            .ToArray();

        if (bindings.Length != breakpoints.Count ||
            bindings.Select(binding => binding.BreakpointId).Distinct().Count() !=
            breakpoints.Count)
            throw new InvalidDataException(
                "NetCoreDbg xdx/setIlBreakpoints response must contain one unique binding " +
                "for every requested breakpoint.");
        RememberBreakpointBindings(bindings);
        return bindings;
    }

    public async Task<IReadOnlyList<DebugThread>> GetThreadsAsync(
        CancellationToken cancellationToken)
    {
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "threads",
            new JsonObject(),
            cancellationToken).ConfigureAwait(false));
        var threads = RequiredArray(body, "threads");
        return threads.EnumerateArray()
            .Select(value => new DebugThread(
                new DebugThreadId(RequiredInt64(value, "id")),
                OptionalString(value, "name") ?? $"Thread {RequiredInt64(value, "id")}",
                isStopped))
            .ToArray();
    }

    public async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(
        DebugThreadId thread,
        CancellationToken cancellationToken)
    {
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "stackTrace",
            new JsonObject { ["threadId"] = thread.Value },
            cancellationToken).ConfigureAwait(false));
        var frames = RequiredArray(body, "stackFrames");
        var modulePaths = await TryGetModulePathsAsync(cancellationToken)
            .ConfigureAwait(false);
        return frames.EnumerateArray()
            .Select(value =>
            {
                var moduleId = OptionalDisplayValue(value, "moduleId");
                return new DebugStackFrame(
                    new DebugFrameId(RequiredInt64(value, "id")),
                    thread,
                    OptionalString(value, "name") ?? "<unknown>",
                    Location: OptionalDebugLocation(value),
                    SourcePath: OptionalObjectString(value, "source", "path"),
                    SourceLine: OptionalPositiveInt32(value, "line"),
                    SourceColumn: OptionalPositiveInt32(value, "column"),
                    ModuleName: moduleId,
                    ModulePath: moduleId is not null &&
                        modulePaths.TryGetValue(moduleId, out var modulePath)
                            ? modulePath
                            : null);
            })
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, string>>
        TryGetModulePathsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetWorker().Connection.SendRequestAsync(
                "modules",
                new JsonObject(),
                cancellationToken).ConfigureAwait(false);
            if (!response.Success || response.Body is not { } body)
                return new Dictionary<string, string>();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var module in RequiredArray(body, "modules").EnumerateArray())
            {
                var id = OptionalDisplayValue(module, "id");
                var path = OptionalString(module, "path");
                if (!string.IsNullOrWhiteSpace(id) &&
                    !string.IsNullOrWhiteSpace(path))
                    result[id] = path;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException)
        {
            return new Dictionary<string, string>();
        }
    }

    public async Task<IReadOnlyList<DebugScope>> GetScopesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken)
    {
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "scopes",
            new JsonObject { ["frameId"] = frame.Value },
            cancellationToken).ConfigureAwait(false));
        return RequiredArray(body, "scopes").EnumerateArray()
            .Select(value => new DebugScope(
                OptionalString(value, "name") ?? "<scope>",
                new DebugVariableReference(RequiredInt64(value, "variablesReference")),
                OptionalBoolean(value, "expensive")))
            .ToArray();
    }

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(
        DebugVariableReference reference,
        CancellationToken cancellationToken)
    {
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "variables",
            new JsonObject { ["variablesReference"] = reference.Value },
            cancellationToken).ConfigureAwait(false));
        return RequiredArray(body, "variables").EnumerateArray()
            .Select(value => new DebugVariable(
                OptionalString(value, "name") ?? "",
                OptionalString(value, "value") ?? "",
                OptionalString(value, "type"),
                new DebugVariableReference(RequiredInt64(value, "variablesReference")),
                OptionalString(value, "evaluateName"),
                CanSetValue: capabilities.SupportsSetVariable))
            .ToArray();
    }

    public async Task<DebugEvaluationResult> EvaluateAsync(
        string expression,
        DebugFrameId? frame,
        CancellationToken cancellationToken)
    {
        var arguments = new JsonObject
        {
            ["expression"] = expression,
            ["context"] = "watch"
        };
        if (frame is { } frameId) arguments["frameId"] = frameId.Value;
        var body = RequireSuccess(await GetWorker().Connection.SendRequestAsync(
            "evaluate",
            arguments,
            cancellationToken).ConfigureAwait(false));
        return new DebugEvaluationResult(
            OptionalString(body, "result") ?? "",
            OptionalString(body, "type"),
            new DebugVariableReference(RequiredInt64(body, "variablesReference")));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var current = worker;
        worker = null;
        if (current is null) return;
        current.Connection.EventReceived -= OnDapEvent;
        current.Connection.Faulted -= OnConnectionFaulted;
        current.OutputReceived -= OnWorkerOutput;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private JsonObject BuildStartArguments(
        DebugStartRequest request,
        bool forceStopAtEntry = false)
    {
        if (request is DebugLaunchRequest launch)
        {
            var program = Path.GetFullPath(launch.ExecutablePath);
            if (!File.Exists(program))
                throw new FileNotFoundException("Debug target was not found.", program);
            var arguments = new JsonObject
            {
                ["program"] = program,
                ["args"] = new JsonArray(
                    (launch.Arguments ?? [])
                    .Select(argument => (JsonNode?)JsonValue.Create(argument))
                    .ToArray()),
                ["cwd"] = string.IsNullOrWhiteSpace(launch.WorkingDirectory)
                    ? Path.GetDirectoryName(program)
                    : Path.GetFullPath(launch.WorkingDirectory),
                ["env"] = EnvironmentObject(launch.Environment),
                ["stopAtEntry"] = launch.StopAtEntry || forceStopAtEntry,
                ["justMyCode"] = false,
                ["enableStepFiltering"] = false,
                ["console"] = "internalConsole"
            };
            return arguments;
        }

        if (request is not DebugAttachRequest attach)
            throw new ArgumentException(
                $"Unsupported CoreCLR start request {request.GetType().Name}.",
                nameof(request));
        if (attach.Host is not null || attach.Port is not null)
            throw new NotSupportedException(
                "CoreCLR remote attach is not supported by the local NetCoreDbg provider.");
        if (attach.ProcessId is not > 0)
            throw new ArgumentException(
                "CoreCLR attach requires a positive process ID.",
                nameof(request));
        targetProcessId = attach.ProcessId;
        return new JsonObject { ["processId"] = attach.ProcessId.Value };
    }

    private static JsonObject EnvironmentObject(
        IReadOnlyDictionary<string, string>? environment)
    {
        var result = new JsonObject();
        foreach (var variable in environment ??
            new Dictionary<string, string>())
            result[variable.Key] = variable.Value;
        return result;
    }

    private async Task WaitForInitializedAsync(
        Task<DapResponse> startResponse,
        CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(initialized.Task, startResponse)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(completed, startResponse))
        {
            RequireSuccess(await startResponse.ConfigureAwait(false));
            await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await initialized.Task.ConfigureAwait(false);
        }
    }

    private async Task<DebugThreadId> GetControlThreadAsync(
        CancellationToken cancellationToken)
    {
        if (lastStoppedThread is { Value: > 0 } stopped) return stopped;
        var threads = await GetThreadsAsync(cancellationToken).ConfigureAwait(false);
        return threads.FirstOrDefault()?.Id ??
            throw new InvalidOperationException(
                "CoreCLR debugger reported no thread for execution control.");
    }

    private void OnDapEvent(DapEvent value)
    {
        try
        {
            switch (value.Name)
            {
                case "initialized":
                    initialized.TrySetResult();
                    break;
                case "process":
                    if (value.Body is { } processBody)
                        targetProcessId = OptionalInt32(
                            processBody,
                            "systemProcessId") ?? targetProcessId;
                    break;
                case "stopped":
                    HandleStopped(value.Body);
                    break;
                case "xdx/ilBreakpoint":
                    HandleIlBreakpoint(value.Body);
                    break;
                case "continued":
                    MarkRunning();
                    Emit(new DebugEngineContinued());
                    break;
                case "output":
                    HandleOutput(value.Body);
                    break;
                case "exited":
                    ReportExit(value.Body is { } exitedBody
                        ? OptionalInt32(exitedBody, "exitCode")
                        : null);
                    break;
                case "terminated":
                    ReportExit(null);
                    break;
            }
        }
        catch (Exception exception)
        {
            Emit(new DebugEngineFaulted(
                $"Invalid NetCoreDbg {value.Name} event: {exception.Message}"));
        }
    }

    private void HandleStopped(JsonElement? optionalBody)
    {
        if (optionalBody is not { } body)
            throw new InvalidDataException("NetCoreDbg stopped event has no body.");
        var thread = new DebugThreadId(RequiredInt64(body, "threadId"));
        var stop = new DebugStopInfo(
            ParseStopReason(OptionalString(body, "reason")),
            thread,
            Location: OptionalDebugLocation(body),
            Description: OptionalString(body, "description") ??
                OptionalString(body, "text"),
            AllThreadsStopped: !body.TryGetProperty(
                "allThreadsStopped",
                out var allThreads) ||
                allThreads.ValueKind != JsonValueKind.False);
        lastStoppedThread = thread;
        lastStop = stop;
        isStopped = true;
        initialStop.TrySetResult(stop);
        Emit(new DebugEngineStopped(stop));
    }

    private void HandleOutput(JsonElement? optionalBody)
    {
        if (optionalBody is not { } body) return;
        var output = OptionalString(body, "output");
        if (output is null) return;
        Emit(new DebugEngineOutput(new(
            OptionalString(body, "category") ?? "console",
            output)));
    }

    private void HandleIlBreakpoint(JsonElement? optionalBody)
    {
        if (optionalBody is not { } body)
            throw new InvalidDataException(
                "NetCoreDbg xdx/ilBreakpoint event has no body.");

        IReadOnlyDictionary<Guid, DebugBreakpoint> requested;
        lock (breakpointsGate)
            requested = requestedBreakpoints;

        var binding = ParseIlBreakpointBinding(body, requested);
        IReadOnlyList<DebugBreakpointBinding> allBindings;
        lock (breakpointsGate)
        {
            if (!requestedBreakpoints.ContainsKey(binding.BreakpointId))
                return;
            breakpointBindings[binding.BreakpointId] = binding;
            allBindings = breakpointOrder
                .Where(breakpointBindings.ContainsKey)
                .Select(id => breakpointBindings[id])
                .ToArray();
        }
        Emit(new DebugEngineBreakpointsChanged(allBindings));
    }

    private void RememberBreakpointBindings(
        IReadOnlyList<DebugBreakpointBinding> bindings)
    {
        lock (breakpointsGate)
            breakpointBindings = bindings.ToDictionary(
                value => value.BreakpointId);
    }

    private void MarkRunning()
    {
        lastStoppedThread = null;
        lastStop = null;
        isStopped = false;
    }

    private void ReportExit(int? exitCode)
    {
        if (Interlocked.Exchange(ref exitReported, 1) == 0)
            Emit(new DebugEngineExited(exitCode));
    }

    private void OnConnectionFaulted(Exception exception) =>
        Emit(new DebugEngineFaulted(
            $"NetCoreDbg protocol failed: {exception.Message}"));

    private void OnWorkerOutput(DebugOutputMessage output) =>
        Emit(new DebugEngineOutput(output));

    private void OnWorkerExited(DebuggerWorkerExit value)
    {
        if (value.Expected ||
            Volatile.Read(ref terminationRequested) != 0 ||
            Volatile.Read(ref exitReported) != 0)
            return;
        Emit(new DebugEngineFaulted(
            $"NetCoreDbg exited unexpectedly with code {value.ExitCode}."));
    }

    private async Task ObserveWorkerExitAsync(DebuggerWorker value)
    {
        try
        {
            OnWorkerExited(await value.Completion.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            Emit(new DebugEngineFaulted(
                $"NetCoreDbg worker monitoring failed: {exception.Message}"));
        }
    }

    private void Emit(DebugEngineEvent value)
    {
        var handlers = EventReceived;
        if (handlers is null) return;
        foreach (Action<DebugEngineEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // Debugger consumers cannot terminate adapter event processing.
            }
        }
    }

    private DebuggerWorker GetWorker()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return worker ??
            throw new InvalidOperationException("CoreCLR debugger engine is not running.");
    }

    private static DebuggerCapabilities ParseCapabilities(JsonElement body) => new(
        SupportsLaunch: true,
        SupportsAttach: true,
        SupportsFunctionBreakpoints: OptionalBoolean(
            body,
            "supportsFunctionBreakpoints"),
        SupportsConditionalBreakpoints: OptionalBoolean(
            body,
            "supportsConditionalBreakpoints"),
        SupportsHitConditions: OptionalBoolean(
            body,
            "supportsHitConditionalBreakpoints"),
        SupportsExceptionBreakpoints:
            body.TryGetProperty("exceptionBreakpointFilters", out var filters) &&
            filters.ValueKind == JsonValueKind.Array &&
            filters.GetArrayLength() > 0,
        SupportsSetVariable: OptionalBoolean(body, "supportsSetVariable"),
        SupportsEvaluate: true,
        SupportsDecompiledCodeBreakpoints: OptionalBoolean(
            body,
            "supportsXdxIlBreakpoints"));

    private static JsonNode SerializeIlBreakpoint(DebugBreakpoint breakpoint)
    {
        if (breakpoint.Location.Method.MetadataToken <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(breakpoint),
                "Breakpoint method metadata token must be positive.");
        if (breakpoint.Location.ILOffset < 0)
            throw new ArgumentOutOfRangeException(
                nameof(breakpoint),
                "Breakpoint IL offset cannot be negative.");
        return new JsonObject
        {
            ["id"] = breakpoint.Id.ToString("D"),
            ["moduleMvid"] = breakpoint.Location.Method.ModuleMvid.ToString("D"),
            ["methodToken"] = breakpoint.Location.Method.MetadataToken,
            ["ilOffset"] = breakpoint.Location.ILOffset,
            ["enabled"] = breakpoint.Enabled,
            ["condition"] = breakpoint.Condition,
            ["hitCondition"] = breakpoint.HitCondition,
            ["logMessage"] = breakpoint.LogMessage
        };
    }

    private static DebugBreakpointBinding ParseIlBreakpointBinding(
        JsonElement value,
        IReadOnlyDictionary<Guid, DebugBreakpoint> requestedById)
    {
        var idText = OptionalString(value, "id");
        if (!Guid.TryParse(idText, out var id) ||
            !requestedById.TryGetValue(id, out var requested))
            throw new InvalidDataException(
                "NetCoreDbg IL breakpoint response contains an unknown or invalid id.");

        DebugCodeLocation? boundLocation = null;
        if (value.TryGetProperty("moduleMvid", out _) ||
            value.TryGetProperty("methodToken", out _) ||
            value.TryGetProperty("ilOffset", out _))
        {
            boundLocation = RequiredDebugLocation(value);
        }
        else if (OptionalBoolean(value, "verified"))
        {
            boundLocation = requested.Location;
        }

        return new DebugBreakpointBinding(
            id,
            OptionalBoolean(value, "verified"),
            boundLocation,
            OptionalString(value, "message"));
    }

    private static DebugCodeLocation? OptionalDebugLocation(JsonElement value)
    {
        if (!value.TryGetProperty("xdxLocation", out var location) ||
            location.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (location.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "NetCoreDbg xdxLocation must be an object.");
        return RequiredDebugLocation(location);
    }

    private static DebugCodeLocation RequiredDebugLocation(JsonElement value)
    {
        var mvidText = OptionalString(value, "moduleMvid");
        if (!Guid.TryParse(mvidText, out var mvid))
            throw new InvalidDataException(
                "NetCoreDbg runtime location requires a valid moduleMvid.");
        var methodToken = RequiredInt32(value, "methodToken");
        var ilOffset = RequiredInt32(value, "ilOffset");
        if (methodToken <= 0 || ilOffset < 0)
            throw new InvalidDataException(
                "NetCoreDbg runtime location contains an invalid methodToken or ilOffset.");
        return new DebugCodeLocation(
            new DebugMethodId(mvid, methodToken),
            ilOffset);
    }

    private static DebugStopReason ParseStopReason(string? reason) => reason switch
    {
        "breakpoint" => DebugStopReason.Breakpoint,
        "step" => DebugStopReason.Step,
        "pause" => DebugStopReason.Pause,
        "exception" => DebugStopReason.Exception,
        "entry" => DebugStopReason.Entry,
        "function breakpoint" => DebugStopReason.FunctionBreakpoint,
        "data breakpoint" => DebugStopReason.DataBreakpoint,
        _ => DebugStopReason.Unknown
    };

    private static JsonElement RequireSuccess(DapResponse response)
    {
        if (!response.Success)
        {
            var bodyMessage = response.Body is { } body
                ? OptionalString(body, "message")
                : null;
            throw new InvalidOperationException(
                $"NetCoreDbg command '{response.Command}' failed: " +
                (response.Message ?? bodyMessage ?? "unknown adapter error"));
        }
        return response.Body ?? EmptyObject();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement RequiredArray(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            found.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"NetCoreDbg response requires array property '{property}'.");
        return found;
    }

    private static long RequiredInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            !found.TryGetInt64(out var result))
            throw new InvalidDataException(
                $"NetCoreDbg response requires integer property '{property}'.");
        return result;
    }

    private static int RequiredInt32(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            !found.TryGetInt32(out var result))
            throw new InvalidDataException(
                $"NetCoreDbg response requires integer property '{property}'.");
        return result;
    }

    private static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static bool OptionalBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) &&
        found.ValueKind == JsonValueKind.True;

    private static int? OptionalInt32(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) &&
        found.TryGetInt32(out var result)
            ? result
            : null;

    private static int? OptionalPositiveInt32(JsonElement value, string property)
    {
        var result = OptionalInt32(value, property);
        return result > 0 ? result : null;
    }

    private static string? OptionalObjectString(
        JsonElement value,
        string objectProperty,
        string stringProperty) =>
        value.TryGetProperty(objectProperty, out var found) &&
        found.ValueKind == JsonValueKind.Object
            ? OptionalString(found, stringProperty)
            : null;

    private static string? OptionalDisplayValue(
        JsonElement value,
        string property)
    {
        if (!value.TryGetProperty(property, out var found)) return null;
        return found.ValueKind switch
        {
            JsonValueKind.String => found.GetString(),
            JsonValueKind.Number => found.GetRawText(),
            _ => null
        };
    }
}
