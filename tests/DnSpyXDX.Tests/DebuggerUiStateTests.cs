using DnSpyXDX.Application;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerUiStateTests
{
    public static TheoryData<DebugSessionStatus, bool, bool, bool, bool, bool, bool, bool>
        LifecycleStates => new()
        {
            { DebugSessionStatus.Created, true, false, false, false, false, false, false },
            { DebugSessionStatus.Starting, false, false, false, true, false, false, false },
            { DebugSessionStatus.Running, false, false, true, true, true, false, false },
            { DebugSessionStatus.Paused, false, true, false, true, true, true, true },
            { DebugSessionStatus.Stopping, false, false, false, false, false, false, false },
            { DebugSessionStatus.Terminated, true, false, false, false, true, false, false },
            { DebugSessionStatus.Faulted, true, false, false, false, true, false, false }
        };

    [Theory]
    [MemberData(nameof(LifecycleStates))]
    public void Lifecycle_projects_consistent_command_and_inspection_state(
        DebugSessionStatus status,
        bool canStart,
        bool canContinue,
        bool canPause,
        bool canStop,
        bool canRestart,
        bool canStep,
        bool inspectionAvailable)
    {
        var state = DebuggerUiState.Create(
            Snapshot(status),
            isBusy: false,
            hasStartRequest: true);

        Assert.Equal(canStart, state.CanStart);
        Assert.Equal(canContinue, state.CanContinue);
        Assert.Equal(canPause, state.CanPause);
        Assert.Equal(canStop, state.CanStop);
        Assert.Equal(canRestart, state.CanRestart);
        Assert.Equal(canStep, state.CanStep);
        Assert.Equal(inspectionAvailable, state.InspectionAvailable);
    }

    [Fact]
    public void Busy_command_disables_every_debugger_command()
    {
        var state = DebuggerUiState.Create(
            Snapshot(DebugSessionStatus.Paused),
            isBusy: true,
            hasStartRequest: true);

        Assert.False(state.CanStart);
        Assert.False(state.CanContinue);
        Assert.False(state.CanPause);
        Assert.False(state.CanStop);
        Assert.False(state.CanRestart);
        Assert.False(state.CanStep);
        Assert.True(state.InspectionAvailable);
    }

    [Theory]
    [InlineData(DebugSessionStatus.Starting, true)]
    [InlineData(DebugSessionStatus.Running, true)]
    [InlineData(DebugSessionStatus.Paused, true)]
    [InlineData(DebugSessionStatus.Faulted, true)]
    [InlineData(DebugSessionStatus.Created, false)]
    [InlineData(DebugSessionStatus.Stopping, false)]
    [InlineData(DebugSessionStatus.Terminated, false)]
    public void Lifecycle_controls_automatic_panel_reveal(
        DebugSessionStatus status,
        bool expected)
    {
        var state = DebuggerUiState.Create(
            Snapshot(status),
            isBusy: false,
            hasStartRequest: false);

        Assert.Equal(expected, state.ShouldRevealPanel);
    }

    [Fact]
    public void Status_includes_runtime_process_and_stop_reason()
    {
        var snapshot = Snapshot(DebugSessionStatus.Paused) with
        {
            ProcessId = 42,
            Stop = new(
                DebugStopReason.Breakpoint,
                new DebugThreadId(7))
        };

        var state = DebuggerUiState.Create(snapshot, false, true);

        Assert.Equal(
            "CoreClr: Paused · Breakpoint · PID 42",
            state.StatusText);
    }

    private static DebugSessionSnapshot Snapshot(DebugSessionStatus status) =>
        new(
            Guid.NewGuid(),
            status == DebugSessionStatus.Created
                ? null
                : DebugRuntimeKind.CoreClr,
            status,
            null,
            DebuggerCapabilitySets.None,
            null,
            status == DebugSessionStatus.Faulted
                ? "Adapter disconnected."
                : null);
}
