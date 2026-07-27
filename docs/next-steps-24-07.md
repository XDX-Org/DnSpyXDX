# Next steps — language selection

Prepared on 2026-07-24. This supersedes the delivery order in `next-steps-23-07.md`.

Implementation status: complete and merged into `main`. The automated suite and `linux-x64`/`win-x64` publishes passed during implementation, and interactive GUI smoke testing is complete.

## Decision

Language selection is the next milestone. Decompilation performance measurement moves to the end of the current backlog; performance work should begin earlier only if language-mode implementation exposes a blocking regression.

Ship three modes:

| Mode | Document language key | Output |
| --- | --- | --- |
| C# | `csharp` | Existing high-level C# decompilation |
| IL | `il` | Metadata IL disassembly for the selected type |
| IL with C# | `il-csharp` | IL annotated with the corresponding decompiled C# statements; not two concatenated documents |

C# remains the default. The selector belongs in the main document toolbar, stays visible whenever a document is open, and applies globally to the active and subsequently opened documents.

## Implementation order

### 1. Define the language contract — complete

- Add a `DecompilerLanguage` enum or closed value type in `DnSpyXDX.Application` with stable persisted values for the three modes.
- Change `IDecompilerBackend.DecompileAsync` to accept the selected language explicitly.
- Keep `DecompilerDocument.Language` as the canonical cache/presentation key and return one of the stable values above.
- Reject unsupported values at the backend boundary rather than silently falling back to C#.
- Update backend fakes and callers in one change so the solution always builds.

Acceptance:

- Every decompilation request states its language.
- The returned document language matches the request.
- Existing C# behavior is unchanged.

### 2. Split backend generation by mode — complete

- Retain the existing `CSharpDecompiler` path for C#.
- Add an IL document generator using ILSpy's metadata disassembler and the existing `PEFile`, resolver, metadata token, and cancellation token.
- Keep assembly sessions and resolver state reusable; do not open the assembly again for a mode change.
- Change the assembly-session document cache from metadata token only to `(metadata token, language, settings fingerprint)`.
- Preserve title, symbol identity, diagnostics, and focus-symbol behavior in every mode.
- Produce symbol links/reference spans where ILSpy exposes reliable targets; do not invent identifier-name links for IL operands.

Acceptance:

- A type can be rendered independently in C# and IL.
- Switching back to an already generated mode uses its cached document.
- Cancellation cannot publish a partial document.

### 3. Implement IL with C# mapping — complete

- Use ILSpy syntax trees and IL ranges/sequence information to associate C# statements with method-body IL ranges.
- Render one IL-oriented document with C# annotations adjacent to the IL they describe.
- Define deterministic formatting for unmapped IL, compiler-generated code, async/iterator state machines, and methods without bodies.
- Attach navigation only when the operand or annotation resolves to an exact `SymbolId`.
- Add focused fixtures before expanding coverage; do not implement this mode as C# followed by a full IL dump.

Acceptance:

- Representative methods show C# annotations beside their producing IL ranges.
- Constructors, properties/accessors, generics, async methods, and no-body methods render without failure.
- The output remains useful when only part of a method can be mapped.

### 4. Add the main-panel selector — complete

- Add a labelled, keyboard-accessible selector to `MainWindow.razor` near document navigation.
- Disable it while no document is open; show the current global mode otherwise.
- On change, cancel the active tab's in-flight decompilation and start the new mode for the same symbol.
- Refresh the active tab in place: keep its ID, position, title, assembly, back/forward stacks, and focus target.
- Ignore stale completions by checking both the tab request and selected language.
- Documents opened after a change use the selected mode.

Acceptance:

- Changing mode never creates a tab or history entry.
- Rapid mode changes display only the final selection.
- Back, forward, search navigation, source navigation, and Ctrl+open use the selected mode.

### 5. Preserve view state and cache separation — complete

- Continue using `SourceDocumentKey`; its existing `Language` field must distinguish all three models and token batches.
- Save scroll and active-match state before replacing the active document.
- Restore view state independently for each `(tab, symbol, language, settings)` key when returning to a mode.
- Select a tokenizer by document language. Add IL token classes only where needed; unknown text must remain readable as plain source.
- Ensure closing a tab or assembly clears view state and cached documents for every language.

Acceptance:

- C# and IL retain separate scroll positions in the same tab.
- No C# token batches are reused for IL documents.
- Cache bounds remain enforced across the combined modes.

### 6. Persist and restore the mode — complete

- Add the selected language to `UiSessionState` with a C# default so older `session.json` files remain valid.
- Save the language with each restored document if tabs may contain different modes; otherwise document that restore uses the persisted global mode.
- Restore the selector before document restoration starts.
- Treat an unknown persisted value as C#.

Acceptance:

- Restarting restores the selected language and documents in that language.
- Sessions written by the current release still load.

### 7. Tests and release gate — complete

Add or update tests for:

- Stable language serialization and unknown-value fallback.
- C#, IL, and IL-with-C# backend output for representative fixtures.
- Cache separation by token, language, and settings.
- In-place tab refresh without history mutation.
- Cancellation and stale-result suppression during rapid changes.
- Per-language view-state restoration.
- Session save/restore and compatibility with a session lacking language fields.
- Navigation behavior in each mode, including deliberately unlinked IL operands.

Release follow-up:

1. [x] Run the full test suite.
2. [x] Build the solution with warnings treated as failures where supported.
3. [x] Publish `linux-x64` and `win-x64` using the documented commands.
4. [x] Smoke-test switching all modes, rapid switching, tab history, search navigation, session restart, and a large type.

## Ordered backlog after language selection

1. Indexed workspace search.
2. Reliable multi-project export and project-reference remapping.
3. Hardening: input limits, adversarial fixtures, cancellation, recovery, and cache limits.
4. Release automation, packaging, and licenses.
5. Decompilation performance measurement and optimization.

The deferred performance phase should still separate cold/warm assembly resolution, ILSpy transforms, source generation, link construction, presentation indexing/tokenization, and first viewport render. Optimize only from measured evidence.

## Done definition

The milestone is complete when C#, IL, and mapped IL-with-C# can be selected from the main panel; mode changes replace the active document in place; navigation, cancellation, caches, view state, and sessions are language-aware; compatibility tests pass; and both target RIDs publish successfully.
