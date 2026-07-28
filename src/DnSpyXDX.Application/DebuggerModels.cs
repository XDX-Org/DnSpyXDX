namespace DnSpyXDX.Application;

public enum DebugRuntimeKind
{
    CoreClr,
    Mono,
    UnityMono
}

public enum DebugSessionStatus
{
    Created,
    Starting,
    Running,
    Paused,
    Stopping,
    Terminated,
    Faulted
}

public enum DebugStopReason
{
    Breakpoint,
    Step,
    Pause,
    Exception,
    Entry,
    FunctionBreakpoint,
    DataBreakpoint,
    Unknown
}

public enum DebugStepKind
{
    Into,
    Over,
    Out
}

public readonly record struct DebugThreadId(long Value);
public readonly record struct DebugFrameId(long Value);
public readonly record struct DebugVariableReference(long Value);
public readonly record struct DebugMethodId(Guid ModuleMvid, int MetadataToken);

/// <summary>
/// Runtime code identity. Decompiled source and PDB source locations are projections of this
/// identity, not substitutes for it.
/// </summary>
public readonly record struct DebugCodeLocation(DebugMethodId Method, int ILOffset);

public sealed record DebugDocumentSequencePoint(
    int StartOffset,
    int Length,
    DebugCodeLocation Location,
    int EndILOffset,
    DebugCodeLocation? BreakpointLocation = null);

public sealed record DebugDocumentMap(
    SymbolId Document,
    IReadOnlyList<DebugDocumentSequencePoint> SequencePoints);

public static class DebugDocumentMaps
{
    public static DebugDocumentSequencePoint? FindByDocumentOffset(
        this DebugDocumentMap map,
        int documentOffset)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (documentOffset < 0) return null;
        return map.SequencePoints
            .Where(point =>
                point.StartOffset <= documentOffset &&
                documentOffset < point.StartOffset + point.Length)
            .OrderBy(point => point.Length)
            .ThenBy(point => point.StartOffset)
            .FirstOrDefault();
    }

    public static DebugDocumentSequencePoint? FindByRuntimeLocation(
        this DebugDocumentMap map,
        DebugCodeLocation location)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.SequencePoints
            .Where(point =>
                point.Location.Method == location.Method &&
                point.Location.ILOffset <= location.ILOffset &&
                location.ILOffset < point.EndILOffset)
            .OrderBy(point => point.EndILOffset - point.Location.ILOffset)
            .ThenBy(point => point.Length)
            .FirstOrDefault();
    }
}

public abstract record DebugStartRequest(DebugRuntimeKind Runtime)
{
    /// <summary>
    /// Breakpoints that must be configured before configurationDone releases a launched target.
    /// </summary>
    public IReadOnlyList<DebugBreakpoint>? InitialBreakpoints { get; init; }
}

public sealed record DebugLaunchRequest(
    DebugRuntimeKind Runtime,
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool StopAtEntry = false) : DebugStartRequest(Runtime);

public sealed record DebugAttachRequest(
    DebugRuntimeKind Runtime,
    int? ProcessId = null,
    string? Host = null,
    int? Port = null) : DebugStartRequest(Runtime);

public sealed record DebuggerCapabilities(
    bool SupportsLaunch,
    bool SupportsAttach,
    bool SupportsFunctionBreakpoints,
    bool SupportsConditionalBreakpoints,
    bool SupportsHitConditions,
    bool SupportsExceptionBreakpoints,
    bool SupportsSetVariable,
    bool SupportsEvaluate,
    bool SupportsDecompiledCodeBreakpoints);

public static class DebuggerCapabilitySets
{
    public static DebuggerCapabilities None { get; } = new(
        SupportsLaunch: false,
        SupportsAttach: false,
        SupportsFunctionBreakpoints: false,
        SupportsConditionalBreakpoints: false,
        SupportsHitConditions: false,
        SupportsExceptionBreakpoints: false,
        SupportsSetVariable: false,
        SupportsEvaluate: false,
        SupportsDecompiledCodeBreakpoints: false);
}

public sealed record DebugBreakpoint(
    Guid Id,
    DebugCodeLocation Location,
    bool Enabled = true,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null);

public sealed record DebugBreakpointBinding(
    Guid BreakpointId,
    bool IsVerified,
    DebugCodeLocation? BoundLocation = null,
    string? Message = null);

public sealed record DebugStopInfo(
    DebugStopReason Reason,
    DebugThreadId Thread,
    DebugCodeLocation? Location = null,
    string? Description = null,
    bool AllThreadsStopped = true);

public sealed record DebugThread(DebugThreadId Id, string Name, bool IsStopped);

public sealed record DebugStackFrame(
    DebugFrameId Id,
    DebugThreadId Thread,
    string Name,
    DebugCodeLocation? Location,
    string? SourcePath = null,
    int? SourceLine = null,
    int? SourceColumn = null,
    string? ModuleName = null,
    string? ModulePath = null);

public sealed record DebugScope(
    string Name,
    DebugVariableReference Variables,
    bool IsExpensive = false);

public sealed record DebugVariable(
    string Name,
    string Value,
    string? Type,
    DebugVariableReference Variables,
    string? EvaluateName = null,
    bool CanSetValue = false);

public sealed record DebugEvaluationResult(
    string Value,
    string? Type,
    DebugVariableReference Variables);

public sealed record DebugOutputMessage(string Category, string Message);

public sealed record DebugSessionSnapshot(
    Guid SessionId,
    DebugRuntimeKind? Runtime,
    DebugSessionStatus Status,
    int? ProcessId,
    DebuggerCapabilities Capabilities,
    DebugStopInfo? Stop,
    string? Error)
{
    public static DebugSessionSnapshot Initial { get; } = new(
        Guid.Empty,
        null,
        DebugSessionStatus.Created,
        null,
        DebuggerCapabilitySets.None,
        null,
        null);
}
