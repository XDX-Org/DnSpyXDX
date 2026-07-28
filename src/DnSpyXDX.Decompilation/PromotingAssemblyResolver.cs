using ICSharpCode.Decompiler.Metadata;

namespace DnSpyXDX.Decompilation;

/// <summary>
/// Wraps the real <see cref="IAssemblyResolver"/> and reports the on-disk path of every assembly it
/// resolves. dnSpy resolves references on demand (a session's type system pulls them in as it builds) and
/// surfaces the resolved documents in the Assembly Explorer; this hook lets the backend do the same by
/// promoting app-local neighbors to their own sessions. Resolution itself is entirely delegated, so the
/// decompiler behaves exactly as it would without the wrapper.
/// </summary>
internal sealed class PromotingAssemblyResolver(IAssemblyResolver inner, Action<string> onResolved) : IAssemblyResolver
{
    public MetadataFile? Resolve(IAssemblyReference reference) => Report(inner.Resolve(reference));

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => Report(inner.ResolveModule(mainModule, moduleName));

    public async Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Report(await inner.ResolveAsync(reference));

    public async Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Report(await inner.ResolveModuleAsync(mainModule, moduleName));

    private MetadataFile? Report(MetadataFile? file)
    {
        if (file?.FileName is { Length: > 0 } path) onResolved(path);
        return file;
    }
}
