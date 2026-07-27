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
