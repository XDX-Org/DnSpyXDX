# Pure Blazor source visualisation plan

Reviewed on 2026-07-23 for the `visualisation` branch.

## Goal

Replace the full-document highlighted `<pre>` with a read-only, virtualized Blazor source viewer that remains responsive for large decompiled types. Preserve syntax colors, reference navigation, find, declaration focus, block guides, tab view state, themes, keyboard access, and offline operation.

The implementation must remain .NET-centric:

- no Monaco, CodeMirror, npm, Node, frontend bundler, web worker, or CDN assets
- source indexing, tokenization, search, caching, and navigation implemented in C#
- Razor renders only the visible line range through .NET 10 `Virtualize<TItem>`
- browser interop limited to scroll position, viewport measurements, focus, and scrolling to an exact line in the existing `layout.js`

This milestone does not add IL modes, exact semantic-reference production, editing, language servers, or a persistent disk cache.

## Progress

Updated on 2026-07-23. The visualisation milestone is complete and accepted. Linux behavior was manually verified; Windows runtime validation is deferred to external testing.

| Phase | Status | Notes |
| --- | --- | --- |
| 1. Document indexing | Complete | UTF-16 line indexing, line boundaries, maximum visual width, document identity, and metadata-token locations are implemented and tested. |
| 2. Tokenizer | Complete | Stateful C# line tokenization, checkpoints, reference targets, brace metadata, and cancellable background processing are implemented and tested. |
| 3. Virtualized viewer | Complete | The production source view uses `Virtualize<TItem>`, fixed-height rows, overscan, stable width, and hidden scrollbars. Large types are still limited by decompilation time before rendering begins. |
| 4. Viewer features | Complete | Find, Enter/Shift+Enter/Escape, click/Ctrl+click navigation, declaration reveal/flash, occurrence hover, direct-token hover, visible guides, themes, and syntax colors have passed manual checks. |
| 5. View state and caches | Complete | Per-tab/document scroll restoration, bounded model/token-batch LRU caching, eviction cancellation, debug counters, and tab/assembly cleanup passed manual verification. |
| 6. Verification and rollout | Complete | Superseded code is removed, automated tests and both RID publishes pass, and the milestone is accepted. Windows WebView2 runtime validation is tracked as deferred external testing. |

### Implementation notes

- The implemented component names are `SourceView.razor` and `SourceLineView.razor`; a separate `VirtualizedSourceView.razor` was unnecessary.
- Browser interop remains limited to focus, keyboard-boundary handling, viewport scrolling, and waiting for virtualization spacers during deep jumps.
- Scrollbars are visually hidden while wheel, trackpad, and keyboard scrolling remain available.
- Declaration flashes are guarded by document and symbol identity so unrelated focus/render events do not replay them.
- Brace guides are neutral rather than rainbow-colored and stop short of their closing-brace row and row edges.
- Rainbow brace token coloring was removed at user request.
- ILSpy's `IndentSwitchBody` formatting option is enabled. Case labels are indented inside `switch` braces while their statements retain normal case-body indentation.
- Whole-document copy has not been added; the need for it remains a phase 4 follow-up decision rather than a blocker for phase 5.
- Current automated result: 64 tests passing. Both RID publishes passed during rollout; Windows GUI runtime validation remains deferred external testing.

## Why this design

The current `SourceView.razor` highlights the complete document into one HTML string and inserts a span for every token. JavaScript then scans or measures the full DOM for links, find matches, braces, and block guides. CPU, allocations, DOM size, layout work, and disposal time therefore scale with the complete document.

Blazor's `Virtualize<TItem>` calculates the visible item range and renders only that slice plus a configurable overscan. It supports an `ItemsProvider`, exact `ItemSize`, asynchronous loading, cancellation, and `RefreshDataAsync`. Source lines are naturally fixed-height items, so this is a good fit without introducing another UI runtime.

The main trade-off is that browser-native selection cannot span lines that are not currently rendered. This viewer is for inspection rather than editing. Preserve ordinary selection within the rendered window and add an explicit **Copy all source** command if whole-document copying is required.

## Target architecture

```text
DecompilerDocument.Text
        |
        v
SourceDocumentModel ---------------------------+
  line starts, lengths, max width, token map   |
        |                                       |
        v                                       |
Virtualize<SourceLine>                          |
  ItemsProvider requests visible lines         |
        |                                       |
        v                                       |
SourceLine.razor                                |
  Razor-rendered tokens and reference actions  |
                                                |
SourcePresentationCache <-----------------------+
  bounded document and token batches
```

### Responsibilities

`SourceDocumentModel` owns immutable presentation data for one document:

- document key and plain source text
- line start offsets and lengths
- newline convention
- maximum visual column count
- tokenization-state checkpoints
- metadata-token declaration locations
- current heuristic reference targets, later replaceable with exact spans

`VirtualizedSourceView.razor` owns:

- `Virtualize<SourceLine>` and its `ItemsProvider`
- active tab/document binding
- find UI and match navigation
- focus/reveal requests
- visible-range loading and cancellation
- per-tab scroll/view-state capture and restoration

`SourceLine.razor` owns one fixed-height row:

- line number gutter
- token/text fragments
- reference click and Ctrl+click callbacks
- find-match and focused-declaration classes
- indentation and bracket-guide fragments

`SourcePresentationCache` owns:

- bounded document models
- tokenized line batches
- recency and approximate byte accounting
- eviction and cancellation

The existing JavaScript file owns only browser operations Blazor cannot express directly:

- read/write `scrollTop` and `scrollLeft`
- observe viewport size changes
- set a calculated scroll offset for a target line
- focus the scroll container

No JavaScript generates source markup, tokenizes text, searches source, owns document state, or resolves navigation.

## Document and line model

### Stable identity

Key presentation models by:

```text
module MVID + metadata token + language + settings fingerprint
```

The first implementation only uses C#, but the key must leave room for future language modes. A tab is not a document identity: two tabs may show the same document while retaining separate view state.

### Text and offsets

- Keep `DecompilerDocument.Text` as plain text.
- Define all offsets as UTF-16 code-unit offsets, matching .NET strings and browser text offsets.
- Build a line-start table in one pass without creating a string for every line.
- Store each line as `(index, startOffset, length)` and slice the source with `ReadOnlyMemory<char>` or spans while preparing a visible batch.
- Preserve the original newline convention or normalize once before any reference offsets are produced.
- Test CRLF, LF, empty lines, final newlines, tabs, combining characters, and surrogate pairs.

### Fixed row geometry

Virtualization depends on stable item height. Use one exact line height, initially 20 px, and prohibit wrapping. Pass the same value to `Virtualize.ItemSize` and CSS.

Horizontal width must not change as different lines enter the viewport. During indexing, calculate the document's maximum visual column count with the configured tab width. Give the virtualized content canvas a stable minimum width derived from that value plus the line-number gutter.

Cap pathological widths for layout safety and provide horizontal scrolling. Lines beyond the cap remain available through selection/copy and may use a warning marker.

## C# tokenization pipeline

Refactor `CodeHighlighter` into a tokenizer and renderer instead of producing one complete HTML document.

### Token output

```csharp
public readonly record struct SourceToken(
    int Start,
    int Length,
    SourceTokenKind Kind,
    SymbolId? Target = null,
    int? BraceDepth = null,
    int? BracePair = null);

public sealed record SourceLine(
    int Number,
    int StartOffset,
    ReadOnlyMemory<char> Text,
    IReadOnlyList<SourceToken> Tokens);
```

Token offsets are relative to their line; model and reference offsets remain document-relative.

### Stateful lexical scanning

A line cannot always be tokenized independently because block comments, verbatim/raw strings, interpolation, preprocessor regions, and brace depth cross line boundaries. Implement a small immutable `TokenizerState` containing the lexical mode, brace stack/depth, and preprocessor state needed by the next line.

- Scan sequentially in cancellable batches.
- Store state checkpoints every 128–256 lines.
- To render an uncached range, resume at its nearest checkpoint and scan forward.
- Cache the requested range plus overscan.
- Run uncached batch tokenization with `Task.Run` so the Photino UI thread remains responsive.
- Never call Razor component APIs from the worker thread; return immutable results and apply them through `InvokeAsync`.

Reuse the current classifications and tests as the starting behavior. Add coverage for raw strings, interpolation, escaped identifiers, generic types, attributes, operators, and malformed/incomplete source.

### References

The backend currently supplies a name-to-symbol map rather than exact reference spans. Preserve existing behavior during this milestone by resolving only identifier tokens outside comments and literals against that map.

Do not let this compatibility rule become the permanent viewer contract. `SourceToken.Target` and document-relative spans must accept exact backend semantic references later without changing Razor components.

### Brackets and guides

- Carry brace depth/pair information from the tokenizer.
- Apply the seven existing rainbow-brace classes to visible brace tokens.
- Render indentation/bracket guides only for visible lines.
- Precompute compact guide intervals or active depths; never measure every brace in the DOM or build a document-sized SVG.
- Verify nested, unmatched, and braces-inside-comment/string cases.

## Virtualized rendering

Use an `ItemsProvider` instead of allocating a permanent `SourceLine` object for every line.

```razor
<div class="source-viewport" tabindex="0" @ref="viewport">
  <div class="source-canvas" style="min-width:@CanvasWidth">
    <Virtualize @ref="virtualizer"
                Context="line"
                ItemsProvider="LoadLinesAsync"
                ItemSize="LineHeight"
                OverscanCount="Overscan">
      <SourceLineView @key="line.Number" Line="line" OnNavigate="OnNavigate" />
    </Virtualize>
  </div>
</div>
```

Implementation rules:

- Start with 10 overscan lines, then tune from traces.
- Honor `ItemsProviderRequest.CancellationToken` immediately.
- Return the exact total line count.
- Give every row the exact configured height.
- Keep the scroll container focusable for keyboard scrolling.
- Use `@key` with stable line numbers.
- Avoid nested components for individual tokens; render token fragments inside one line component.
- Override `ShouldRender` for line rows when their immutable model and transient highlight version have not changed.
- Avoid handling raw `onscroll` in Blazor; `Virtualize` owns normal scrolling.

## Find and navigation

### Find

Implement document search in C# against the plain source text:

- debounce query changes
- compute matches on a background task with cancellation
- store document offsets, not DOM ranges
- map offsets to line/column by binary-searching the line-start table
- decorate only matches in rendered lines
- keep current match index and total count in Blazor state
- scroll to the selected match by calculated line offset
- support Enter, Shift+Enter, Escape, and Ctrl+F

Search must not force tokenization of every line.

### Reference navigation

Render linkable identifier tokens as buttons or accessible inline elements without changing monospace alignment. Preserve:

- click: navigate in the active tab
- Ctrl+click: open in a new tab
- Alt/Shift: leave text selection alone
- keyboard activation with Enter
- grouped occurrence highlighting within currently rendered lines

Use one `EventCallback<NavigationRequest>` from each line to the source view. Do not attach global DOM queries or one JavaScript listener per token.

### Declaration focus

Build a metadata-token-to-line index from declaration token comments while indexing the document. When search or source navigation supplies `FocusSymbol`:

1. Resolve its line and column in C#.
2. Set the viewport scroll offset to `line * LineHeight`, adjusted to place it near the upper third.
3. Mark the declaration token with a transient focus class.
4. Clear the emphasis after the existing animation without rebuilding the document model.

## View state and lifecycle

Store view state by `(tab ID, document key)`:

```csharp
public sealed record SourceViewState(
    double ScrollTop,
    double ScrollLeft,
    int? ActiveMatch,
    int? SelectionStart,
    int? SelectionLength);
```

Initial implementation requirements:

- capture outgoing scroll offsets before switching documents or tabs
- restore offsets after the new virtualized content renders
- retain separate state when two tabs show the same document
- restore back/forward navigation without re-tokenizing cached ranges
- cancel active find and tokenization work when a tab closes
- release view state on tab close and all related data on assembly unload
- decide separately whether selection/find state should survive application restart

Scroll capture/restore is the one intentional browser-interop dependency. Keep it as a few functions in `layout.js`; all policy and state remain in C#.

## Bounded caching

Use a shared `SourcePresentationCache` registered through dependency injection.

Start with both limits:

- at most 12 document models
- at most 64 MiB estimated presentation memory

Count at minimum:

- source text at two bytes per UTF-16 code unit
- line-index arrays
- tokenizer checkpoints
- cached token arrays and strings
- find-result arrays

Evict least-recently-used inactive documents. The active document is never evicted. Token batches may have a smaller independent budget so a single huge document cannot occupy the entire cache. Eviction must cancel work and release arrays promptly.

Expose counters in debug logging: models, cached batches, estimated bytes, hits, misses, evictions, tokenization duration, and requested line ranges.

## Implementation phases

### 1. Extract document indexing — complete

- Add `SourceDocumentModel`, document keys, line indexing, max-width calculation, and token-location lookup.
- Establish UTF-16/newline rules.
- Add tests for boundary cases and large generated input.

Exit: indexing is deterministic, cancellable where appropriate, and does not allocate one string per source line.

### 2. Extract the tokenizer — complete

- Replace full-document HTML generation with line/batch token output.
- Add tokenizer state and checkpoints.
- Preserve existing syntax, reference, and brace behavior in tests.
- Run batch work off the UI thread.

Exit: arbitrary requested ranges render correctly from their nearest checkpoint.

### 3. Introduce the virtualized viewer — complete

- Add `VirtualizedSourceView.razor` and `SourceLineView.razor`.
- Wire `Virtualize<TItem>` to the document model.
- Add fixed-height rows, stable horizontal canvas width, overscan, keyboard scrolling, and cancellation.
- Replace the production `<pre>` path after parity tests pass.

Exit: DOM size remains proportional to viewport height rather than document length.

### 4. Restore viewer features — complete

- Port reference navigation and occurrence highlighting.
- Port C# find and match decoration.
- Port declaration focus and reveal.
- Render visible brace/indent guides.
- Preserve all application themes.
- Add copy-all behavior if whole-document selection is required.

Exit: the old source viewer can be removed without losing supported workflows.

### 5. Add view state and bounded caches — complete

- Capture and restore per-tab/document scroll state.
- Add model and token-batch LRU limits.
- Dispose state on tab close and assembly unload.
- Prevent unrelated UI renders from rebuilding source models or retokenizing unchanged ranges.

Exit: repeated navigation stays warm while memory plateaus at configured limits.

### 6. Verify and roll out — complete

- Remove superseded full-document highlighting, find, and SVG guide code.
- Run unit, component, publish, and manual GUI tests.
- Exercise Windows WebView2 and Linux WebKitGTK.
- Update the implementation roadmap and reliability documentation.

Exit: the virtualized viewer is the only production source path and both platform smoke tests pass.

## Test strategy

### Unit tests

- document-key stability and uniqueness
- line indexing for LF, CRLF, empty/final lines, and malformed text
- UTF-16 offset-to-line/column conversion with surrogate pairs
- maximum visual columns with tabs and pathological long lines
- tokenizer state across batch/checkpoint boundaries
- every current syntax classification and brace-pair behavior
- reference resolution only for eligible identifier tokens
- search mapping, overlap policy, case handling, and cancellation
- cache accounting, recency, eviction, and cancellation callbacks

### Component tests

- `ItemsProvider` returns the requested range and total count
- cancellation prevents stale range results from applying
- fixed row height and stable keys
- line rows skip unchanged rerenders
- click, Ctrl+click, Alt/Shift, and keyboard reference behavior
- current find/focus decorations appear only on affected visible rows
- changing theme does not rebuild document models

### Integration and GUI tests

- publish and launch on `win-x64` and `linux-x64`
- open, decompile, scroll, switch tabs, find, navigate, change theme, close, and restore
- verify keyboard scrolling by focusing the source viewport
- exercise 1 MiB, 5 MiB, and 25 MiB generated C# fixtures
- include very long lines, raw strings, deep braces, and many references
- repeatedly open/close large documents and verify memory returns to the configured bound
- confirm the application remains fully offline and has no Node/npm-generated assets

### Initial performance budgets

Record hardware and WebView versions. Refine these budgets after the first measured prototype, but replace rather than silently remove them.

| Scenario | Initial budget |
| --- | ---: |
| Switch to a cached document | 200 ms to keyboard/scroll input |
| Close a large active document | 100 ms UI-thread response |
| Show first viewport of a 5 MiB cached document | 1 s |
| Scroll a 5 MiB document | No repeated UI-thread tasks over 100 ms |
| Rendered source rows | Viewport rows plus configured overscan only |
| Repeat open/close cycle | Memory plateaus within cache limits |

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Fixed-height assumptions drift with zoom/fonts | Derive CSS and `ItemSize` from one setting; measure after zoom and refresh virtualization |
| Horizontal scrollbar width jumps | Precompute a stable canvas width from maximum visual columns |
| Stateful tokens render incorrectly when jumping | Resume from tested tokenizer checkpoints rather than tokenizing lines independently |
| Too many Razor frames per line | Use one line component and render lightweight fragments; benchmark before adding abstractions |
| Browser selection cannot cross unrendered lines | Document the read-only limitation and provide Copy all source |
| Scroll restoration races rendering | Restore after render, use a document/version guard, and discard stale requests |
| Rapid scroll causes excess work | Honor cancellation, tune overscan, cache batches, and avoid Blazor `onscroll` handlers |
| Reference heuristic remains ambiguous | Keep the token target contract span-ready and replace names in the semantic-navigation milestone |
| One large document monopolizes memory | Separate token-batch budget from model budget and evict inactive ranges first |
| Accessibility regresses | Focusable viewport, semantic line numbers, keyboard links, sensible ARIA labels, and screen-reader testing |

## Decisions to validate in the prototype

- Exact line height and overscan at each supported application zoom.
- Checkpoint interval that balances jump latency and memory.
- Whether line rendering should use a hand-written `RenderTreeBuilder` component or ordinary Razor loops.
- Stable horizontal-width cap for pathological generated lines.
- Cache limits after measuring representative Unity and desktop assemblies.
- Whether whole-document Copy all is required for initial parity.
- Which view-state fields, beyond scroll offsets, should persist across application restarts.

## Primary references

- [ASP.NET Core Razor component virtualization (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization?view=aspnetcore-10.0)
- [`Virtualize<TItem>` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.web.virtualization.virtualize-1?view=aspnetcore-10.0)
- [Blazor rendering performance guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering?view=aspnetcore-10.0)
- [Blazor JavaScript interop and element references](https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/call-javascript-from-dotnet?view=aspnetcore-10.0)
