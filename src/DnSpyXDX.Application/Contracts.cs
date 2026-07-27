namespace DnSpyXDX.Application;

public interface IDecompilerBackend : IAsyncDisposable
{
    IReadOnlyList<AssemblyDescriptor> Assemblies { get; }
    Task<AssemblyDescriptor> OpenAsync(string path, CancellationToken cancellationToken = default);
    Task<AssemblyDescriptor> OpenReferenceAsync(NodeId reference, CancellationToken cancellationToken = default);
    Task<AssemblyDescriptor> OpenAssemblyForSymbolAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task CloseAsync(Guid sessionId);
    Task<IReadOnlyList<TreeNodeDescriptor>> GetChildrenAsync(NodeId parent, CancellationToken cancellationToken = default);
    Task<ResourceDocument> GetResourceAsync(NodeId resource, CancellationToken cancellationToken = default);
    Task<DecompilerDocument> DecompileAsync(SymbolId symbol, DecompilerLanguage language, CancellationToken cancellationToken = default);
    Task<SymbolId> GetDeclaringTypeAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeId>> GetPathAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, IProgress<IReadOnlyList<SearchResult>>? progress = null);
    bool TryGetAssembly(Guid sessionId, out AssemblyDescriptor? assembly);
}

public interface IProjectExportService
{
    Task<ExportReport> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IFileDialogService
{
    Task<string?> OpenAssemblyAsync();
    Task<string?> SelectExportFolderAsync();
}

public interface IWorkspaceSessionService
{
    UiSessionState UiState { get; set; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task RestoreAssembliesAsync(CancellationToken cancellationToken = default);
    Task RestoreDocumentsAsync(CancellationToken cancellationToken = default);
    void CancelDocumentRestore(string tabId);
    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Controls the native host webview scale for the whole application.</summary>
public interface IApplicationZoomService
{
    int ZoomPercent { get; }
    void SetZoom(int percent);
}

public interface IApplicationLifetime
{
    void Exit();
}

public sealed class RuntimeLoggingSettings
{
    private int debugEnabled;
    public bool DebugEnabled
    {
        get => Volatile.Read(ref debugEnabled) != 0;
        set => Volatile.Write(ref debugEnabled, value ? 1 : 0);
    }
}

public sealed class RuntimeDisplaySettings
{
    private int showMetadataTokens;
    public bool ShowMetadataTokens
    {
        get => Volatile.Read(ref showMetadataTokens) != 0;
        set => Volatile.Write(ref showMetadataTokens, value ? 1 : 0);
    }
}
