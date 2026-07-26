# Next steps and roadmap review

Reviewed on 2026-07-23 after completion of the virtualized source visualisation milestone.

## Current position

The core read-only workflow is in place: assemblies can be opened, types decompiled, symbols searched and navigated, sessions restored, themes selected, and projects exported. Source presentation now uses a pure-Blazor virtualized viewer with bounded presentation caching and per-tab scroll restoration. The next work should identify why type decompilation is considerably slower than dnSpy before adding language modes, then prioritize navigation accuracy and release confidence.

The assembly tree intentionally stops at types. Members remain discoverable through search and source navigation and open within their declaring type.

## Recommended delivery order

1. **Virtualized source presentation — complete**
   - The pure-Blazor viewer renders visible fixed-height lines and tokenizes cancellable batches away from the UI thread.
   - Per-tab/document scroll state is restored and presentation work is cancelled with its owning view.
2. **Bounded document caching — complete**
   - Model and token-batch LRU caches enforce entry and approximate-memory limits.
   - Unchanged documents and visible ranges remain warm across navigation.
3. **Decompilation performance — deferred**
   - Benchmark cold and warm decompilation for small, large, generic-heavy, and pathological types against dnSpy on the same machine.
   - Trace assembly resolution, ILSpy transforms, source generation, symbol-link construction, document indexing, tokenization, and first-viewport rendering separately.
   - Confirm that navigation and session restoration do not repeat metadata scans, resolver setup, or decompilation for unchanged documents.
   - Review ILSpy settings and transforms for expensive work that dnSpy disables or performs lazily.
   - Add structured timing counters behind the debug-logging switch and regression benchmarks for representative fixtures.
   - Set budgets for time to source text and time to first interactive viewport; optimize the measured bottleneck before changing architecture.
4. **Language modes — complete**
   - Add C#, IL, and IL with C# modes.
   - Key cache entries by symbol, language, settings, and decompiler version.
   - Refresh the active document in place while preserving navigation and selection.
   - Persist the selected mode and support cancellation.
5. **Exact semantic navigation**
   - Produce exact reference spans during decompilation.
   - Resolve those spans across open assemblies.
   - Replace identifier-name heuristics in editor navigation.
6. **Indexed search**
   - Build an index when assemblies open instead of scanning metadata for every query.
   - Keep filtering and cancellation behavior.
   - Design the index so future reference analysis can reuse it.
7. **Reliable multi-project export**
   - Remap references between open assemblies to project references.
   - Report unresolved, framework, and external references separately.
   - Exercise generated solutions with build validation.
8. **Hardening and release engineering**
   - Add file-size, work, cache, and memory limits.
   - Add malformed and adversarial assembly fixtures.
   - Test cancellation, crash recovery, and session restoration.
   - Automate `win-x64` and `linux-x64` publishing.
   - Validate package layouts, bundle licenses, and run GUI smoke tests on both platforms.

## Work completed ahead of earlier backlog items

The implementation did not strictly follow the original backlog order. The following later capabilities shipped while source-presentation work remained open:

- workspace search and navigation
- project and `.slnx` export
- settings and native exit actions
- extensible and persisted themes
- session and layout restoration
- declaring-type navigation for members selected from search or source

This sequencing delivered a broad usable workflow, but source scalability, bounded memory use, and cross-platform release validation now lag behind the feature set.

## Deliberate substitutions and skipped work

| Original direction | Current implementation | Follow-up |
| --- | --- | --- |
| Expand members beneath types in the tree | Tree intentionally stops at types | Update remaining product documentation to describe search/source navigation as the member workflow |
| Virtualized source editor | Pure-Blazor virtualized line viewer | Complete |
| Exact semantic spans | Identifier-name matching | Replace with decompiler-derived spans |
| Indexed workspace search | Metadata scan per query | Add an index before analyzer/reference features |
| Bounded LRU cache | Bounded model and token-batch caches | Complete |
| Automated dual-platform release | Manual publish instructions | Add CI publishing, packaging, licenses, and smoke tests |
| Project-reference mapping | Multi-project output without remapping | Resolve open-assembly references to generated projects |

## Roadmap maintenance changes

- Treat the virtualized editor and large-document pipeline as one milestone.
- Place bounded caching immediately after that milestone because its policy depends on editor models and presentation output.
- Profile and optimize decompilation after the scalable viewer foundation, then implement language modes.
- Split semantic navigation into span production and cross-assembly resolution.
- Track hardening as measurable limits and test suites rather than one broad phase.
- Track release work as separate publishing, packaging, licensing, and platform smoke-test tasks.
- Separate completed capabilities from future milestones once the ordered backlog is next reorganized.

## Documentation follow-up

The type-bounded tree is now the intended product behavior. These existing descriptions should be aligned:

- `README.md` currently says members are browsed in the assembly tree.
- `product-direction.md` lists member nodes in the expandable tree as an MVP requirement.
- Navigation documentation should avoid presenting member expansion as the primary interaction.
