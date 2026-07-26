using System.Text.Json;
using DnSpyXDX.Application;

namespace DnSpyXDX.Host;

public sealed class WorkspaceSessionService(IDecompilerBackend backend, WorkspaceState workspace) : IWorkspaceSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> documentRestores = [];
    private readonly HashSet<string> cancelledDocuments = [];
    private readonly HashSet<string> pendingDocumentTabs = [];
    private readonly object restoreGate = new();
    private SessionSnapshot? pendingRestore;
    public UiSessionState UiState { get; set; } = new();
    private string SessionPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnSpyXDX", "session.json");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SessionPath)) return;
        try
        {
            await using var stream = File.OpenRead(SessionPath);
            pendingRestore = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return; }
        UiState = pendingRestore?.UiState ?? new UiSessionState();
    }

    public async Task RestoreAssembliesAsync(CancellationToken cancellationToken = default)
    {
        if (pendingRestore is not { } snapshot) return;
        foreach (var path in snapshot.AssemblyPaths.Distinct(PathComparer()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            try { await backend.OpenAsync(path, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    public async Task RestoreDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (pendingRestore is not { } snapshot) return;
        var documents = new List<(SavedDocument Saved, string TabId)>();
        foreach (var saved in snapshot.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assembly = backend.Assemblies.FirstOrDefault(a => a.ModuleMvid == saved.Symbol.ModuleMvid);
            if (assembly is null) continue;
            var title = string.IsNullOrWhiteSpace(saved.Title) ? $"0x{saved.Symbol.MetadataToken:X8}" : saved.Title;
            var language = saved.Language.ValidOrDefault();
            var tabId = workspace.OpenLoading(saved.Symbol, title, saved.AssemblyName ?? assembly.Name, language, newTab: true);
            documents.Add((saved, tabId));
            lock (restoreGate) pendingDocumentTabs.Add(tabId);
        }
        if (workspace.Tabs.ElementAtOrDefault(snapshot.ActiveIndex) is { } active) workspace.Activate(active.Id);

        foreach (var (saved, tabId) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource restore;
            lock (restoreGate)
            {
                if (cancelledDocuments.Remove(tabId) || workspace.Tabs.All(tab => tab.Id != tabId))
                {
                    pendingDocumentTabs.Remove(tabId);
                    continue;
                }
                restore = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                documentRestores[tabId] = restore;
            }
            try
            {
                var document = await backend.DecompileAsync(saved.Symbol, saved.Language.ValidOrDefault(), restore.Token);
                workspace.CompleteLoading(tabId, document);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException) { workspace.FailLoading(tabId, ex.Message); }
            finally
            {
                lock (restoreGate)
                {
                    documentRestores.Remove(tabId);
                    pendingDocumentTabs.Remove(tabId);
                }
                restore.Dispose();
            }
        }
        pendingRestore = null;
    }

    public void CancelDocumentRestore(string tabId)
    {
        lock (restoreGate)
        {
            if (documentRestores.TryGetValue(tabId, out var restore)) restore.Cancel();
            else if (pendingDocumentTabs.Contains(tabId)) cancelledDocuments.Add(tabId);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = new SessionSnapshot(
                backend.Assemblies.Select(a => a.Path).ToArray(),
                workspace.Tabs.Where(t => !t.IsLoading && t.Error is null).Select(t => new SavedDocument(t.Document.Symbol, t.Title, t.AssemblyName, ParseLanguage(t.Document.Language))).ToArray(),
                workspace.Tabs.ToList().FindIndex(t => t.Id == workspace.ActiveTabId),
                UiState);
            var directory = Path.GetDirectoryName(SessionPath)!;
            Directory.CreateDirectory(directory);
            var temporary = SessionPath + ".tmp";
            await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            File.Move(temporary, SessionPath, true);
        }
        finally { gate.Release(); }
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static DecompilerLanguage ParseLanguage(string value) => value switch
    {
        "il" => DecompilerLanguage.IL,
        "il-csharp" => DecompilerLanguage.ILWithCSharp,
        "hex" => DecompilerLanguage.Hex,
        _ => DecompilerLanguage.CSharp
    };
    private sealed record SessionSnapshot(string[] AssemblyPaths, SavedDocument[] Documents, int ActiveIndex, UiSessionState? UiState = null);
    private sealed record SavedDocument(SymbolId Symbol, string? Title = null, string? AssemblyName = null, DecompilerLanguage Language = DecompilerLanguage.CSharp);
}
