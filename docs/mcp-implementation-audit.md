# MCP implementation audit

Audited and updated on 2026-07-27 against [mcp-integration-plan.md](mcp-integration-plan.md).

The MCP implementation is a working compatibility spike, but it does not yet satisfy the full Phase 1 MVP or test matrix.

## Coverage

| Area | Status |
| --- | --- |
| Embedded loopback HTTP server | Complete |
| Default-off Settings switch | Complete |
| Bearer authentication | Complete |
| Origin validation | Partial |
| Live endpoint and configuration copy | Complete |
| `open_assembly`, `close_assembly`, `list_assemblies` | Complete |
| `search_symbols`, `get_symbol`, `get_references` | Functional, incomplete |
| C#, IL, and IL-with-C# resources | Complete |
| Shared desktop/MCP workspace | Mostly complete |
| Activity panel | Partial |
| Roots enforcement | Configured/client root intersection implemented |
| Limits and concurrency controls | Partial |
| Cursor pagination | Missing |
| Structured MCP errors | Partial; stable coded MCP errors implemented |
| Automated MCP tests | Started; settings/root snapshot coverage only |
| Host/platform compatibility verification | Partial, manual only |

## Highest-priority gaps

### 1. Harden root enforcement

`AssemblyTools` now:

- requires and normalizes absolute configured roots;
- resolves links in roots and assembly paths before containment checks;
- uses an immutable root snapshot for each open operation;
- requests roots from capable MCP clients and requires the path to satisfy both client and configured roots;
- revalidates after opening and rolls back the backend session on failure.

Clients without roots capability remain constrained by configured roots. A roots-capable client returning no usable file roots cannot open a path. Path-based opening cannot completely eliminate filesystem time-of-check/time-of-use races; hardened deployments still need a handle-based or isolated-worker boundary.

### 2. Implement every advertised resource

Assembly descriptors advertise:

```text
dnspyxdx://assembly/{mvid}
```

Symbol descriptors advertise:

```text
dnspyxdx://assembly/{mvid}/symbol/{token}
```

Neither URI has a registered resource template. Only `/source/{language}` works. Add assembly-summary and symbol-descriptor resources or stop returning the unavailable links.

### 3. Add resource and tree enumeration

The planned `list_children` tool is missing. A host cannot browse namespaces, types, references, or embedded resources without knowing a search term.

Also missing:

- assembly summary resources;
- symbol descriptor resources;
- embedded safe-text resources;
- reference and resource enumeration.

### 4. Add operational limits

Search result count and source character count were already bounded. Assembly opening now also has defaults for:

- maximum assembly file size (512 MiB);
- maximum simultaneously open assemblies (32);
- open-operation timeout (30 seconds);
- concurrent open operations (2).

Still add:

- endpoint-wide/per-client concurrency for every handler;
- maximum embedded-resource size;
- page size and cursor limits;
- aggregate cache budget.

Without these, a large or hostile assembly can consume excessive CPU, memory, or file handles.

### 5. Return structured errors

Assembly, symbol, and source handlers now translate expected failures into sanitized MCP errors with stable prefixes, including:

```text
An error occurred invoking 'open_assembly'.
```

- `path_not_allowed`;
- `assembly_not_open`;
- `symbol_not_found`;
- `invalid_token`;
- `limit_exceeded`;
- `invalid_language`.

Cancellation continues through protocol cancellation rather than being rewritten. Cursor support must add `stale_cursor`, and coded errors still need integration tests confirming the exact wire representation and structured client consumption.

## Protocol-surface gaps

- `list_children` is absent.
- `search_symbols` has no cursor and scans the full workspace before truncating.
- Search results are not explicitly ranked.
- `get_symbol` lacks signature and visibility information.
- `get_references` only implements outgoing `Uses` relations.
- Source truncation cuts at an arbitrary character boundary.
- C# resources use `text/plain` rather than `text/x-csharp`.
- Resource-list change notifications are not emitted when assemblies open or close.

## Lifecycle and Settings gaps

- `McpPort` exists but is neither persisted nor configurable.
- The bearer token is regenerated each process, invalidating saved host configuration after restart.
- Protected per-user token storage is not implemented.
- Only IPv4 loopback is bound; `::1` is not.
- The endpoint path is predictable (`/mcp`).
- Port changes cannot restart only the MCP endpoint because there is no port control.

## Activity-panel gaps

The panel covers endpoint status, counts, completed operations, and clearing. It does not yet show or capture:

- client name;
- initialize and `tools/list` requests;
- queued/running rows;
- cancellation notifications;
- per-row structured details;
- logging event IDs and scopes;
- tool activity through `Microsoft.Extensions.Logging`.

The current request count covers implemented handlers rather than every MCP request.

## Workspace-integration gaps

MCP open operations notify the UI. MCP close removes tabs through `WorkspaceState`, but does not use the complete desktop unload path:

- active decompilations are not explicitly cancelled;
- `SourceViewStateStore` is not cleared;
- `SourcePresentationCache` is not cleared;
- search selections and results can remain stale.

Move workspace open/unload coordination into a shared application service used by UI and MCP.

## Testing status

Automated coverage now verifies absolute configured roots and immutable root snapshots. The broader planned `tests/DnSpyXDX.Tests/Mcp/` integration/security suite is still missing. All 92 current tests pass, and manual HTTP testing covered the happy path, authentication, resource reads, invalid paths, and clean session deletion.

Still untested:

- malformed JSON-RPC and negotiation failures;
- token and Origin rejection combinations;
- root traversal and symlinks;
- symlink escape and path replacement races;
- cancellation during open, search, decompilation, and resource reads;
- endpoint restart and port conflicts;
- open-assembly and response limits;
- stale resources after close;
- duplicate assemblies and MVIDs;
- Windows behavior;
- MCP Inspector and two production hosts;
- shutdown with active SSE and resource operations.

## Recommended order

1. [x] Require absolute configured roots, resolve links, snapshot roots, and revalidate after opening.
2. [x] Add assembly file-size, open-count, open-timeout, and concurrent-open limits.
3. [x] Support client-provided MCP roots and intersect them with configured roots.
4. [ ] **Partial:** Stable sanitized MCP error codes are implemented; wire-level integration coverage and cursor errors remain.
5. [ ] Add assembly and symbol resources so every returned URI works.
6. [ ] Implement paginated `list_children`.
7. [ ] Extract shared workspace open/unload coordination.
8. [ ] Add the full MCP integration and security test suite.
9. [ ] Complete activity interception and logging.
10. [ ] Add configurable port and protected token persistence.
11. [ ] Add richer analysis operations and shared indexing.

Phase 0 is functionally demonstrated, but its formal exit criteria still require automated security and lifecycle coverage, Windows verification, MCP Inspector, and two real hosts. Phase 1 remains partial: configured/client root enforcement, initial assembly-open limits, and stable coded errors are implemented, while resources, enumeration, pagination, complete limits, and integration tests remain.
