using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class WorkspaceAssemblyServiceTests
{
    [Fact]
    public async Task Debugger_module_open_populates_workspace_without_duplicates()
    {
        await using var backend = new DecompilerBackend();
        var workspace = new WorkspaceState();
        var service = new WorkspaceAssemblyService(
            backend,
            workspace,
            new SourceViewStateStore(),
            new SourcePresentationCache(
                NullLogger<SourcePresentationCache>.Instance));
        var path = typeof(WorkspaceAssemblyServiceTests).Assembly.Location;

        var opened = await service.TryOpenDebuggerModuleAsync(path);
        var reopened = await service.TryOpenDebuggerModuleAsync(
            path,
            opened!.ModuleMvid);

        Assert.NotNull(opened);
        Assert.Equal(opened, reopened);
        Assert.Equal(opened, Assert.Single(backend.Assemblies));
    }

    [Fact]
    public async Task Debugger_module_open_rejects_wrong_runtime_identity()
    {
        await using var backend = new DecompilerBackend();
        var service = new WorkspaceAssemblyService(
            backend,
            new WorkspaceState(),
            new SourceViewStateStore(),
            new SourcePresentationCache(
                NullLogger<SourcePresentationCache>.Instance));

        var result = await service.TryOpenDebuggerModuleAsync(
            typeof(WorkspaceAssemblyServiceTests).Assembly.Location,
            Guid.NewGuid());

        Assert.Null(result);
    }
}
