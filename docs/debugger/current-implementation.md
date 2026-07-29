# Debugger current implementation

Reviewed on 2026-07-29 from the `debug` branch. This describes the code as it exists, including
the local NetCoreDbg integration and current defects. It is not a proposed architecture.

## Runtime identity

DnSpyXDX identifies managed code with:

```text
(module MVID, MethodDef metadata token, IL offset)
```

`DebugCodeLocation` is the shared identity used by decompiled source maps, breakpoints, stopped
events, and stack frames. Decompiled line and character offsets are projections of this identity;
they are not sent to a runtime debugger.

The public models and `IDebuggerService` contract are in:

- `src/DnSpyXDX.Application/DebuggerModels.cs`
- `src/DnSpyXDX.Application/DebuggerContracts.cs`

## Layers

```text
SourceView / DebuggerView
        |
DebuggerWorkspace                 UI state and decompiler projection
        |
IDebuggerService / DebuggerService lifecycle, serialization and snapshots
        |
IDebuggerEngineRegistry
        |---------------------------|
NetCoreDbgEngine              MonoSoftDebuggerEngine
        |                           |
DebuggerWorker + DapConnection     Mono.Debugger.Soft
        |
local netcoredbg process
```

### Application contract

`DnSpyXDX.Application` contains protocol-independent immutable records. It does not reference DAP,
NetCoreDbg, `ICorDebug`, or Mono debugger objects.

`IDebuggerService` exposes session state, breakpoint bindings, output, execution control, threads,
frames, scopes, variables, and evaluation.

### Session service

`DebuggerService` owns the active `IDebuggerEngine` and the public `DebugSessionSnapshot`. A
semaphore serializes commands. Each engine receives a generation number; events from an engine
whose generation is no longer current are ignored.

The state progression is:

```text
Created -> Starting -> Running <-> Paused -> Stopping -> Terminated
                    \-> Faulted              \-> Faulted
```

Engine events update the snapshot and are forwarded as service events. A malformed adapter event
becomes `DebugEngineFaulted`, which currently moves the complete service session to `Faulted`.

### Engine selection

`DebuggerEngineRegistry` selects one provider by `DebugRuntimeKind`:

- `CoreClr` uses `NetCoreDbgEngineProvider`.
- `Mono` uses `MonoSoftDebuggerEngineProvider`.
- `UnityMono` exists in the model but has no registered provider.

Both implemented providers and the shared service/workspace are registered as singletons in
`src/DnSpyXDX.Host/Program.cs`.

## CoreCLR and NetCoreDbg

### Executable selection

`NetCoreDbgEngineProvider` resolves the executable in this order:

1. `CoreClrDebuggerOptions.NetCoreDbgPath`;
2. `DNSPYXDX_NETCOREDBG_PATH`;
3. `debuggers/netcoredbg/<rid>/netcoredbg` under the application directory;
4. `debuggers/netcoredbg/netcoredbg` under the application directory;
5. `netcoredbg` beside the application;
6. `netcoredbg` on `PATH`.

The provider is unavailable if no candidate exists.

### Build integration

`eng/NetCoreDbg.targets` currently defaults `UseLocalNetCoreDbg` to `true`. Its default local input
is the sibling repository directory `../../netcoredbg/bin/`. Build and publish validate the local
executable and copy that directory to:

```text
<output>/debuggers/netcoredbg/<rid>/
```

Set `-p:UseLocalNetCoreDbg=false` to use the pinned release download instead. The release path
downloads an archive, verifies its SHA-256, extracts it under `obj`, and copies it to the same
runtime location.

### Worker and transport

`DebuggerWorker` starts NetCoreDbg with `--interpreter=vscode` and redirected standard streams.
Standard output is exclusively DAP traffic. Standard error is forwarded as debugger output with
category `stderr`.

`DapConnection` implements `Content-Length` framing, one input loop, request sequence correlation,
serialized writes, event dispatch, reverse-request responses, cancellation, and pending-request
failure when the stream faults. Graceful shutdown sends `disconnect`; after the timeout the worker
kills the adapter process tree.

### Startup sequence

CoreCLR launch or attach uses:

```text
start worker
initialize
launch/attach (request remains pending)
wait for initialized event
xdx/setIlBreakpoints for initial breakpoints
configurationDone
await launch/attach response
```

When initial breakpoints must be installed before user code runs, launch temporarily stops at
entry, installs them, then continues. A decompiled entry-point breakpoint is also used to provide
`StopAtEntry` when the extension is available.

Supported standard DAP operations include continue, pause, step, threads, stack trace, scopes,
variables, evaluate, output, process, stopped, continued, exited, and terminated.

## IL breakpoints

Stock DAP source breakpoints cannot target decompiled text without a real source file. The local
NetCoreDbg fork therefore adds:

- capability `supportsXdxIlBreakpoints`;
- request `xdx/setIlBreakpoints`;
- event `xdx/ilBreakpoint`;
- `xdxLocation` on stopped events and stack frames.

`xdx/setIlBreakpoints` replaces the complete breakpoint set. Every breakpoint has a client-created
UUID plus its MVID, MethodDef token, IL offset, enabled state, condition, hit condition, and log
message. The response must contain exactly one unique binding for every requested UUID.

The engine keeps three related collections:

- `requestedBreakpoints`: current UUID to requested breakpoint;
- `breakpointOrder`: current display order;
- `breakpointBindings`: last binding by UUID.

An `xdx/ilBreakpoint` event is parsed, merged into `breakpointBindings`, reordered using
`breakpointOrder`, then raised as `DebugEngineBreakpointsChanged`.

### Known event race

The current event merge is not robust when breakpoint sets change quickly.

`HandleIlBreakpoint` copies the current `requestedBreakpoints` reference and calls
`ParseIlBreakpointBinding`. That parser rejects an ID unless it occurs in that latest set. A valid
late event for a breakpoint removed or replaced by a newer `xdx/setIlBreakpoints` request therefore
throws:

```text
Invalid NetCoreDbg xdx/ilBreakpoint event: NetCoreDbg IL breakpoint response contains an unknown or invalid id.
```

The later `ContainsKey` check was intended to discard stale events, but parsing throws before that
check is reached. `OnDapEvent` converts the exception to `DebugEngineFaulted`, and
`DebuggerService` faults the complete session.

This can also represent genuinely malformed adapter data, because the diagnostic does not include
the received ID or distinguish a missing/invalid UUID from a well-formed stale UUID.

Areas to investigate on this branch:

1. Log the raw event sequence and UUID together with breakpoint-set revisions.
2. Serialize or revision breakpoint-set requests so responses cannot overwrite newer state.
3. Ignore well-formed event UUIDs that belong to a retired set.
4. Continue faulting on malformed UUIDs or invalid location fields.
5. Add a test where an event from set A arrives after set B becomes current.

The wire contract is documented separately in `docs/netcoredbg-il-breakpoint-protocol.md`.

## Decompiled source and local names

`DecompilerBackend` builds `DebugDocumentMap` data from ILSpy output:

- sequence points map document spans to runtime locations;
- local-name records map method and IL slot to an inferred name and IL lifetime.

`DebuggerSourceMap` maps gutter lines to sequence points and runtime locations back to source
lines. `SourceView` registers each loaded map with `DebuggerWorkspace` and uses it for breakpoint
placement and current-instruction highlighting.

NetCoreDbg can expose symbol-less locals as `V_<slot>`. `DebuggerWorkspace.DisplayVariable`
parses that slot, selects a local-name candidate for the selected method and current IL offset, and
changes only the displayed name. The underlying debugger variable, evaluate name, and variable
reference remain unchanged.

IL and combined IL/C# output also annotate `.locals` and local load/store instructions using these
decompiler-derived names. These are inferred display names, not names returned by NetCoreDbg.

## UI projection

`DebuggerWorkspace` is the stateful UI facade. It owns:

- requested breakpoints and their bindings;
- threads, frames, scopes and flattened variables;
- selected thread/frame;
- expanded variable children;
- registered decompiler local names;
- up to 1,000 output messages;
- busy and error state.

On pause it loads threads, selects the stopped thread, loads its frames, selects the first frame,
then loads all scopes and their variables. Resuming cancels the refresh and clears runtime handles,
frames, variables, and selections. Breakpoint runtime identities remain available between sessions.

`DebuggerView.razor` renders execution controls, threads, call stack, variables, breakpoints, and
debug output. The literal `stdout` category label is hidden, but its message content is retained.

## Mono

The Mono engine directly uses `Mono.Debugger.Soft` in the host process. It supports TCP attach,
execution control, threads, frames, scopes, variables, arrays, and MVID/token/IL-offset
breakpoints. It does not use `DebuggerWorker` or DAP. Expression evaluation, object-field
expansion, process launch, and Unity-specific discovery remain incomplete.

## Tests

The main coverage is in:

- `tests/DnSpyXDX.Tests/DebuggerServiceTests.cs`
- `tests/DnSpyXDX.Tests/DebuggerWorkerTests.cs`
- `tests/DnSpyXDX.Tests/NetCoreDbgEngineTests.cs`
- `tests/DnSpyXDX.Tests/MonoSoftDebuggerEngineTests.cs`
- `tests/DnSpyXDX.Tests/DebuggerWorkspaceTests.cs`
- `tests/DnSpyXDX.Tests/DecompilerBackendTests.cs`

`tests/DnSpyXDX.Debugger.TestWorker` is a controllable DAP child process used for protocol and
worker integration tests. Live NetCoreDbg tests use `DNSPYXDX_NETCOREDBG_PATH` when configured.

The missing regression test most relevant to the current fault is a stale `xdx/ilBreakpoint`
event delivered after the complete breakpoint set has been replaced.
