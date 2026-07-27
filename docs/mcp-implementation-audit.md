# MCP implementation audit

Audited and updated on 2026-07-27 against [mcp-integration-plan.md](mcp-integration-plan.md).

The MCP implementation is a working compatibility spike, but it does not yet satisfy the full Phase 1 MVP or test matrix.

## Coverage

| Area | Status |
| --- | --- |
| Embedded loopback HTTP server | Complete |
| Default-off activity-panel control | Complete |
| Bearer authentication | Complete |
| Origin validation | Partial |
| Live endpoint and configuration copy | Complete |
| `open_assembly`, `close_assembly`, `list_assemblies` | Complete |
| `search_symbols`, `get_symbol`, `get_references` | Functional, incomplete |
| C#, IL, and IL-with-C# resources | Complete |
| Assembly and symbol descriptor resources | Complete |
| Shared desktop/MCP workspace lifecycle | Complete |
| Activity interception and logging | Complete for current protocol surface |
| Roots enforcement | Configured/client root intersection implemented |
| Limits and concurrency controls | Partial |
| Cursor pagination | Implemented for `list_children` |
| Structured MCP errors | Partial; stable coded MCP errors implemented |
| Automated MCP tests | Integration/security suite started |
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

### 2. Implement every advertised resource — complete

Assembly descriptors advertise:

```text
dnspyxdx://assembly/{mvid}
```

Symbol descriptors advertise:

```text
dnspyxdx://assembly/{mvid}/symbol/{token}
```

Both URIs now have `application/json` resource templates. Assembly summaries include identity, platform, references, and an opaque browse-root ID. Symbol descriptors include exact identity, declaring type, and C#/IL/IL-with-C# resource links.

### 3. Add resource and tree enumeration — partial

`list_children` now browses namespaces, types, members, references, and resource nodes through bounded pages with opaque node IDs and node-scoped cursors. Invalid or mismatched cursors return `stale_cursor`.

Also missing:

- embedded safe-text resources;
- direct embedded-resource reads.

### 4. Add operational limits

Search result count and source character count were already bounded. Assembly opening now also has defaults for:

- maximum assembly file size (512 MiB);
- maximum simultaneously open assemblies (32);
- open-operation timeout (30 seconds);
- concurrent open operations (2).

Still add:

- endpoint-wide/per-client concurrency for every handler;
- maximum embedded-resource size;
- pagination for symbol search and reference results;
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

Cancellation continues through protocol cancellation rather than being rewritten. `list_children` adds `invalid_node` and `stale_cursor`; coded errors still need broader wire-level coverage.

## Protocol-surface gaps

- `list_children` is implemented; search and reference results still need cursors.
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

## Activity panel and logging

The panel and protocol interceptor now capture:

- client name from initialization and subsequent session requests;
- initialize, listing, tool, resource, notification, and cancellation envelopes;
- live running rows and completed duration/state;
- exact authorized-request counts;
- bounded retention that evicts completed rows first;
- scoped structured logging with event ID `4100`.

Richer expandable per-row details remain deferred; source, secrets, bodies, and full paths are not captured.

## Workspace integration

Desktop and MCP assembly operations now use `WorkspaceAssemblyService`. Closing through either path:

- notifies desktop consumers to cancel active decompilations;
- removes tabs and history for the module;
- clears source view state and presentation cache entries;
- removes stale search results and selections.

## Testing status

The automated MCP suite now covers absolute/snapshotted roots, live endpoint authentication and Origin rejection, protocol initialization, activity/client capture, assembly and symbol descriptor reads, paginated tree traversal, stale cursors, stale resources after close, and shared workspace close cleanup. All 95 current tests pass. Manual HTTP testing also covered tool discovery, live assembly/search/source reads, coded errors, and clean session deletion.

Still untested:

- malformed JSON-RPC and negotiation failures;
- root traversal and symlinks;
- symlink escape and path replacement races;
- cancellation during open, search, decompilation, and resource reads;
- endpoint restart and port conflicts;
- open-assembly and response limits;
- duplicate assemblies and MVIDs;
- Windows behavior;
- MCP Inspector and two production hosts;
- shutdown with active SSE and resource operations.

## Recommended order

1. [x] Require absolute configured roots, resolve links, snapshot roots, and revalidate after opening.
2. [x] Add assembly file-size, open-count, open-timeout, and concurrent-open limits.
3. [x] Support client-provided MCP roots and intersect them with configured roots.
4. [ ] **Partial:** Stable sanitized MCP error codes and cursor errors are implemented; broader wire-level coverage remains.
5. [x] Add assembly and symbol resources so every returned URI works.
6. [x] Implement paginated `list_children`.
7. [x] Extract shared workspace open/unload coordination.
8. [x] Establish the MCP integration and security test suite; continue expanding the matrix.
9. [x] Complete activity interception and scoped structured logging for the current protocol surface.
10. [ ] Add configurable port and protected token persistence.
11. [ ] Add richer analysis operations and shared indexing.

Phase 0 is functionally demonstrated with initial automated security and lifecycle coverage; Windows verification, MCP Inspector, and two real hosts remain. Phase 1 is substantially implemented: exact descriptor/source resources, tree browsing, configured/client roots, initial limits, coded errors, shared lifecycle, activity interception, and integration tests are present. Embedded-resource reads, search/reference cursors, complete limits, richer symbol data, and broader adversarial coverage remain.
