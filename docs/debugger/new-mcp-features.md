# New MCP debugger features

## Implementation status

Implemented on `main`: all proposed tools, transport-session ownership, stop generations and stale
reference rejection, event-driven stop waiting, variable bounds, path and environment controls,
lease expiry, endpoint-shutdown cleanup, activity logging, structured errors, and UI command
isolation. Deterministic tests cover ownership, waiting, stale handles, and UI isolation.

`debug_stop(terminate=false)` detaches while preserving the target. Unity attach is advertised only
through the existing runtime enum and remains unavailable until `main` has a Unity debugger provider.

## Goal

Expose the existing DnSpyXDX debugger through MCP. The MCP layer must use the current
`IDebuggerService` and `DebuggerWorkspace`; it must not introduce a new debugger architecture,
replace debugger engines, or expose runtime objects directly.

Debugging executes target code, so these tools are opt-in, authenticated, bounded, and restricted
to configured MCP roots.

## Proposed tools

| Tool | Purpose |
|---|---|
| `debug_launch` | Launch an allowed managed DLL or executable with bounded arguments and stop-at-entry support |
| `debug_attach` | Attach to an allowed PID or approved Mono/Unity endpoint |
| `debug_set_breakpoints` | Replace the MCP session's breakpoint set using exact managed-code identities |
| `debug_wait_for_stop` | Wait for a pause, breakpoint, step, exception, exit, fault, or timeout |
| `debug_status` | Return process, runtime, state, stop reason, and debugger capabilities |
| `debug_get_threads` | Return threads for a paused session |
| `debug_get_stack` | Return frames and managed runtime locations for one thread |
| `debug_get_scopes` | Return scopes for one frame |
| `debug_get_variables` | Return variables with bounded child expansion |
| `debug_evaluate` | Evaluate an expression in a selected paused frame when supported |
| `debug_continue` | Resume execution |
| `debug_pause` | Pause execution |
| `debug_step` | Step into, over, or out on a selected thread |
| `debug_stop` | Explicitly detach or terminate |

Every result uses structured content. Unsupported operations return capability errors rather than
guessing from the runtime name.

## Typical flow

```text
debug_launch(path="/allowed/project/bin/Debug/net10.0/App.dll")
debug_set_breakpoints(sessionId, [{ moduleMvid, methodToken, ilOffset }])
debug_wait_for_stop(sessionId, timeoutMilliseconds=30000)
debug_get_stack(sessionId, stopGeneration, threadId)
debug_get_variables(sessionId, stopGeneration, frameId, maximumDepth=1)
debug_continue(sessionId)
debug_stop(sessionId, terminate=true)
```

## Identity and breakpoint resolution

Managed locations retain DnSpyXDX's canonical identity:

```text
(module MVID, MethodDef token, IL offset)
```

Breakpoint input may use:

- exact `{ moduleMvid, methodToken, ilOffset }` identity;
- an allowed assembly path plus MethodDef token and IL offset;
- an unambiguous qualified method name plus IL offset;
- a decompiled document position only after resolving it to an exact runtime location.

Breakpoint UUIDs remain stable across replacement. Binding results report verification, the bound
runtime location, and a sanitized diagnostic. MCP must not create a parallel breakpoint identity
scheme.

## MCP session ownership

1. Launch and attach return an opaque MCP debugger session ID.
2. Every later tool requires that ID.
3. One MCP connection owns a debugger session by default; another connection receives an ownership
   error.
4. The activity panel identifies MCP ownership and disables competing UI execution commands while
   MCP controls the session.
5. Ownership is released on stop, termination, fault, endpoint shutdown, or lease expiry.
6. An abandoned session is detached or terminated according to an explicit configured policy.

Ownership should derive from the MCP transport session, not an in-process `McpServer` object, so it
remains stable across tool invocations and cannot leak between clients.

## Paused-state references

Thread, frame, scope, and variable references are valid only for one stop generation.

- Increment the generation whenever execution resumes or a distinct stop arrives.
- Require `stopGeneration` for stack, scope, variable, evaluation, and step operations.
- Return `stale_reference` when a client uses a handle from an earlier stop.
- Return the current state immediately if the stop occurred before `debug_wait_for_stop` began.
- Permit one outstanding wait per MCP debugger session unless fan-out is designed explicitly.
- Implement waiting with debugger state events; do not poll or hold the debugger command gate.

## Limits

Apply explicit limits before calling debugger services:

- launch argument count and total length;
- environment variable count, name length, value length, and allowlist;
- attach host and port policy;
- wait and evaluation timeout;
- maximum threads and frames returned;
- maximum variables, children per node, depth, and value length;
- one active debugger automation session unless multi-session isolation is added later;
- configurable idle lease duration.

Truncation must be visible in structured output. Cancellation propagates through every asynchronous
debugger operation.

## Security

Debugger MCP tools are not read-only.

1. Require the executable, DLL, working directory, and breakpoint assembly paths to be within the
   configured MCP roots.
2. Canonicalize paths, resolve links where practical, and revalidate immediately before use.
3. Never accept a debugger executable or adapter path from MCP.
4. Use an explicit environment-variable allowlist; do not inherit or expose secrets by default.
5. Restrict Mono and Unity endpoints to explicit loopback addresses unless remote debugging has a
   separate opt-in policy.
6. Mark launch, attach, evaluation, execution control, and termination with accurate MCP mutation
   and destructive annotations.
7. Require an explicit `terminate` value for `debug_stop`.
8. Log client, target label, operation, duration, and outcome without arguments, expressions,
   environment values, target output, or variable values.
9. Reuse the MCP server's bearer authentication, origin validation, request timeout, and activity
   interception.

## Host integration

Add a host-owned `DebuggerAutomationService` between MCP tools and existing debugger services:

```text
MCP debugger tools
    |
DebuggerAutomationService
    |-- ownership and lease
    |-- stop generation and stale-handle checks
    |-- limits and sanitized errors
    |
IDebuggerService + DebuggerWorkspace
```

The automation service should:

- subscribe to debugger state changes;
- serialize state-changing commands through existing debugger gates;
- project existing debugger records into MCP DTOs;
- use the workspace's displayed variable names without changing debugger naming behaviour;
- publish ownership state to the UI;
- clean up subscriptions and leases when the MCP endpoint stops.

Keep MCP DTOs separate from application debugger models so the protocol can remain compatible as
internal records evolve.

## Errors

Translate expected failures to stable, sanitized codes:

- `debug_session_active`;
- `debug_session_not_found`;
- `debug_session_owned`;
- `debug_target_not_allowed`;
- `debug_capability_unsupported`;
- `debug_target_not_paused`;
- `stale_reference`;
- `debug_wait_active`;
- `debug_timeout`;
- `limit_exceeded`;
- `invalid_breakpoint`;
- `invalid_endpoint`.

Do not send raw adapter messages, stack traces, full paths, or debugger exception details to MCP
clients.

## Validation

The deterministic integration suite should use the real HTTP MCP transport and a controllable
debug target or fake adapter. Cover:

- tool discovery and structured schemas;
- authentication and MCP connection ownership;
- allowed and rejected launch paths;
- environment allowlisting;
- launch, initial breakpoints, wait, stack, scopes, variables, continue, and stop;
- attach endpoint restrictions;
- cancellation and timeout while waiting;
- stop-before-wait ordering;
- stale frame and variable references after resume and a later stop;
- concurrent wait rejection;
- lease expiry and endpoint shutdown cleanup;
- UI command isolation while MCP owns the session;
- sanitized activity logs and errors.

Live NetCoreDbg, Mono, and Unity tests remain opt-in because they require external runtimes. The
standard MCP suite must remain deterministic and must not require an installed debugger adapter.

## Delivery order

1. Add MCP DTOs, error codes, settings, and transport-session identity.
2. Add `DebuggerAutomationService` ownership, generation, wait, and lease behaviour.
3. Add status, launch/attach, stop, and execution-control tools.
4. Add exact breakpoint resolution and replacement.
5. Add paused-data and evaluation tools with bounds.
6. Add activity-panel ownership state and command isolation.
7. Add end-to-end HTTP tests and security cases.

Exit criteria:

- An MCP client can launch an allowed target, install exact breakpoints before user code runs, wait
  for a stop, inspect bounded paused state, continue, and explicitly detach or terminate.
- A second MCP connection and the UI cannot race execution control with the owner.
- Resumed-state handles are rejected deterministically.
- Cancellation, disconnect, lease expiry, and endpoint shutdown do not leave an unowned session.
