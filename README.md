# DnSpyXDX

<p align="center">
  <img src="src/DnSpyXDX.UI/wwwroot/images/xdding.webp" width="400" alt="DnSpyXDX animation">
</p>

DnSpyXDX is a focused, read-only assembly browser for Windows and Linux. It combines a native Photino window, a Blazor interface, and the ILSpy decompiler engine to provide a compact desktop workflow for understanding compiled C# applications and libraries.

## Highlights

- Open managed `.dll` and `.exe` files without executing assembly code, from a dialog or by dragging them onto the window
- Lazily browse assemblies, references, resources, namespaces, and types; reach members through search and source navigation
- Decompile complete types or individual members into readable C#
- Switch between C#, IL, and sequence-point-mapped IL with C# views
- Search types, methods, fields, properties, and events across one or all open assemblies
- Analyze symbol relationships including uses, callers, derived types, overrides, implementations, instantiation, exposure, and event firing
- Reveal search results in the assembly tree
- Reopen previously decompiled documents quickly through a content-addressed local cache
- Export open assemblies as SDK-style C# projects in a `.slnx` solution
- Restore open assemblies, document tabs, search state, panel layout, and window placement
- Navigate large trees and source documents with persistent, themed scrollbars
- Read decompiler-derived semantic source highlighting and metadata tokens
- Expose the live workspace to local MCP hosts through an optional authenticated loopback endpoint

## Safety model

DnSpyXDX treats assemblies as untrusted input. It reads PE files and CLI metadata through `ICSharpCode.Decompiler`; it does not inspect files using `Assembly.Load`, reflection, or an `AssemblyLoadContext`.

Malformed files can still consume significant CPU or memory while being parsed or decompiled. Do not treat the current in-process architecture as a security boundary.

The application UI is fully offline-capable: its HTML, CSS, JavaScript, images, and fonts are bundled with the published application. The optional MCP server uses an authenticated loopback HTTP endpoint only when explicitly started from the MCP Activity panel.

## Requirements

- .NET 10 SDK for development
- Windows x64 with WebView2, or
- Linux x64 with GTK 3, WebKitGTK 4.1, and libnotify

Linux package names vary between distributions.

## Run from source

```bash
dotnet restore DnSpyXDX.slnx
dotnet run --project src/DnSpyXDX.Host
```

## Build and test

```bash
dotnet build DnSpyXDX.slnx
dotnet test DnSpyXDX.slnx
```

## Publish

```bash
dotnet publish src/DnSpyXDX.Host -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false
dotnet publish src/DnSpyXDX.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

Trimming, Native AOT, and single-file publishing are intentionally disabled for the initial release.

## Project structure

- `DnSpyXDX.Host` — Photino executable, native dialogs, and session/window persistence
- `DnSpyXDX.UI` — Blazor desktop interface and source presentation
- `DnSpyXDX.Application` — application contracts and workspace state
- `DnSpyXDX.Decompilation` — metadata browsing, analysis, persistent caching, and ILSpy decompilation backend
- `DnSpyXDX.Export` — project export, reports, and `.slnx` generation
- `DnSpyXDX.Tests` — metadata, decompilation, analysis, MCP, export, cache, and presentation tests

The project and namespace names consistently use the DnSpyXDX product identity while retaining clear architectural boundaries.

## Scope

DnSpyXDX currently focuses on read-only inspection and best-effort source export. Debugging, assembly editing, recompilation, IL patching, and metadata rewriting are outside the current scope.

## Documentation

The original implementation guide is now organized into focused documents:

- [Documentation index](docs/README.md)
- [Product direction and scope](docs/product-direction.md)
- [Application architecture](docs/application-architecture.md)
- [Assembly workspace and navigation](docs/assembly-workspace-and-navigation.md)
- [Project export](docs/project-export.md)
- [Dependencies and platform support](docs/dependencies-and-platform.md)
- [Reliability and testing](docs/reliability-and-testing.md)
- [Implementation roadmap](docs/implementation-roadmap.md)
- [Architecture decisions](docs/architecture-decisions.md)
- [Primary references](docs/references.md)

## License

DnSpyXDX is licensed under the [MIT License](LICENSE). Third-party packages and native components retain their respective licenses.
