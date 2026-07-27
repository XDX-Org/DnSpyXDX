using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

public sealed class WorkspaceAssemblyService(
    IDecompilerBackend backend,
    WorkspaceState workspace,
    SourceViewStateStore viewStates,
    SourcePresentationCache presentationCache)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public event Action<AssemblyDescriptor>? Closing;

    public async Task<AssemblyDescriptor> OpenAsync(string path, string source, CancellationToken cancellationToken = default)
    {
        var assembly = await backend.OpenAsync(path, cancellationToken);
        workspace.SetBusy(false, $"Opened {assembly.Name} through {source}");
        return assembly;
    }

    public async Task<AssemblyDescriptor> EnsureOpenAsync(
        string path,
        string source,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var existing = backend.Assemblies.FirstOrDefault(
                value => string.Equals(
                    Path.GetFullPath(value.Path),
                    fullPath,
                    comparison));
            return existing ??
                await OpenAsync(fullPath, source, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AssemblyDescriptor?> TryOpenDebuggerModuleAsync(
        string path,
        Guid? expectedMvid = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
        var candidates = new List<string> { fullPath };
        if (string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.ChangeExtension(fullPath, ".dll"));
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        foreach (var candidate in candidates.Distinct(comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate)) continue;
            try
            {
                var assembly = await EnsureOpenAsync(
                    candidate,
                    "debugger",
                    cancellationToken);
                if (expectedMvid is null ||
                    assembly.ModuleMvid == expectedMvid)
                    return assembly;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Apphosts and remote module paths may not be managed files
                // accessible to the decompiler. Debugging must still continue.
            }
        }
        return null;
    }

    public async Task<bool> CloseAsync(Guid moduleMvid, string source, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var assembly = backend.Assemblies.FirstOrDefault(candidate => candidate.ModuleMvid == moduleMvid);
            if (assembly is null) return false;
            Closing?.Invoke(assembly);
            await backend.CloseAsync(assembly.SessionId);
            workspace.CloseAssembly(moduleMvid);
            viewStates.RemoveAssembly(moduleMvid);
            presentationCache.RemoveAssembly(moduleMvid);
            workspace.SetBusy(false, $"Unloaded {assembly.Name} through {source}");
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task CloseAllAsync(string source, CancellationToken cancellationToken = default)
    {
        foreach (var assembly in backend.Assemblies.ToArray())
            await CloseAsync(assembly.ModuleMvid, source, cancellationToken);
        workspace.Clear();
        viewStates.Clear();
        presentationCache.Clear();
        workspace.SetBusy(false, "All assemblies unloaded");
    }
}
