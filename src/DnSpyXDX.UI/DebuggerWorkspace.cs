using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

public sealed record DebugScopeGroup(
    string Name,
    IReadOnlyList<DebugVariable> Variables);

public sealed record DebugWatch(
    Guid Id,
    string Expression,
    DebugEvaluationResult? Result = null,
    string? Error = null);

/// <summary>
/// UI projection of one debugger session. Runtime handles are discarded whenever execution
/// resumes; IL-native breakpoint identities remain stable across session restarts.
/// </summary>
public sealed class DebuggerWorkspace : IDisposable
{
    private const int MaximumOutputMessages = 1_000;
    private const int MaximumPresentedCollectionItems = 200;
    private readonly IDebuggerService debugger;
    private CancellationTokenSource? refresh;
    private IReadOnlyList<DebugBreakpoint> breakpoints = [];
    private IReadOnlyList<DebugBreakpointBinding> bindings = [];
    private HashSet<Guid> mcpBreakpointIds = [];
    private IReadOnlyList<DebugThread> threads = [];
    private IReadOnlyList<DebugStackFrame> frames = [];
    private IReadOnlyList<DebugScopeGroup> scopeGroups = [];
    private IReadOnlyList<DebugVariable> variables = [];
    private IReadOnlyList<DebugWatch> watches = [];
    private readonly Dictionary<DebugVariableReference, IReadOnlyList<DebugVariable>>
        variableChildren = [];
    private readonly Dictionary<DebugVariableReference, IReadOnlyList<DebugVariable>>
        syntheticVariableChildren = [];
    private readonly Dictionary<DebugVariableReference, int> collectionCounts = [];
    private long nextSyntheticVariableReference;
    private IReadOnlyList<DebugOutputMessage> output = [];
    private DebugThreadId? selectedThread;
    private DebugFrameId? selectedFrame;
    private bool disposed;

    public DebuggerWorkspace(IDebuggerService debugger)
    {
        this.debugger = debugger;
        debugger.StateChanged += OnStateChanged;
        debugger.BreakpointsChanged += OnBreakpointsChanged;
        debugger.OutputReceived += OnOutputReceived;
    }

    public DebugSessionSnapshot Snapshot => debugger.Snapshot;
    public IReadOnlyList<DebugBreakpoint> Breakpoints => breakpoints;
    public IReadOnlyList<DebugBreakpointBinding> Bindings => bindings;
    public IReadOnlyList<DebugThread> Threads => threads;
    public IReadOnlyList<DebugStackFrame> Frames => frames;
    public IReadOnlyList<DebugScopeGroup> ScopeGroups => scopeGroups;
    public IReadOnlyList<DebugVariable> Variables => variables;
    public IReadOnlyList<DebugWatch> Watches => watches;
    public IReadOnlyList<DebugOutputMessage> Output => output;
    public DebugStartRequest? StartRequest { get; private set; }
    public DebugCodeLocation? CurrentLocation =>
        frames.FirstOrDefault(value => value.Id == selectedFrame)?.Location ??
        Snapshot.Stop?.Location ??
        frames.FirstOrDefault()?.Location;
    public DebugThreadId? SelectedThread => selectedThread;
    public DebugFrameId? SelectedFrame => selectedFrame;
    public bool IsBusy { get; private set; }
    public bool IsMcpControlled { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;
    public event Action? PersistentStateChanged;

    public void SetMcpControl(bool active)
    {
        if (IsMcpControlled == active) return;
        IsMcpControlled = active;
        NotifyChanged();
    }

    public DebugBreakpointBinding? BindingFor(Guid breakpointId) =>
        bindings.FirstOrDefault(value => value.BreakpointId == breakpointId);

    public DebugBreakpoint? BreakpointAt(DebugCodeLocation location) =>
        breakpoints.FirstOrDefault(value => value.Location == location);

    public async Task<IReadOnlyList<DebugBreakpointBinding>> SetMcpBreakpointsAsync(
        IReadOnlyList<DebugBreakpoint> values,
        CancellationToken cancellationToken = default)
    {
        breakpoints = breakpoints
            .Where(value => !mcpBreakpointIds.Contains(value.Id))
            .Concat(values)
            .GroupBy(value => value.Id)
            .Select(value => value.Last())
            .ToArray();
        mcpBreakpointIds = values.Select(value => value.Id).ToHashSet();
        await SynchronizeBreakpointsAsync(cancellationToken);
        return bindings.Where(value => mcpBreakpointIds.Contains(value.BreakpointId)).ToArray();
    }

    public void ClearMcpBreakpoints()
    {
        if (mcpBreakpointIds.Count == 0) return;
        breakpoints = breakpoints
            .Where(value => !mcpBreakpointIds.Contains(value.Id))
            .ToArray();
        bindings = bindings
            .Where(value => !mcpBreakpointIds.Contains(value.BreakpointId))
            .ToArray();
        mcpBreakpointIds.Clear();
        NotifyChanged();
    }

    public Task LaunchAsync(
        string executablePath,
        IReadOnlyList<string>? arguments,
        bool stopAtEntry,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var request = new DebugLaunchRequest(
                    DebugRuntimeKind.CoreClr,
                    executablePath,
                    arguments,
                    workingDirectory,
                    environment,
                    StopAtEntry: stopAtEntry)
                {
                    InitialBreakpoints = breakpoints
                };
            StartRequest = request;
            await debugger.StartAsync(request, token);
        }, cancellationToken, requireActiveSession: false, requireUiControl: true);

    public Task AttachAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var request = new DebugAttachRequest(
                    DebugRuntimeKind.CoreClr,
                    ProcessId: processId)
                {
                    InitialBreakpoints = breakpoints
                };
            StartRequest = request;
            await debugger.StartAsync(request, token);
        }, cancellationToken, requireActiveSession: false, requireUiControl: true);

    public Task AttachMonoAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var request = new DebugAttachRequest(
                    DebugRuntimeKind.Mono,
                    Host: host,
                    Port: port)
                {
                    InitialBreakpoints = breakpoints
                };
            StartRequest = request;
            await debugger.StartAsync(request, token);
        }, cancellationToken, requireActiveSession: false, requireUiControl: true);

    public Task ContinueAsync(CancellationToken cancellationToken = default) =>
        RunUiCommandAsync(debugger.ContinueAsync, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        RunUiCommandAsync(debugger.PauseAsync, cancellationToken);

    public Task TerminateAsync(CancellationToken cancellationToken = default) =>
        RunUiCommandAsync(debugger.TerminateAsync, cancellationToken);

    public Task DetachAsync(CancellationToken cancellationToken = default) =>
        RunUiCommandAsync(debugger.DetachAsync, cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (StartRequest is not { } previous)
            return SetErrorAsync("No previous debug launch or attach is available.");

        return RunAsync(async token =>
        {
            if (Snapshot.Status is DebugSessionStatus.Starting or
                DebugSessionStatus.Running or
                DebugSessionStatus.Paused)
                await debugger.TerminateAsync(token);

            DebugStartRequest request = previous switch
            {
                DebugLaunchRequest launch => launch with
                {
                    InitialBreakpoints = breakpoints
                },
                DebugAttachRequest attach => attach with
                {
                    InitialBreakpoints = breakpoints
                },
                _ => throw new InvalidOperationException(
                    $"Unsupported debug start request {previous.GetType().Name}.")
            };
            StartRequest = request;
            await debugger.StartAsync(request, token);
        }, cancellationToken, requireActiveSession: false, requireUiControl: true);
    }

    public Task StepAsync(
        DebugStepKind kind,
        CancellationToken cancellationToken = default)
    {
        if (IsMcpControlled)
            return SetErrorAsync("This debug session is controlled by an MCP client.");
        var thread = Snapshot.Stop?.Thread ?? threads.FirstOrDefault()?.Id;
        return thread is { } id
            ? RunAsync(token => debugger.StepAsync(id, kind, token), cancellationToken)
            : SetErrorAsync("No stopped thread is available for stepping.");
    }

    private Task RunUiCommandAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        IsMcpControlled
            ? SetErrorAsync("This debug session is controlled by an MCP client.")
            : RunAsync(action, cancellationToken);

    public Task ToggleBreakpointAsync(
        DebugCodeLocation location,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var existing = BreakpointAt(location);
            if (existing is null)
                breakpoints =
                [
                    .. breakpoints,
                    new DebugBreakpoint(Guid.NewGuid(), location)
                ];
            else
                breakpoints = breakpoints
                    .Where(value => value.Id != existing.Id)
                    .ToArray();
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task ForceCloseAsync(CancellationToken cancellationToken = default) =>
        RunAsync(
            token => debugger.ForceCloseAsync(token),
            cancellationToken,
            requireActiveSession: false);

    public Task SelectFrameAsync(
        DebugStackFrame frame,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => LoadVariablesAsync(frame, token),
            cancellationToken,
            requireActiveSession: false);

    public Task SelectThreadAsync(
        DebugThread thread,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => LoadThreadAsync(
                thread.Id,
                Snapshot.SessionId,
                token),
            cancellationToken,
            requireActiveSession: false);

    public Task ToggleVariableAsync(
        DebugVariable variable,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            if (variable.Variables.Value == 0) return;
            if (variableChildren.ContainsKey(variable.Variables))
            {
                CollapseVariable(variable.Variables, []);
                NotifyChanged();
                return;
            }

            var children = syntheticVariableChildren.TryGetValue(
                variable.Variables,
                out var synthetic)
                ? synthetic
                : await debugger.GetVariablesAsync(variable.Variables, token);
            children = selectedFrame is { } frame
                ? await RecoverVariableReferencesAsync(children, frame, token)
                : children;
            children = await PresentCollectionAsync(variable, children, token);
            variableChildren[variable.Variables] = children;
            NotifyChanged();
        }, cancellationToken, requireActiveSession: false);

    public bool TryGetVariableChildren(
        DebugVariableReference reference,
        out IReadOnlyList<DebugVariable> children) =>
        variableChildren.TryGetValue(reference, out children!);

    public string DisplayValue(DebugVariable variable) =>
        collectionCounts.TryGetValue(variable.Variables, out var count)
            ? $"{ShortTypeName(variable.Type)} Count = {count}"
            : variable.Value;

    public static string? ShortTypeName(string? type) => type?
        .Replace("System.Collections.Generic.", "", StringComparison.Ordinal)
        .Replace("System.", "", StringComparison.Ordinal)
        .Replace("String", "string", StringComparison.Ordinal)
        .Replace("Boolean", "bool", StringComparison.Ordinal)
        .Replace("Int32", "int", StringComparison.Ordinal)
        .Replace("Int64", "long", StringComparison.Ordinal);

    public Task RemoveBreakpointAsync(
        Guid breakpointId,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            breakpoints = breakpoints
                .Where(value => value.Id != breakpointId)
                .ToArray();
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task SetBreakpointEnabledAsync(
        Guid breakpointId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            breakpoints = breakpoints
                .Select(value => value.Id == breakpointId
                    ? value with { Enabled = enabled }
                    : value)
                .ToArray();
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task UpdateBreakpointAsync(
        Guid breakpointId,
        string? condition,
        string? hitCondition,
        string? logMessage,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            breakpoints = breakpoints
                .Select(value => value.Id == breakpointId
                    ? value with
                    {
                        Condition = NullIfWhiteSpace(condition),
                        HitCondition = NullIfWhiteSpace(hitCondition),
                        LogMessage = NullIfWhiteSpace(logMessage)
                    }
                    : value)
                .ToArray();
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task SetAllBreakpointsEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            breakpoints = breakpoints
                .Select(value => value with { Enabled = enabled })
                .ToArray();
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task RemoveAllBreakpointsAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            breakpoints = [];
            await SynchronizeBreakpointsAsync(token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);

    public Task AddWatchAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
            return SetErrorAsync("Watch expression cannot be empty.");
        if (watches.Any(value =>
            string.Equals(value.Expression, expression, StringComparison.Ordinal)))
            return Task.CompletedTask;

        return RunAsync(async token =>
        {
            var watch = new DebugWatch(Guid.NewGuid(), expression);
            watches = [.. watches, watch];
            if (Snapshot.Status == DebugSessionStatus.Paused &&
                selectedFrame is { } frame)
                await EvaluateWatchAsync(watch.Id, frame, token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);
    }

    public Task UpdateWatchAsync(
        Guid watchId,
        string expression,
        CancellationToken cancellationToken = default)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
            return RemoveWatchAsync(watchId, cancellationToken);

        return RunAsync(async token =>
        {
            watches = watches.Select(value => value.Id == watchId
                ? value with
                {
                    Expression = expression,
                    Result = null,
                    Error = null
                }
                : value).ToArray();
            if (Snapshot.Status == DebugSessionStatus.Paused &&
                selectedFrame is { } frame)
                await EvaluateWatchAsync(watchId, frame, token);
            NotifyPersistentStateChanged();
        }, cancellationToken, requireActiveSession: false);
    }

    public Task RemoveWatchAsync(
        Guid watchId,
        CancellationToken cancellationToken = default) =>
        RunAsync(token =>
        {
            watches = watches.Where(value => value.Id != watchId).ToArray();
            NotifyPersistentStateChanged();
            return Task.CompletedTask;
        }, cancellationToken, requireActiveSession: false);

    public Task RefreshWatchesAsync(
        CancellationToken cancellationToken = default) =>
        selectedFrame is { } frame
            ? RunAsync(
                token => EvaluateWatchesAsync(frame, token),
                cancellationToken,
                requireActiveSession: false)
            : SetErrorAsync("Select a paused stack frame to evaluate watches.");

    public Task ToggleWatchAsync(
        DebugWatch watch,
        CancellationToken cancellationToken = default) =>
        watch.Result is { Variables.Value: not 0 } result
            ? ToggleVariableAsync(
                new DebugVariable(
                    watch.Expression,
                    result.Value,
                    result.Type,
                    result.Variables,
                    watch.Expression),
                cancellationToken)
            : Task.CompletedTask;

    public void RestorePersistentState(
        IReadOnlyList<DebugBreakpoint>? restoredBreakpoints,
        IReadOnlyList<string>? restoredWatches)
    {
        breakpoints = (restoredBreakpoints ?? [])
            .Where(value =>
                value.Id != Guid.Empty &&
                value.Location.Method.ModuleMvid != Guid.Empty &&
                value.Location.Method.MetadataToken > 0 &&
                value.Location.ILOffset >= 0)
            .GroupBy(value => value.Id)
            .Select(value => value.First())
            .ToArray();
        bindings = breakpoints.Select(value => new DebugBreakpointBinding(
            value.Id,
            false,
            Message: "Pending until a debug session starts.")).ToArray();
        watches = (restoredWatches ?? [])
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(value => new DebugWatch(Guid.NewGuid(), value))
            .ToArray();
        NotifyChanged();
    }

    public void ClearOutput()
    {
        output = [];
        NotifyChanged();
    }

    public void ClearError()
    {
        if (Error is null) return;
        Error = null;
        NotifyChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refresh?.Cancel();
        refresh?.Dispose();
        debugger.StateChanged -= OnStateChanged;
        debugger.BreakpointsChanged -= OnBreakpointsChanged;
        debugger.OutputReceived -= OnOutputReceived;
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool requireActiveSession = true,
        bool requireUiControl = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(DebuggerWorkspace));
        IsBusy = true;
        Error = null;
        NotifyChanged();
        try
        {
            if (requireUiControl && IsMcpControlled)
                throw new InvalidOperationException(
                    "This debug session is controlled by an MCP client.");
            if (requireActiveSession &&
                Snapshot.Status is not (DebugSessionStatus.Running or
                    DebugSessionStatus.Paused))
                throw new InvalidOperationException("No active debug session.");
            await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    private Task SetErrorAsync(string message)
    {
        Error = message;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private async Task SynchronizeBreakpointsAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.Status is not (DebugSessionStatus.Running or
            DebugSessionStatus.Paused))
        {
            bindings = breakpoints.Select(value => new DebugBreakpointBinding(
                value.Id,
                false,
                Message: "Pending until a debug session starts.")).ToArray();
            NotifyChanged();
            return;
        }

        bindings = await debugger.SetBreakpointsAsync(
            breakpoints,
            cancellationToken);
        NotifyChanged();
    }

    private void OnStateChanged(DebugSessionSnapshot snapshot)
    {
        if (snapshot.Status != DebugSessionStatus.Paused)
        {
            refresh?.Cancel();
            threads = [];
            frames = [];
            scopeGroups = [];
            variables = [];
            watches = watches
                .Select(value => value with { Result = null, Error = null })
                .ToArray();
            variableChildren.Clear();
            syntheticVariableChildren.Clear();
            collectionCounts.Clear();
            selectedThread = null;
            selectedFrame = null;
        }
        if (snapshot.Status == DebugSessionStatus.Faulted &&
            !string.IsNullOrWhiteSpace(snapshot.Error))
            Error = snapshot.Error;
        NotifyChanged();
        if (snapshot.Status == DebugSessionStatus.Paused)
            QueuePausedRefresh(snapshot.SessionId);
    }

    private void QueuePausedRefresh(Guid sessionId)
    {
        refresh?.Cancel();
        refresh?.Dispose();
        refresh = new CancellationTokenSource();
        _ = RefreshPausedAsync(sessionId, refresh.Token);
    }

    private async Task RefreshPausedAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var loadedThreads = await debugger.GetThreadsAsync(cancellationToken);
            var stoppedThread = Snapshot.Stop?.Thread;
            var initialThread = loadedThreads.FirstOrDefault(
                    value => value.Id == stoppedThread) ??
                loadedThreads.FirstOrDefault();
            if (Snapshot.SessionId != sessionId ||
                Snapshot.Status != DebugSessionStatus.Paused)
                return;
            threads = loadedThreads;
            if (initialThread is not null)
                await LoadThreadAsync(
                    initialThread.Id,
                    sessionId,
                    cancellationToken);
            else
                NotifyChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            NotifyChanged();
        }
    }

    private async Task LoadThreadAsync(
        DebugThreadId thread,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (Snapshot.Status != DebugSessionStatus.Paused)
            throw new InvalidOperationException(
                "Target must be paused to select a debugger thread.");
        var loadedFrames = await debugger.GetStackTraceAsync(
            thread,
            cancellationToken);
        if (Snapshot.SessionId != sessionId ||
            Snapshot.Status != DebugSessionStatus.Paused)
            return;
        selectedThread = thread;
        frames = loadedFrames;
        scopeGroups = [];
        variables = [];
        variableChildren.Clear();
        syntheticVariableChildren.Clear();
        collectionCounts.Clear();
        selectedFrame = null;
        var firstFrame = loadedFrames.FirstOrDefault();
        if (firstFrame is not null)
            await LoadVariablesAsync(firstFrame, cancellationToken);
        else
            NotifyChanged();
    }

    private async Task LoadVariablesAsync(
        DebugStackFrame frame,
        CancellationToken cancellationToken)
    {
        var scopes = await debugger.GetScopesAsync(frame.Id, cancellationToken);
        var loadedGroups = new List<DebugScopeGroup>();
        foreach (var scope in scopes.Where(value =>
            !value.IsExpensive &&
            value.Variables.Value != 0))
        {
            var loaded = await debugger.GetVariablesAsync(
                scope.Variables,
                cancellationToken);
            loaded = await RecoverVariableReferencesAsync(
                loaded,
                frame.Id,
                cancellationToken);
            loadedGroups.Add(new DebugScopeGroup(scope.Name, loaded));
        }
        selectedFrame = frame.Id;
        scopeGroups = loadedGroups;
        variables = loadedGroups.SelectMany(value => value.Variables).ToArray();
        variableChildren.Clear();
        syntheticVariableChildren.Clear();
        collectionCounts.Clear();
        await EvaluateWatchesAsync(frame.Id, cancellationToken);
        NotifyChanged();
    }

    private async Task<IReadOnlyList<DebugVariable>> RecoverVariableReferencesAsync(
        IReadOnlyList<DebugVariable> source,
        DebugFrameId frame,
        CancellationToken cancellationToken)
    {
        if (!Snapshot.Capabilities.SupportsEvaluate) return source;
        var result = new DebugVariable[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var variable = source[index];
            if (variable.Variables.Value == 0 &&
                !string.IsNullOrWhiteSpace(variable.EvaluateName) &&
                !DebugVariableTypes.IsScalar(variable.Type))
            {
                try
                {
                    var evaluated = await debugger.EvaluateAsync(
                        variable.EvaluateName,
                        frame,
                        cancellationToken);
                    if (evaluated.Variables.Value != 0)
                        variable = variable with { Variables = evaluated.Variables };
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Some adapters cannot re-evaluate synthetic variables.
                }
            }
            result[index] = variable;
        }
        return result;
    }

    private async Task<IReadOnlyList<DebugVariable>> PresentCollectionAsync(
        DebugVariable parent,
        IReadOnlyList<DebugVariable> raw,
        CancellationToken cancellationToken)
    {
        if (parent.Type?.StartsWith(
                "System.Collections.Generic.Dictionary<",
                StringComparison.Ordinal) == true)
            return await PresentDictionaryAsync(parent, raw, cancellationToken);
        if (parent.Type?.StartsWith(
                "System.Collections.Generic.List<",
                StringComparison.Ordinal) == true)
            return await PresentListAsync(parent, raw, cancellationToken);
        return raw;
    }

    private async Task<IReadOnlyList<DebugVariable>> PresentDictionaryAsync(
        DebugVariable parent,
        IReadOnlyList<DebugVariable> raw,
        CancellationToken cancellationToken)
    {
        if (!TryReadCount(raw, "_count", out var allocatedCount) ||
            raw.FirstOrDefault(value => value.Name == "_entries") is not { } entries ||
            entries.Variables.Value == 0)
            return raw;

        var count = TryReadCount(raw, "Count", out var publicCount)
            ? publicCount
            : allocatedCount - (TryReadCount(raw, "_freeCount", out var freeCount)
                ? freeCount
                : 0);
        var items = await debugger.GetVariablesAsync(entries.Variables, cancellationToken);
        var presented = new List<DebugVariable>(Math.Min(count, MaximumPresentedCollectionItems) + 2);
        foreach (var item in items.Take(allocatedCount))
        {
            if (item.Variables.Value == 0) continue;
            var fields = await debugger.GetVariablesAsync(item.Variables, cancellationToken);
            if (int.TryParse(fields.FirstOrDefault(value =>
                    value.Name.Equals("next", StringComparison.OrdinalIgnoreCase))?.Value,
                    out var next) && next < -1)
                continue;
            var key = fields.FirstOrDefault(value =>
                value.Name.Equals("key", StringComparison.OrdinalIgnoreCase));
            var value = fields.FirstOrDefault(value =>
                value.Name.Equals("value", StringComparison.OrdinalIgnoreCase));
            if (key is null || value is null) continue;
            presented.Add(value with
            {
                Name = $"[{key.Value.Trim('\"')}]",
                EvaluateName = value.EvaluateName ?? item.EvaluateName
            });
            if (presented.Count == Math.Min(count, MaximumPresentedCollectionItems))
                break;
        }
        if (presented.Count != Math.Min(count, MaximumPresentedCollectionItems))
            return raw;
        collectionCounts[parent.Variables] = count;
        if (count > MaximumPresentedCollectionItems)
            presented.Add(new DebugVariable(
                $"… {count - MaximumPresentedCollectionItems} more",
                "",
                null,
                default));
        presented.Add(CreateRawView(raw));
        return presented;
    }

    private async Task<IReadOnlyList<DebugVariable>> PresentListAsync(
        DebugVariable parent,
        IReadOnlyList<DebugVariable> raw,
        CancellationToken cancellationToken)
    {
        if (!TryReadCount(raw, "_size", out var count) ||
            raw.FirstOrDefault(value => value.Name == "_items") is not { } items ||
            items.Variables.Value == 0)
            return raw;
        var presented = (await debugger.GetVariablesAsync(
                items.Variables,
                cancellationToken))
            .Take(Math.Min(count, MaximumPresentedCollectionItems))
            .ToList();
        if (presented.Count != Math.Min(count, MaximumPresentedCollectionItems))
            return raw;
        collectionCounts[parent.Variables] = count;
        if (count > MaximumPresentedCollectionItems)
            presented.Add(new DebugVariable(
                $"… {count - MaximumPresentedCollectionItems} more",
                "",
                null,
                default));
        presented.Add(CreateRawView(raw));
        return presented;
    }

    private DebugVariable CreateRawView(IReadOnlyList<DebugVariable> raw)
    {
        var reference = new DebugVariableReference(--nextSyntheticVariableReference);
        syntheticVariableChildren[reference] = raw
            .OrderBy(value => value.Name.StartsWith('_'))
            .ToArray();
        return new DebugVariable("Raw View", "", null, reference);
    }

    private static bool TryReadCount(
        IReadOnlyList<DebugVariable> variables,
        string name,
        out int count) =>
        int.TryParse(
            variables.FirstOrDefault(value => value.Name == name)?.Value,
            out count) && count >= 0;

    private async Task EvaluateWatchesAsync(
        DebugFrameId frame,
        CancellationToken cancellationToken)
    {
        foreach (var watch in watches)
            await EvaluateWatchAsync(watch.Id, frame, cancellationToken);
    }

    private async Task EvaluateWatchAsync(
        Guid watchId,
        DebugFrameId frame,
        CancellationToken cancellationToken)
    {
        var watch = watches.FirstOrDefault(value => value.Id == watchId);
        if (watch is null) return;
        if (!Snapshot.Capabilities.SupportsEvaluate)
        {
            ReplaceWatch(watch with
            {
                Result = null,
                Error = "Expression evaluation is unavailable for this debugger."
            });
            return;
        }

        try
        {
            var result = await debugger.EvaluateAsync(
                watch.Expression,
                frame,
                cancellationToken);
            ReplaceWatch(watch with { Result = result, Error = null });
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            ReplaceWatch(watch with
            {
                Result = null,
                Error = exception.Message
            });
        }
    }

    private void ReplaceWatch(DebugWatch replacement)
    {
        watches = watches.Select(value => value.Id == replacement.Id
            ? replacement
            : value).ToArray();
    }

    private void CollapseVariable(
        DebugVariableReference reference,
        HashSet<DebugVariableReference> visited)
    {
        if (!visited.Add(reference) ||
            !variableChildren.Remove(reference, out var children))
            return;
        collectionCounts.Remove(reference);
        foreach (var child in children)
        {
            if (child.Variables.Value != 0)
            {
                CollapseVariable(child.Variables, visited);
                syntheticVariableChildren.Remove(child.Variables);
            }
        }
    }

    private void OnBreakpointsChanged(
        IReadOnlyList<DebugBreakpointBinding> updated)
    {
        bindings = updated;
        NotifyChanged();
    }

    private void OnOutputReceived(DebugOutputMessage message)
    {
        output = output
            .Append(message)
            .TakeLast(MaximumOutputMessages)
            .ToArray();
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private void NotifyPersistentStateChanged() =>
        PersistentStateChanged?.Invoke();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
