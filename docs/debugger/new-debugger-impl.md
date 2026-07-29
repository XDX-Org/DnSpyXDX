# New debugger implementation plan

## Implementation status

Implemented on the `debug` branch:

- versioned, bounded worker protocol with session generation, sequence correlation, structured
  errors, and breakpoint revisions;
- supervised detached workers for CoreCLR, Mono, and Unity Mono;
- separate CoreCLR, Mono, and Unity Mono backend assemblies loaded only by the worker;
- pinned XDX-Org NetCoreDbg payload with DAP contained inside the CoreCLR backend;
- stale breakpoint revision filtering, including the delayed `xdx/ilBreakpoint` regression;
- local name origin, IL slot and lifetime metadata, plus shared decompiler-name projection;
- Unity multicast endpoint discovery, version profiles, remote confirmation, and IL2CPP rejection;
- MCP launch, attach, breakpoint, wait, status, thread, stack, scope, variable, evaluation,
  execution-control, and stop tools with path/endpoint limits, per-connection ownership, and
  session leases;
- opt-in metadata-only worker traces that omit message bodies and debugger values.

Live Unity attachment and assembly-reload verification remain in the opt-in live matrix because
they require a Unity Editor/development-player fixture. Deterministic tests cover discovery packet
parsing, compatibility selection, explicit endpoint translation, and IL2CPP rejection.

The deterministic suite includes an HTTP MCP client driving a detached CoreCLR worker through
launch, ownership rejection, IL breakpoint binding, pause, stack, local variables, and stop.

## Goal

Detach all runtime debuggers from the DnSpyXDX host process. The host retains one debugger UI and
one runtime-independent service. CoreCLR, Mono, and Unity Mono run in separate worker processes
behind the same DnSpyXDX protocol.

```text
DnSpyXDX UI
    |
DebuggerWorkspace
    |
IDebuggerService
    |
Debugger worker client
    |
    +-- CoreCLR worker ---- NetCoreDbg
    +-- Mono worker ------- Mono.Debugger.Soft
    +-- Unity Mono worker - Unity-compatible Mono.Debugger.Soft
```

IL2CPP is excluded. It requires a native debugger and a separate design.

## Principles

1. Keep `(module MVID, MethodDef token, IL offset)` as the canonical managed-code identity.
2. Keep runtime objects, native libraries, adapter processes, and protocol libraries outside the
   host process.
3. Give every worker the same lifecycle and command contract.
4. Keep runtime-specific capability and compatibility logic inside its worker.
5. Treat worker messages as untrusted input and validate them at the host boundary.
6. Make stale-session and stale-breakpoint messages harmless.
7. Migrate one backend at a time without disabling the existing debugger UI.
8. Preserve real local-variable names whenever the runtime, symbols, or decompiler can provide
   them. A backend is not complete if it unnecessarily exposes only synthetic slot names.

## Target projects

```text
src/
  DnSpyXDX.Application/             public debugger models and service contract
  DnSpyXDX.Debugging/               host service and worker client
  DnSpyXDX.Debugging.Protocol/      versioned worker wire contract and framing
  DnSpyXDX.Debugger.Worker/         worker executable and engine dispatch
  DnSpyXDX.Debugging.CoreClr/       NetCoreDbg-backed worker engine
  DnSpyXDX.Debugging.Mono/          Mono.Debugger.Soft worker engine
  DnSpyXDX.Debugging.UnityMono/     Unity discovery and compatibility layer
```

Separate worker executables may be introduced later if native dependency conflicts prevent one
worker executable from loading all engines. The host contract must not depend on that choice.

## Phase 1: freeze the host contract

1. Inventory every operation currently exposed by `IDebuggerEngine` and `IDebuggerService`.
2. Separate commands, replies, and asynchronous events explicitly.
3. Add missing session identity to all worker messages:
   - protocol version;
   - session UUID;
   - monotonically increasing session generation;
   - request sequence;
   - breakpoint-set revision where applicable.
4. Keep application-facing records free of DAP and Mono types.
5. Define capability flags for optional operations rather than runtime-name checks.
6. Record unsupported operations as structured errors.

Exit criteria:

- The complete UI requirement is represented by runtime-independent messages.
- No public contract refers to NetCoreDbg, DAP, `ICorDebug`, or Mono mirror objects.

## Phase 2: create the worker protocol

Use a DnSpyXDX-owned protocol between the host and workers. DAP may remain between the CoreCLR
worker and NetCoreDbg, but it must not define the host-to-worker contract.

Required commands:

- initialize and negotiate protocol version;
- launch and attach;
- terminate and detach;
- continue, pause, step into, step over, and step out;
- replace breakpoints;
- get threads, frames, scopes, variables, and variable children;
- evaluate;
- disconnect worker.

Required events:

- initialized;
- process started;
- stopped and continued;
- breakpoint bindings changed;
- output;
- process exited;
- engine faulted.

Protocol requirements:

1. Use bounded length-prefixed messages over redirected standard I/O.
2. Set maximum header, payload, collection, string, and nesting sizes.
3. Correlate replies with request sequences.
4. Reject messages for another session or protocol version.
5. Ignore late replies to cancelled requests.
6. Preserve structured error codes and diagnostic text.
7. Add protocol serialization and malformed-input tests before moving an engine.

Exit criteria:

- An in-memory fake worker can drive the existing `DebuggerService` tests.
- A test worker can launch, stop, return variables, and exit through the new protocol.

## Phase 3: implement worker supervision

1. Extract the reusable process lifecycle from `DebuggerWorker`.
2. Start workers without shell command parsing.
3. Pass configuration through protocol messages, not environment-global mutable state.
4. Forward worker standard error as diagnostics, never as protocol data.
5. On shutdown:
   - request engine detach or termination;
   - request worker shutdown;
   - close worker input;
   - wait for a bounded grace period;
   - kill the worker process tree if necessary.
6. Distinguish expected exit, engine failure, protocol failure, and forced termination.
7. Discard every event from an obsolete worker generation.

Exit criteria:

- Worker crash and hang tests cannot crash or permanently block DnSpyXDX.
- Restarting creates a new generation and cannot receive old state.

## Phase 4: make breakpoint synchronization revisioned

Replace the current implicit “latest dictionary” scheme.

1. Assign an increasing revision to every complete breakpoint set.
2. Send `sessionId`, `revision`, and stable breakpoint UUIDs with the replace request.
3. Return the same revision with its response.
4. Include the active revision on every later binding event.
5. Apply a response only if its revision is still current.
6. Ignore an event from a retired revision.
7. Reject malformed UUIDs and invalid runtime locations without confusing them with stale events.
8. Include event UUID, revision, and session in diagnostics.
9. Serialize breakpoint replacement per session or explicitly allow concurrent revisions and
   discard superseded results.

Required regression cases:

- event for set A arrives after set B becomes current;
- response for set A arrives after set B;
- breakpoint removed while its module is loading;
- module unload/reload rebinding;
- worker restart with the same UI breakpoint UUIDs;
- malformed event in the current revision.

Exit criteria:

- A stale breakpoint update can never fault or overwrite the current session.

## Phase 5: move CoreCLR behind the worker contract

1. Move `NetCoreDbgEngineProvider` and `NetCoreDbgEngine` into
   `DnSpyXDX.Debugging.CoreClr`.
2. Run that engine inside `DnSpyXDX.Debugger.Worker`.
3. Keep NetCoreDbg as a child process of the worker initially.
4. Keep DAP translation entirely inside the CoreCLR engine.
5. Translate DAP frames, scopes, variables, output, stopped locations, and breakpoint bindings to
   DnSpyXDX worker messages.
6. Replace the current NetCoreDbg UUID-only breakpoint extension with revision-aware messages, or
   have the worker enforce revisions around the existing extension.
7. Keep pinned XDX-Org NetCoreDbg packaging, but package it with the CoreCLR worker payload.
8. Remove direct NetCoreDbg and DAP references from the host after parity tests pass.

Exit criteria:

- Existing CoreCLR tests pass through the worker.
- Killing NetCoreDbg faults only its worker/session.
- The host has no NetCoreDbg process or DAP connection object.

## Phase 6: move Mono behind the worker contract

1. Move `MonoSoftDebuggerEngine`, session factory, and session into
   `DnSpyXDX.Debugging.Mono`.
2. Load `Mono.Debugger.Soft` only inside the worker.
3. Translate Mono mirrors immediately into application records; never send mirror objects or
   runtime handles to the host.
4. Scope thread, frame, and variable references to one paused generation.
5. Invalidate those references on continue, step, detach, exit, or a new stop.
6. Preserve pending IL breakpoints across assembly load and unload.
7. Add bounded attach timeouts and cancellation through worker shutdown.
8. Remove `Mono.Debugger.Soft` references from the host/UI dependency graph.

Exit criteria:

- Current Mono integration tests pass through the worker.
- A Mono protocol failure cannot terminate or poison the DnSpyXDX host.

## Phase 7: add a dedicated Unity Mono provider

Do not make ordinary Mono attach silently guess Unity behaviour.

1. Add `UnityMonoDebuggerEngineProvider` for `DebugRuntimeKind.UnityMono`.
2. Implement Unity Editor and development-player endpoint discovery separately from connection.
3. Show discovered project/player name, address, process identity, and runtime version before
   attaching.
4. Require explicit confirmation for non-loopback targets.
5. Negotiate the Unity Mono protocol version and advertise only supported capabilities.
6. Maintain compatibility profiles for supported Unity generations.
7. Handle Unity assembly reloads as module unload/load and rebind breakpoints by MVID/token/offset.
8. Detect IL2CPP and report it as unsupported managed debugging instead of attempting Mono attach.
9. Keep Unity-specific workarounds in this provider, not in `DebuggerWorkspace`.

Exit criteria:

- Direct Mono and Unity Mono have separate providers and test matrices.
- Assembly reload does not lose stable UI breakpoints.

## Phase 8: simplify the host

After both current engines have moved:

1. Replace runtime engine registrations in `Program.cs` with worker-backed providers.
2. Remove direct DAP, NetCoreDbg, and Mono engine construction from the host.
3. Remove native/runtime debugger dependencies from host publish output except worker payloads.
4. Keep `DebuggerService`, `DebuggerWorkspace`, source mapping, local-name projection, and UI.
5. Verify host startup and assembly browsing work when no debugger payload is installed.
6. Display engine availability and packaging errors before launch/attach.

Exit criteria:

- The host can run normally with every debugger worker absent.
- Installing or updating one debugger backend does not change the host dependency graph.

## Phase 9: guarantee local-variable names

Local-variable names are a required capability, not an optional UI improvement.

Use this precedence:

1. Runtime debugger name when it is meaningful and not synthetic.
2. Portable or Windows PDB local name with its lexical scope/lifetime.
3. Decompiler-derived name for the same method, IL slot, and active IL range.
4. Synthetic `V_<slot>` only when no better name is available.

Requirements:

1. Workers return the IL slot and scope/lifetime when the backend exposes them; do not send only a
   formatted name.
2. Preserve runtime variable references and evaluate names when replacing a display name.
3. Resolve reused slots by current method and IL offset so two lexical locals in one slot do not
   receive the same name incorrectly.
4. Apply the same resolved name in Variables, source hover, IL `.locals`, IL load/store
   instructions, watches, and MCP results.
5. Mark the name origin as `runtime`, `symbols`, `decompiler`, or `synthetic` in the worker model.
6. Do not claim an inferred name is symbol-authored.
7. Cache names by module MVID, MethodDef token, decompiler settings, and module content identity.
8. Invalidate names after module replacement, hot reload, or Unity assembly reload.

Required tests:

- Portable PDB locals retain their names.
- Symbol-less CoreCLR locals map from `V_<slot>` to decompiler names.
- Mono and Unity Mono preserve names supplied by the runtime.
- Reused IL slots select the name whose lifetime contains the stop offset.
- Optimized-away and compiler-generated locals degrade without corrupting other names.
- UI and MCP return the same resolved names for the same paused frame.

Exit criteria:

- Every backend returns the best available local name and passes the shared name-resolution suite.

## Phase 10: add debugger capabilities to MCP

Expose the detached debugger through MCP without giving an MCP client direct access to workers or
runtime objects. MCP tools call a host-owned debugger automation service that uses the same
`IDebuggerService` contracts and breakpoint store as the UI.

Initial tools:

| Tool | Purpose |
|---|---|
| `debug_launch` | Launch a DLL/executable with arguments, working directory, environment allowlist, runtime and stop-at-entry options |
| `debug_attach` | Attach using a PID or an approved Mono/Unity endpoint |
| `debug_set_breakpoints` | Replace the caller's breakpoint set using runtime locations or resolvable symbols/source positions |
| `debug_wait_for_stop` | Wait until breakpoint, step, pause, exception, exit, fault, or timeout |
| `debug_get_threads` | Return threads for the paused session |
| `debug_get_stack` | Return frames and runtime locations for one thread |
| `debug_get_scopes` | Return scopes for one frame |
| `debug_get_variables` | Return named variables and optionally bounded child expansion |
| `debug_evaluate` | Evaluate in a selected paused frame when supported |
| `debug_continue` | Resume execution |
| `debug_step` | Step into, over, or out on a selected thread |
| `debug_pause` | Pause execution |
| `debug_stop` | Detach or terminate according to an explicit argument |
| `debug_status` | Return session, process, state, stop reason and capabilities |

A typical MCP flow is:

```text
debug_launch(path="/allowed/project/bin/Debug/net10.0/App.dll")
debug_set_breakpoints(sessionId, [{ moduleMvid, methodToken, ilOffset }])
debug_wait_for_stop(sessionId, timeoutMs=30000)
debug_get_stack(sessionId, threadId)
debug_get_variables(sessionId, frameId, includeChildren=true)
debug_continue(sessionId)
debug_stop(sessionId, terminate=true)
```

Breakpoint input should support these forms:

- exact `{ moduleMvid, methodToken, ilOffset }` identity;
- assembly path plus MethodDef token and IL offset;
- resolvable qualified method name plus IL offset;
- decompiled document/line only after resolving it to an exact runtime identity.

Every response returns structured content. Stop results include session ID, stop generation, reason,
thread ID, runtime location, matched breakpoint UUID, and description. Variable results include
name, value, type, evaluate name, child reference, whether children are available, and name origin.
The returned name must follow the mandatory local-name rules above.

### MCP session and concurrency rules

1. Return an opaque debugger session ID from launch/attach and require it on every later tool.
2. Scope thread, frame, scope, and variable references to one stop generation.
3. Return `stale_reference` after the target resumes or stops again.
4. Give a debug session one owning MCP client/session by default.
5. Do not let UI and MCP execution commands race; use the existing debugger command gate and show
   MCP ownership/state in the UI.
6. Make `debug_wait_for_stop` cancellable and event-driven. Do not hold a worker lock while waiting.
7. Allow only one outstanding wait per MCP debugger session, or define fan-out explicitly.
8. Bound variable depth, children per node, total returned variables, value length, and evaluation
   time.
9. Return current state when a stop happened before `debug_wait_for_stop` began.
10. Keep breakpoint UUIDs stable across replacement and report their binding state/revision.

### MCP security

Debugging executes target code and is not read-only.

1. Require the DLL/executable and working directory to be inside configured MCP allowed roots.
2. Resolve symlinks and canonical paths before authorization.
3. Do not accept arbitrary debugger executable paths from MCP.
4. Use an explicit environment-variable allowlist; do not expose or inherit secrets by default.
5. Mark launch, attach, execution control, evaluation, and termination tools with accurate MCP
   mutation/destructive annotations.
6. Require an explicit `terminate` choice; default stop behaviour should detach where supported.
7. Restrict remote Mono/Unity endpoints unless separately enabled.
8. Record MCP client, target, operation, duration, and outcome in `McpActivityLog` without logging
   variable values or secrets.
9. Terminate or detach orphaned sessions after a configurable lease timeout.

Exit criteria:

- An MCP client can launch an allowed DLL, install breakpoints before user code runs, wait for a
  hit, receive correctly named locals, continue, and terminate the session.
- Disconnecting or cancelling the MCP request cannot leave an unowned worker indefinitely.
- MCP and UI observe one consistent session state.

## Phase 11: diagnostics

Add an opt-in per-session trace containing:

- timestamps and direction;
- worker/session generation;
- request/event type and sequence;
- breakpoint revision and UUID;
- state transitions;
- worker and adapter exit details.

Redact environment values, evaluated expressions, variable values, command arguments, and target
output by default. Support exporting a sanitized trace for issue reports.

## Phase 12: validation matrix

Minimum automated coverage:

| Area | Cases |
|---|---|
| Protocol | framing, limits, malformed data, cancellation, version mismatch |
| Supervision | normal exit, crash, hang, forced kill, restart |
| Lifecycle | launch, attach, detach, terminate, repeated sessions |
| Breakpoints | pending, bind, remove, disable, stale revision, reload, moved offset |
| Paused data | threads, frames, scopes, nested variables, invalidated handles |
| CoreCLR | symbol-less assembly, optimized code, pinned XDX-Org NetCoreDbg fork |
| Mono | direct attach, assembly load/unload, protocol disconnect |
| Unity Mono | discovery, version profiles, domain/assembly reload, IL2CPP rejection |
| Local names | runtime/PDB/decompiler precedence, reused slots, UI/MCP parity |
| MCP | allowed paths, launch, pre-start breakpoints, wait cancellation, stale handles, ownership |

Run live backend tests separately from deterministic fake-worker tests. The standard test suite
must not require an installed runtime adapter or a running Unity instance.

## Migration order

Implement in this order:

1. Freeze contracts and add session/revision identity.
2. Add worker protocol and fake worker.
3. Add supervision and failure tests.
4. Move CoreCLR without changing visible behaviour.
5. Move Mono without changing visible behaviour.
6. Remove direct debugger dependencies from the host.
7. Enforce the shared local-name contract on every backend.
8. Add the dedicated Unity Mono provider.
9. Add MCP debugger automation after worker/session isolation is stable.
10. Add diagnostic trace export and broaden the live test matrix.

Do not delete the current engines before their worker-backed replacements reach parity. Keep each
migration as a reversible branch-sized change.
