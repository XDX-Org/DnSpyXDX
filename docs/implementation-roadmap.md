# Implementation roadmap

## Phased implementation plan

The estimates below assume one experienced C# developer working mostly full-time. They are planning ranges, not commitments.

Status was audited against the repository on 2026-07-27 through commit `081edfa`. **Partial** means useful parts exist, but the stated exit condition has not been demonstrated.

| Phase | Status | Deliverable | Estimate | Exit condition |
| --- | --- | --- | ---: | --- |
| 0. Feasibility spike | Complete | Photino + Blazor + .NET 10 shell; ILSpy package loads a test DLL on both OSes | 2–4 days | Shell, decompilation, and launch/decompile smoke tests are complete on Windows and Linux |
| 1. Application skeleton | Partial | Project split, DI, logging, settings, native dialogs, CI builds | 3–5 days | Split projects, DI, filtered logging, session/window state, and dialogs exist; CI and verified dual-RID artifacts do not |
| 2. Assembly workspace | Complete | Open/close sessions, type-bounded lazy tree, metadata/error views | 1–2 weeks | The type-bounded tree is lazy and virtualized; resources, metadata-aware hex inspection, and decompilation error views are implemented |
| 3. Decompiled documents | Complete | Read-only type/member decompilation, tabs, bounded caching, cancellation, and large-document virtualization | 1–2 weeks | The pure-Blazor viewer virtualizes fixed-height lines, caches models/token batches within LRU limits, and restores per-tab scroll state |
| 4. Navigation and search | Partial | Symbol IDs, history, semantic spans, Ctrl+click, indexed name search | 1–2 weeks | History, symbol IDs, cross-assembly decompiler-derived C# links, Ctrl+click, and workspace search exist; an indexed search remains |
| 5. Project and `.slnx` export | Partial | Whole-project adapter, staging, multi-project mapping, reports, optional build | 1–2 weeks | Export, staging, `.slnx`, progress, reports, and optional validation exist; open-assembly references are not remapped to project references |
| 6. Hardening | Partial | Malformed inputs, resource/path safety, memory/concurrency controls, recovery | 1–2 weeks | Basic validation, staging, cancellation, serialized per-session decompilation, and recovery exist; size/memory limits and the adversarial fixture suite do not |
| 7. Release engineering | Partial | installers/archive layout, prerequisites, licenses, smoke tests, docs | 3–5 days | Requirements, manual publish commands, and cross-platform GUI smoke tests are complete; automation, packaged layouts, and license bundling remain |

A realistic read-only MVP is approximately **6–10 weeks** for one developer. Semantic source hyperlinks and reliable multi-project export are the two areas most likely to move the schedule.

## Immediate performance phase

Before language modes, profile the current decompilation path against dnSpy using the same
assemblies and hardware. Measure assembly resolution, ILSpy transforms, source generation,
symbol-link construction, presentation indexing/tokenization, and first interactive paint as
separate stages. Add debug-gated structured timings and representative regression benchmarks,
then optimize the measured bottleneck. The exit condition is documented cold/warm baselines and
budgets for time to source text and time to first interactive viewport.

## First implementation backlog

Build these tickets in order. Checked items are supported by concrete production code and, where practical, tests; partial items remain unchecked.

1. [x] Create the .NET 10 solution and Photino.Blazor host.
2. [ ] **Partial:** Add a Razor three-pane shell: assembly tree, source tabs, and status/output panel. The shell and status bar exist; there is no general output panel.
3. [x] Add `IDecompilerBackend` and a test fake.
4. [x] Implement `AssemblySession` with `PEFile` and `UniversalAssemblyResolver`.
5. [x] Show assembly details, references, resources, namespaces, and types lazily.
6. [x] Decompile a selected type into a plain read-only source view.
7. [x] Add a dnSpy-style main-panel language selector with **C#**, **IL**, and **IL with C#** modes. C# remains the default; IL uses the metadata disassembler; IL with C# annotates IL ranges with sequence-point-mapped C# statements. Mode changes refresh the active document in place, preserve navigation and per-language view state, persist across sessions, support cancellation, and key caches by symbol and language.
8. [x] Add a large-document source pipeline: cache presentation output, tokenize incrementally off the UI thread, render only visible lines, load nearby line ranges on scroll, and cancel pending presentation work when its tab closes.
9. [x] Use the pure-Blazor virtualized viewer and preserve scroll state per tab; Monaco is intentionally not required.
10. [x] Stop interactive tree expansion at types; retain backend member discovery and open members through search and source navigation in their declaring type.
11. [x] Add cancellation, progress, error documents, and a bounded model/token-batch LRU cache.
12. [x] Implement history and symbol identity.
13. [x] Add decompiler-derived semantic reference spans and source navigation, including resolved overloads, extension methods, and exact-MVID cross-assembly navigation with same-folder dependency discovery.
14. [x] Add workspace-wide type/member name search with filtering and debounced UI updates.
15. [x] Add application-menu settings and native application exit actions, including reliable menu dismissal.
16. [x] Add extensible application themes, Rider Dark and VS Dark presets, themed syntax colors, and persisted pre-paint theme restoration.
17. [x] Open member search results in their declaring type and scroll to the selected declaration.
18. [x] Open source-linked members in their declaring type and scroll to the selected declaration.
19. [x] Export one assembly with `WholeProjectDecompiler`.
20. [ ] **Partial:** Add `SlnxWriter`, multi-project export, and project-reference mapping. Solution and multi-project output exist; project-reference remapping does not.
21. [x] Add optional `dotnet build` validation and a persistent export report.
22. [ ] **Partial:** Windows/Linux GUI smoke tests are complete; publishing automation remains.
