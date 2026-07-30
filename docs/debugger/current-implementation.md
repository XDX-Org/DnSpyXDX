# Debugger current implementation

Reviewed on 2026-07-29 from the `debug` branch after the detached-worker migration. This describes
the implemented system, including the pinned NetCoreDbg integration.

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
DebuggerWorkspace                  UI state and decompiler projection
        |
IDebuggerService / DebuggerService lifecycle and snapshots
        |
WorkerDebuggerEngine               versioned DnSpyXDX protocol
        |
DnSpyXDX.Debugger.Worker process
        |----------------------------|
NetCoreDbgEngine              Mono / UnityMono engine
        |                           |
DAP + pinned NetCoreDbg       Mono.Debugger.Soft
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

`DebuggerEngineRegistry` selects one worker-backed provider by `DebugRuntimeKind`:

- `CoreClr` dispatches to `NetCoreDbgEngineProvider` inside the worker.
- `Mono` dispatches to `MonoSoftDebuggerEngineProvider` inside the worker.
- `UnityMono` dispatches to a dedicated Unity compatibility wrapper inside the worker.

The host registers only `WorkerDebuggerEngineProvider`; backend assemblies and Mono dependencies
are absent from the host dependency graph.

## Detached worker protocol

`DnSpyXDX.Debugging.Protocol` defines bounded length-prefixed messages. Every message contains the
protocol version, session UUID, generation, sequence, command/event name, and optional breakpoint
revision. Replies correlate by sequence. Another session/generation is rejected, cancelled late
replies are ignored, and protocol failure faults pending operations.

The host first sends `initialize`; the worker returns its protocol version and supported runtimes,
then emits `initialized`. Starting an engine emits `processStarted`. JSON payloads have explicit
payload, nesting, string, property, and collection limits.

`DnSpyXDX.Debugger.Worker` owns exactly one runtime engine. `eng/DebuggerWorker.targets` publishes
it under `debuggers/worker/<rid>`; the pinned NetCoreDbg payload remains under
`debuggers/netcoredbg/<rid>`. Shutdown requests engine termination/detach, requests worker shutdown,
then kills the process tree after a bounded timeout.

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

`eng/NetCoreDbg.targets` downloads the pinned XDX-Org NetCoreDbg release, verifies its SHA-256,
extracts it under `obj`, and copies it to:

```text
<output>/debuggers/netcoredbg/<rid>/
```

Set `-p:BundleNetCoreDbg=false` only when an external packager intentionally supplies the adapter.
There is no sibling-repository or arbitrary local-build input.

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

### Stale-event handling

Every complete breakpoint set receives an increasing worker revision. Responses echo the revision;
events carry the active revision. The host applies only its current revision. NetCoreDbg events
with a well-formed UUID from a retired set are ignored before strict binding parsing. Malformed
UUIDs and invalid runtime locations still fault the backend.

The former failure was:

```text
Invalid NetCoreDbg xdx/ilBreakpoint event: NetCoreDbg IL breakpoint response contains an unknown or invalid id.
```

That path now has a regression test where set A is removed before its delayed event arrives.

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
Local mappings are removed when their module is unloaded so replacement and Unity-reloaded
assemblies cannot reuse stale inferred names.

## Unity Mono

`UnityMonoEndpointDiscovery` listens for Unity player announcements on multicast group
`225.0.0.222:54997`, accepts only debug-enabled packets, and exposes player/project, address,
debugger protocol version, and loopback status. Remote UI attachment requires explicit
confirmation; MCP attachment remains loopback-only. The dedicated worker backend selects a Unity
generation profile and rejects IL2CPP before opening a Mono connection. Mono assembly load/unload
events rebind the complete stable breakpoint set.

## MCP debugger automation

Debugger tools cover launch/attach, complete breakpoint replacement, event-driven stop waiting,
status, threads, stack, scopes, bounded variables, evaluation, execution control, and explicit
stop semantics. Paths are canonicalized under allowed roots; remote Mono/Unity endpoints are
rejected; debug environment variables default to none and must appear in
`DebugEnvironmentAllowlist`.

Each debug session is bound to the creating MCP connection as well as its opaque session UUID.
Paused handles require the current stop generation and become stale on resume. One stop wait is
allowed per session, abandoned sessions expire by lease, and the UI displays MCP ownership while
disabling competing execution commands.

An end-to-end test uses the real HTTP MCP transport, server, automation service, detached worker,
and fake DAP adapter to verify launch, ownership isolation, IL breakpoints, stop waiting, stack,
local variables, and shutdown.

Set `DNSPYXDX_DEBUG_TRACE_DIRECTORY` to an absolute directory to write one sanitized JSONL trace
per session. Traces retain message/session/generation/sequence, lifecycle event, breakpoint
revision/UUID and structured error code metadata while omitting message bodies, expressions,
arguments, environment values, variables, and target output.

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

The Mono engine uses `Mono.Debugger.Soft` only in the detached worker process. It supports TCP attach,
execution control, threads, frames, scopes, variables, arrays, and MVID/token/IL-offset
breakpoints. It does not use `DebuggerWorker` or DAP. Expression evaluation, object-field
expansion and process launch remain incomplete. Unity uses a distinct provider, rejects IL2CPP,
and selects an explicit compatibility profile when a Unity version is supplied.

## MCP debugger automation

The loopback bearer-authenticated MCP server exposes `debug_launch`, `debug_attach`, breakpoint
replacement, event-driven stop waiting, status, threads, stack, bounded variable trees, evaluation,
continue, pause, step, and explicit stop. Launch and assembly-path breakpoints are restricted to
configured roots with symlink resolution. Mono/Unity MCP attach is loopback-only. Session IDs are
opaque, frame operations require the current stop generation, only one wait is allowed, and idle
sessions expire by lease. Variable results use the same decompiler name projection as the UI.

Optional worker tracing writes direction, timestamp, session/generation, sequence, message name,
breakpoint revision and error code. Message bodies, expressions, arguments, variables, environment,
and target output are not written.

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

The stale `xdx/ilBreakpoint` regression is covered by delivering a delayed event after the complete
breakpoint set has been replaced.
