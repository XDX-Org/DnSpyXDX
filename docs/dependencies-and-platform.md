# Dependencies and platform support

## Packages and version policy

Start with centrally managed packages in `Directory.Packages.props`.

| Purpose | Package/asset | Initial policy |
| --- | --- | --- |
| Window + Blazor host | `Photino.Blazor` | Start with stable 4.0.13; prove .NET 10 on both OSes in Phase 0 |
| Decompiler | `ICSharpCode.Decompiler` | Start with stable 10.1.1.8388; do not take an 11 preview for the MVP |
| Logging | `Microsoft.Extensions.Logging` | Match .NET 10 |
| CoreCLR debugger | `NetCoreDbg` | Pin one tested MIT-licensed release per RID; package as a separate native debugger payload |
| Mono debugger | `mono/debugger-libs` | Pin a tested Git submodule commit; build `Mono.Debugger.Soft` for `net6.0` |
| Options/DI | `Microsoft.Extensions.*` | Match .NET 10 |
| Source viewer | Monaco Editor | Pin and vendor/build the required browser assets |
| Unit tests | xUnit or NUnit | Use the team's preference |
| Assertions | FluentAssertions or built-in asserts | Optional |

Photino.Blazor currently targets .NET 8 and is compatible with a .NET 10 consuming project, but compatibility should be demonstrated, not assumed. Photino's last stable NuGet release predates .NET 10, and the project publicly noted a maintenance/workflow transition in 2026. Make the host replaceable and keep all useful code outside it.

Avoid referencing dnSpy's fork of ILSpy or dnSpy contracts unless you intentionally accept the resulting coupling and licensing analysis. The upstream `ICSharpCode.Decompiler` package is the cleaner dependency.

## Cross-platform design and packaging

### Project target

Use a common target framework:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <PlatformTarget>x64</PlatformTarget>
</PropertyGroup>
```

Do not use `net10.0-windows` in shared projects. If a Windows-only helper is ever required, put it in a separate adapter loaded only on Windows.

### Runtime builds

Start with framework-dependent builds during development and self-contained folder publishes for releases:

```bash
dotnet publish src/DnSpyXDX.Host -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false

dotnet publish src/DnSpyXDX.Host -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=false
```

Do not enable trimming, Native AOT, or single-file publishing initially. Decompiler code, Blazor assets, and Photino's native library loading all deserve dedicated tests before those optimizations.

`eng/NetCoreDbg.targets` pins release `3.2.0-1092`. Host builds and publishes:

1. select `win-x64` or `linux-x64` from `RuntimeIdentifier`, falling back to SDK host RID;
2. download matching official archive into project `obj` cache when absent;
3. verify pinned SHA-256 before extraction;
4. copy complete adapter payload into `debuggers/netcoredbg/<RID>`;
5. copy NetCoreDbg MIT license beside RID payloads.

No debugger binary is downloaded at application runtime. First clean build requires GitHub access.
Subsequent builds use cached verified archive. `BundleNetCoreDbg=false` disables acquisition for
offline or externally packaged builds. Unsupported RIDs fail with an actionable build error while
bundling is enabled.

### Mono soft-debugger dependency

`third_party/debugger-libs` pins the maintained `mono/debugger-libs` repository as a Git
submodule. Clone with submodules or run:

```bash
git submodule update --init --recursive
```

The build targets only `Mono.Debugger.Soft` at `net6.0` and explicitly carries its
`Mono.Cecil` runtime dependency; `NuGet.config` includes the official
`dotnet-tools` feed required by its `Microsoft.SymbolStore` and `Microsoft.FileFormats`
dependencies. The upstream MIT license is copied into build and publish outputs. No Mono runtime
is bundled: the target application supplies the Mono runtime and starts its debugger agent.

### Native UI dependencies

- Windows uses WebView2. Detect a missing runtime and show an actionable installation message.
- Linux uses GTK 3, WebKitGTK 4.1, and libnotify in the current Photino native build. Package names vary by distribution. Document Debian/Ubuntu and Arch/CachyOS prerequisites in release notes and fail clearly when a native library cannot be loaded.
- Use native file/folder dialogs exposed by Photino where reliable. Hide them behind `IFileDialogService` so a small platform-specific implementation can replace them if needed.
- Use `Path`, `Path.GetRelativePath`, and `StringComparer` choices deliberately. Linux paths are case-sensitive; Windows paths normally are not.

### CI matrix

At minimum:

| Job | What it proves |
| --- | --- |
| Windows x64 build/test/publish | .NET, WebView2 host, native asset layout |
| Linux x64 build/test/publish | .NET, Photino `.so`, WebKitGTK/GTK dependencies |
| Export golden tests | deterministic `.cs`, `.csproj`, `.slnx`, and report output |
| GUI smoke test | window starts, sample DLL opens, a type renders |

For Linux GUI CI, use an image with GTK/WebKitGTK installed and run the smoke test under Xvfb or a suitable headless compositor. Also test manually on Wayland because browser-control and native-dialog behavior can differ from X11.
