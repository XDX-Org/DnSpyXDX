namespace DnSpyXDX.Application;

public interface IDecompilerBackend : IAsyncDisposable
{
    IReadOnlyList<AssemblyDescriptor> Assemblies { get; }
    /// <summary>Raised whenever the set of open assemblies changes, including neighbors opened in the
    /// background by on-demand reference loading. Handlers must marshal to the UI thread themselves.</summary>
    event Action? AssembliesChanged;
    Task<AssemblyDescriptor> OpenAsync(string path, CancellationToken cancellationToken = default);
    Task<AssemblyDescriptor> OpenReferenceAsync(NodeId reference, CancellationToken cancellationToken = default);
    Task<AssemblyDescriptor> OpenAssemblyForSymbolAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task CloseAsync(Guid sessionId);
    /// <summary>Unloads every open assembly at once. Cheaper and non-freezing versus closing one at a time
    /// when many are open (a whole folder): clears the set immediately and releases modules off-thread.</summary>
    Task CloseAllAsync();
    Task<IReadOnlyList<TreeNodeDescriptor>> GetChildrenAsync(NodeId parent, CancellationToken cancellationToken = default);
    Task<ResourceDocument> GetResourceAsync(NodeId resource, CancellationToken cancellationToken = default);
    Task<DecompilerDocument> DecompileAsync(SymbolId symbol, DecompilerLanguage language, CancellationToken cancellationToken = default);
    Task<SymbolId> GetDeclaringTypeAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NodeId>> GetPathAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, IProgress<IReadOnlyList<SearchResult>>? progress = null);
    /// <summary>The analysis relations (Used By, Uses, …) that apply to the given symbol's kind.</summary>
    Task<IReadOnlyList<AnalyzerRelation>> GetAnalyzerRelationsAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    /// <summary>The analyzer descriptor (name, kind, qualified name) for a single symbol, used to label an
    /// analyzer root with the same canonical name its results use. Null if the symbol can't be resolved.</summary>
    Task<AnalyzerResult?> DescribeSymbolAsync(SymbolId symbol, CancellationToken cancellationToken = default);
    /// <summary>Resolves one analyzer relation for a symbol, scanning IL across the visibility-scoped
    /// set of open assemblies. Results stream through <paramref name="progress"/> like search.</summary>
    Task<IReadOnlyList<AnalyzerResult>> AnalyzeAsync(SymbolId symbol, AnalyzerRelation relation, CancellationToken cancellationToken = default, IProgress<IReadOnlyList<AnalyzerResult>>? progress = null);
    bool TryGetAssembly(Guid sessionId, out AssemblyDescriptor? assembly);
}

public interface IProjectExportService
{
    Task<ExportReport> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IFileDialogService
{
    Task<string?> OpenAssemblyAsync(string? initialDirectory = null);
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

/// <summary>Controls dnSpy-style on-demand loading of referenced assemblies. When enabled, any assembly
/// the decompiler resolves out of a directory the workspace has already opened is promoted to its own
/// session in the background, so cross-assembly analysis and navigation see the whole app. Framework and
/// GAC assemblies (resolved from the shared runtime or NuGet packs) are deliberately left out.</summary>
public sealed class NeighborLoadingSettings
{
    public bool AutoLoadReferencedAssemblies { get; init; }
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
