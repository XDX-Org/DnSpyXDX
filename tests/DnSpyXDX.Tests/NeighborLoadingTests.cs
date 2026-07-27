using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class NeighborLoadingTests
{
    // The test assembly references DnSpyXDX.Decompilation, copied into the same output directory, so on-demand
    // loading should promote it to its own session in the background.
    private const string LocalNeighbor = "DnSpyXDX.Decompilation";

    [Fact]
    public async Task Reopens_an_assembly_after_unloading_everything()
    {
        await using var backend = new DecompilerBackend();
        var path = typeof(NeighborLoadingTests).Assembly.Location;

        await backend.OpenAsync(path);
        Assert.NotEmpty(backend.Assemblies);

        await backend.CloseAllAsync();
        Assert.Empty(backend.Assemblies);

        var reopened = await backend.OpenAsync(path);
        Assert.Contains(backend.Assemblies, assembly => assembly.SessionId == reopened.SessionId);
    }

    [Fact]
    public async Task Auto_load_promotes_siblings_again_after_they_are_individually_unloaded()
    {
        // Reproduces the "unload (via selection/Delete) then reopen and the siblings don't come back" bug:
        // per-assembly CloseAsync must forget the closed path so it can be promoted again.
        var folder = Directory.CreateTempSubdirectory("dnspyxdx-reopen");
        try
        {
            var targetCopy = Path.Combine(folder.FullName, Path.GetFileName(typeof(NeighborLoadingTests).Assembly.Location));
            var neighborCopy = Path.Combine(folder.FullName, Path.GetFileName(typeof(DecompilerBackend).Assembly.Location));
            File.Copy(typeof(NeighborLoadingTests).Assembly.Location, targetCopy);
            File.Copy(typeof(DecompilerBackend).Assembly.Location, neighborCopy);

            await using var backend = new DecompilerBackend(
                new RuntimeDisplaySettings(), null, new NeighborLoadingSettings { AutoLoadReferencedAssemblies = true });

            await backend.OpenAsync(targetCopy);
            Assert.True(await WaitForAssemblyAsync(backend, LocalNeighbor), "Sibling should be promoted the first time.");

            // Unload each assembly individually, the way Ctrl+A + Delete / "Unload selected" does.
            foreach (var assembly in backend.Assemblies.ToArray()) await backend.CloseAsync(assembly.SessionId);
            Assert.Empty(backend.Assemblies);

            await backend.OpenAsync(targetCopy);
            Assert.True(await WaitForAssemblyAsync(backend, LocalNeighbor), "Sibling should be promoted again after reopening.");
        }
        finally { await TryDeleteAsync(folder); }
    }

    [Fact]
    public async Task Auto_load_promotes_app_local_siblings_but_not_framework_garbage()
    {
        // Isolate the target and one sibling it references in their own folder, so the only app-local
        // assembly that can be promoted is the sibling. Framework references (System.*) resolve from the
        // shared runtime, not this folder, so they must stay out — that is what keeps the tree clean.
        var folder = Directory.CreateTempSubdirectory("dnspyxdx-neighbors");
        try
        {
            var targetCopy = Path.Combine(folder.FullName, Path.GetFileName(typeof(NeighborLoadingTests).Assembly.Location));
            var neighborCopy = Path.Combine(folder.FullName, Path.GetFileName(typeof(DecompilerBackend).Assembly.Location));
            File.Copy(typeof(NeighborLoadingTests).Assembly.Location, targetCopy);
            File.Copy(typeof(DecompilerBackend).Assembly.Location, neighborCopy);

            var changed = 0;
            await using var backend = new DecompilerBackend(
                new RuntimeDisplaySettings(), null, new NeighborLoadingSettings { AutoLoadReferencedAssemblies = true });
            backend.AssembliesChanged += () => Interlocked.Increment(ref changed);

            await backend.OpenAsync(targetCopy);
            var promoted = await WaitForAssemblyAsync(backend, LocalNeighbor);

            Assert.True(promoted, $"Expected the app-local sibling '{LocalNeighbor}' to be opened on demand.");
            // Only the target and its one app-local sibling; no framework assemblies dragged in from the runtime.
            Assert.Equal(2, backend.Assemblies.Count);
            Assert.DoesNotContain(backend.Assemblies, assembly => assembly.Name.StartsWith("System.", StringComparison.Ordinal));
            Assert.True(Volatile.Read(ref changed) >= 2, "Opening a sibling should raise AssembliesChanged.");
        }
        finally { await TryDeleteAsync(folder); }
    }

    // Background warm-up/promotion can still hold a module handle for a moment after the backend disposes, so
    // deleting the staging folder is best-effort: retry briefly, then give up rather than fail the test.
    private static async Task TryDeleteAsync(DirectoryInfo folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { folder.Delete(recursive: true); return; }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    [Fact]
    public async Task Auto_load_disabled_by_default_opens_only_the_requested_assembly()
    {
        await using var backend = new DecompilerBackend();

        await backend.OpenAsync(typeof(NeighborLoadingTests).Assembly.Location);
        // Give any (non-existent) background promotion a chance to run before asserting the negative.
        await Task.Delay(500);

        Assert.DoesNotContain(backend.Assemblies, assembly => assembly.Name == LocalNeighbor);
        Assert.Single(backend.Assemblies);
    }

    [Fact]
    public async Task Open_reference_resolves_a_dependency_that_is_not_beside_the_file()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(NeighborLoadingTests).Assembly.Location);
        var references = await backend.GetChildrenAsync(new NodeId(assembly.SessionId, "references"));

        // A framework reference (System.*) is resolved from the shared runtime / reference packs, not from the
        // directory beside the test assembly. The old beside-only lookup failed on these; the resolver-backed
        // path should now open it.
        var frameworkReference = references.First(node => node.Name.StartsWith("System.", StringComparison.Ordinal));
        var opened = await backend.OpenReferenceAsync(frameworkReference.Id);

        Assert.Equal(frameworkReference.Name, opened.Name);
        Assert.Contains(backend.Assemblies, candidate => candidate.SessionId == opened.SessionId);
    }

    private static async Task<bool> WaitForAssemblyAsync(DecompilerBackend backend, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (backend.Assemblies.Any(assembly => assembly.Name == name)) return true;
            try { await Task.Delay(50, timeout.Token); } catch (OperationCanceledException) { break; }
        }
        return backend.Assemblies.Any(assembly => assembly.Name == name);
    }
}
