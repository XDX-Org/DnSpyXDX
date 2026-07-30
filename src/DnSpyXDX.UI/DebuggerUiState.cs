using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

/// <summary>
/// One authoritative projection of debugger lifecycle state into UI behavior.
/// Components consume this instead of independently interpreting session status.
/// </summary>
public sealed record DebuggerUiState(
    bool CanStart,
    bool CanContinue,
    bool CanPause,
    bool CanStop,
    bool CanRestart,
    bool CanStep,
    bool InspectionAvailable,
    bool ShouldRevealPanel,
    string StatusText,
    string Guidance)
{
    public static DebuggerUiState Create(
        DebugSessionSnapshot snapshot,
        bool isBusy,
        bool hasStartRequest)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var status = snapshot.Status;
        var paused = status == DebugSessionStatus.Paused;
        var active = status is DebugSessionStatus.Starting or
            DebugSessionStatus.Running or
            DebugSessionStatus.Paused;
        var canStart = status is DebugSessionStatus.Created or
            DebugSessionStatus.Terminated or
            DebugSessionStatus.Faulted;

        return new(
            CanStart: !isBusy && canStart,
            CanContinue: !isBusy && paused,
            CanPause: !isBusy && status == DebugSessionStatus.Running,
            CanStop: !isBusy && active,
            CanRestart: !isBusy &&
                hasStartRequest &&
                status is DebugSessionStatus.Running or
                    DebugSessionStatus.Paused or
                    DebugSessionStatus.Terminated or
                    DebugSessionStatus.Faulted,
            CanStep: !isBusy && paused,
            InspectionAvailable: paused,
            ShouldRevealPanel: status is DebugSessionStatus.Starting or
                DebugSessionStatus.Running or
                DebugSessionStatus.Paused or
                DebugSessionStatus.Faulted,
            StatusText: BuildStatusText(snapshot),
            Guidance: BuildGuidance(snapshot));
    }

    private static string BuildStatusText(DebugSessionSnapshot snapshot)
    {
        if (snapshot.Status == DebugSessionStatus.Created)
            return "Debugger idle";

        var runtime = snapshot.Runtime?.ToString() ?? "Debugger";
        var process = snapshot.ProcessId is { } processId
            ? $" · PID {processId}"
            : "";
        var reason = snapshot.Stop is { } stop
            ? $" · {stop.Reason}"
            : "";
        return $"{runtime}: {snapshot.Status}{reason}{process}";
    }

    private static string BuildGuidance(DebugSessionSnapshot snapshot) =>
        snapshot.Status switch
        {
            DebugSessionStatus.Created =>
                "Start or attach to inspect managed code.",
            DebugSessionStatus.Starting =>
                "Starting debugger and configuring breakpoints…",
            DebugSessionStatus.Running =>
                "Target is running. Pause or wait for a breakpoint to inspect state.",
            DebugSessionStatus.Paused =>
                "Target is paused. Select a thread or frame to inspect state.",
            DebugSessionStatus.Stopping =>
                "Stopping debug session…",
            DebugSessionStatus.Terminated =>
                "Debug session ended. Restart or start another target.",
            DebugSessionStatus.Faulted =>
                snapshot.Error ?? "Debugger failed. Restart or start another target.",
            _ => snapshot.Status.ToString()
        };
}
