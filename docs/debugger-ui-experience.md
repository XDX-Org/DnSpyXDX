# Debugger UI experience

This document defines the debugger UI lifecycle contract. UI components derive command
availability from `DebuggerUiState`; they do not interpret `DebugSessionStatus` independently.

## Lifecycle states

| State | Start | Continue | Pause | Stop | Restart | Step | Inspection |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Created | enabled | disabled | disabled | disabled | disabled | disabled | unavailable |
| Starting | disabled | disabled | disabled | disabled while busy | disabled | disabled | unavailable |
| Running | disabled | disabled | enabled | enabled | enabled | disabled | unavailable |
| Paused | disabled | enabled | disabled | enabled | enabled | enabled | available |
| Stopping | disabled | disabled | disabled | disabled | disabled | disabled | unavailable |
| Terminated | enabled | disabled | disabled | disabled | enabled after a prior start | disabled | unavailable |
| Faulted | enabled | disabled | disabled | disabled | enabled after a prior start | disabled | unavailable |

All commands are disabled while another debugger command is busy. Runtime frame and variable
handles disappear outside `Paused`; breakpoint identities and debugger output remain.

## Layout

The default **Inspect** view keeps stopped context together:

- thread selector and call stack on the left;
- variables for the selected frame on the right;
- a keyboard-resizable divider between them.

**Breakpoints** and **Output** are focused views instead of permanent narrow columns. Selected
view and bottom-panel size survive application restart. Inspect pane proportions survive locally.

## Inspection

The paused-state value pane preserves debugger scopes instead of flattening Arguments, Locals,
and adapter-specific scopes together. Watches use the selected frame and refresh after every
pause or frame change. Expressions remain across resume and application restart, while results
and expandable runtime handles are cleared as soon as execution continues.

Variable and watch rows expose copy-expression, copy-value, and copy-type actions. The selected
frame and actual stopped instruction use separate visual markers; changing either thread or frame
reveals its decompiled source location.

## Breakpoints

Breakpoint rows show readable assembly, method, decompiled line, and IL-offset labels. Binding
state distinguishes bound, moved, pending, disabled, and rejected breakpoints. Each row has an
editor for condition, hit condition, and log message; unsupported options remain visible but are
explained by capability and binding feedback.

Filtering and enable-all, disable-all, and remove-all actions operate on the persisted breakpoint
set. Breakpoint UUIDs and options survive application restart.

## Start configurations

Named CoreCLR launch, CoreCLR attach, and Mono attach configurations survive application restart.
Launch configurations include target path, one-argument-per-line input, working directory,
environment variables, and stop-at-entry. CoreCLR attach provides a searchable, refreshable list
of local processes hosting CoreCLR plus a manual PID fallback.

Mono attach shows the full host/port target and warns when a non-loopback endpoint would send the
unencrypted debugger protocol over a network. Faulted sessions retain their last start request so
Retry and Restart can recover without re-entering configuration.

## Keyboard contract

- `F5`: start when idle, continue when paused;
- `Shift+F5`: stop;
- `Ctrl+Shift+F5`: restart the last launch or attach;
- `F10`: step over;
- `F11`: step into;
- `Shift+F11`: step out;
- `Ctrl+Alt+Pause`: pause.

Shortcuts never capture keystrokes from inputs, text areas, selects, or editable content.

## Acceptance criteria

1. Every lifecycle state renders deterministic status text, guidance, and command availability.
2. Starting, running, paused, and faulted sessions reveal the debugger panel automatically.
3. Inspect data only appears while paused; resuming immediately removes stale runtime handles.
4. Thread selection reloads frames; frame selection reloads variables and reveals its source.
5. Breakpoints and output remain usable without shrinking stopped-state inspection.
6. Mouse, keyboard, focus, disabled-state, and resize behavior expose equivalent actions.
7. Scope groups, watch expressions, breakpoints, and saved start configurations restore without
   restoring stale runtime handles or evaluation results.
