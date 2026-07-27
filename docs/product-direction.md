# Building a Cross-Platform dnSpy-Style Decompiler with Photino and Blazor

> Target: .NET 10, C#, Windows x64, and Linux x64
> Initial scope: open managed assemblies, browse types and members, decompile C#, navigate symbols, and export one or more assemblies as SDK-style projects in an `.slnx` solution.

## Recommendation

Build a new application inspired by dnSpy's workflow rather than trying to port dnSpy's WPF application.

Use:

- **Photino.Blazor** for the native window and Blazor UI host.
- **ICSharpCode.Decompiler** for metadata inspection, C# decompilation, and whole-project export.
- **System.Reflection.Metadata** types exposed by the decompiler package for stable metadata handles and tokens.
- **Monaco Editor** or CodeMirror 6 as the read-only source viewer. Monaco gives the closest IDE-like result.
- A small application layer that owns open assemblies, tabs, navigation history, cancellation, and export jobs.

Do not load inspected DLLs with `Assembly.Load`, `AssemblyLoadContext`, or reflection. Parse them as PE/metadata files only. This keeps opening a DLL independent of its runtime dependencies and avoids executing module initializers or other assembly code.

### Why not port dnSpy directly?

dnSpy is a large, mature debugger and assembly editor built around WPF, MEF, dnlib, Roslyn, and its own desktop service contracts. Its repository separates contracts, decompiler integrations, debugger components, and the application shell, but much of the UI and composition model is Windows-oriented. Replacing WPF while retaining the rest would be a major port, not a UI rewrite.

The dnSpyEx repository is also GPLv3. Copying or adapting its implementation would bring GPL distribution obligations. Treating its feature set and interaction model as inspiration while writing a new implementation around the MIT-licensed `ICSharpCode.Decompiler` package gives a much cleaner technical and licensing boundary. This is an engineering observation, not legal advice.

## Scope the first release carefully

### MVP

The first useful release should support:

1. Open a `.dll` or managed `.exe` from a native file dialog.
2. Reject native-only or invalid PE files with a clear error.
3. Display an expandable tree:
   - assembly/module
   - references and resources
   - namespaces
   - types

   Members are reached through search and source navigation rather than expanded beneath types.
4. Decompile a selected type or member to C# without blocking the UI.
5. Open decompiled documents in tabs.
6. Navigate back/forward and from a method to its declaring type.
7. Navigate clickable type/member references when the target exists in an open assembly.
8. Search by type/member name across open assemblies.
9. Export a selected assembly to an SDK-style `.csproj` and C# files.
10. Export multiple opened assemblies as projects inside one `.slnx`.
11. Publish and smoke-test self-contained `win-x64` and `linux-x64` builds.

### Explicitly defer

Do not include these in the first release:

- debugging or attaching to processes
- editing and rewriting assemblies
- compiling edited C# back into a DLL
- IL editing, metadata table editing, or a hex editor
- BAML-to-XAML reconstruction
- extension/plugin loading
- Unity-specific debugger support

Those are separate products' worth of complexity. The architecture below leaves room for them without making the first release depend on them.

## Future roadmap after the MVP

Add capabilities in this order:

1. [x] IL disassembly view.
2. [x] Analyzer for callers, callees, type uses, derived types, overrides, implementations, instantiation, exposure, and event firing, with navigable lazy results and persisted roots.
3. [ ] Unify workspace search and analysis behind a shared assembly-open index; consider an IDE-style `Used By: N` indicator with searchable exact-location results.
4. [x] Resource viewer and extraction.
5. [ ] Assembly diff and API diff.
6. [ ] PDB/source-link awareness.
7. [ ] BAML/XAML support.
8. [ ] Out-of-process hardened worker.
9. [ ] Assembly editing with dnlib and save-as semantics.
10. [ ] Roslyn-based C# editing/compilation.
11. [ ] Debugging as a separate subsystem.

Assembly editing changes the product's risk profile: it requires metadata preservation, strong-name handling, deterministic backups, verification, and extensive round-trip testing. Do not let it leak into the read-only MVP abstractions prematurely.
