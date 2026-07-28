namespace DnSpyXDX.Application;

public sealed class WorkspaceState
{
    private readonly List<DocumentTab> tabs = [];
    public event Action? Changed;
    // Raised by the native drag-drop handler while a file is dragged over the window, so the UI can show the
    // drop overlay. Separate from Changed so a hover doesn't trigger a re-render or a session save.
    public event Action<bool>? DragActiveChanged;
    public void SetDragActive(bool active) => DragActiveChanged?.Invoke(active);
    public IReadOnlyList<DocumentTab> Tabs => tabs;
    public string? ActiveTabId { get; private set; }
    public DocumentTab? ActiveTab => tabs.FirstOrDefault(t => t.Id == ActiveTabId);
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "Ready";

    public void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>
    /// Shows a document, following dnSpy: a plain navigation replaces the active tab's content and
    /// pushes the previous document onto that tab's history, while <paramref name="newTab"/> opens
    /// a separate tab instead.
    /// </summary>
    public void Open(DecompilerDocument document, string assemblyName, bool newTab = false)
    {
        var active = ActiveTab;
        if (newTab || active is null)
        {
            var tab = new DocumentTab(Guid.NewGuid().ToString("N"), document, assemblyName);
            tabs.Add(tab);
            ActiveTabId = tab.Id;
        }
        else active.NavigateTo(document, assemblyName);
        Status = $"Decompiled {document.Title}";
        Changed?.Invoke();
    }

    public string OpenLoading(SymbolId symbol, string title, string assemblyName, DecompilerLanguage language, bool newTab = false)
    {
        var placeholder = new DecompilerDocument(symbol, title, language.Key(), "", [], []);
        var tab = ActiveTab;
        if (newTab || tab is null)
        {
            tab = new DocumentTab(Guid.NewGuid().ToString("N"), placeholder, assemblyName, isLoading: true);
            tabs.Add(tab);
            ActiveTabId = tab.Id;
        }
        else tab.NavigateToLoading(placeholder, assemblyName);
        Status = $"Decompiling {title}…";
        Changed?.Invoke();
        return tab.Id;
    }

    public bool RefreshLoading(string id, DecompilerLanguage language)
    {
        var tab = tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return false;
        tab.RefreshLoading(language);
        Status = $"Decompiling {tab.Title}…";
        Changed?.Invoke();
        return true;
    }

    public bool CompleteLoading(string id, DecompilerDocument document)
    {
        var tab = tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !tab.IsLoading) return false;
        tab.CompleteLoading(document);
        Status = $"Decompiled {document.Title}";
        Changed?.Invoke();
        return true;
    }

    public bool FailLoading(string id, string message)
    {
        var tab = tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !tab.IsLoading) return false;
        tab.FailLoading(message);
        Status = message;
        Changed?.Invoke();
        return true;
    }

    public bool GoBack() => Navigate(tab => tab.GoBack());
    public bool GoForward() => Navigate(tab => tab.GoForward());

    public bool FocusActive(SymbolId symbol)
    {
        if (ActiveTab is not { } tab || !tab.Focus(symbol)) return false;
        Status = $"Navigated to token 0x{symbol.MetadataToken:X8}";
        Changed?.Invoke();
        return true;
    }

    private bool Navigate(Func<DocumentTab, bool> move)
    {
        if (ActiveTab is not { } tab || !move(tab)) return false;
        Status = $"Decompiled {tab.Document.Title}";
        Changed?.Invoke();
        return true;
    }

    public void Activate(string id) { ActiveTabId = id; Changed?.Invoke(); }

    public void Close(string id)
    {
        var index = tabs.FindIndex(t => t.Id == id);
        if (index < 0) return;
        tabs.RemoveAt(index);
        if (ActiveTabId == id) ActiveTabId = tabs.ElementAtOrDefault(index)?.Id ?? tabs.LastOrDefault()?.Id;
        Changed?.Invoke();
    }

    /// <summary>Closes documents owned by an unloaded module and removes that module from the
    /// navigation history of documents that remain open.</summary>
    public void CloseAssembly(Guid moduleMvid)
    {
        var activeIndex = tabs.FindIndex(tab => tab.Id == ActiveTabId);
        for (var index = tabs.Count - 1; index >= 0; index--)
        {
            if (tabs[index].RemoveAssembly(moduleMvid)) tabs.RemoveAt(index);
        }
        if (ActiveTabId is not null && tabs.All(tab => tab.Id != ActiveTabId))
            ActiveTabId = tabs.ElementAtOrDefault(Math.Min(Math.Max(activeIndex, 0), tabs.Count - 1))?.Id ?? tabs.LastOrDefault()?.Id;
        Changed?.Invoke();
    }

    public void Clear()
    {
        tabs.Clear();
        ActiveTabId = null;
        Status = "Ready";
        Changed?.Invoke();
    }
}

/// <summary>A tab is a view whose content changes as you navigate, so it keeps its own back/forward
/// history rather than being identified by the symbol it happens to be showing.</summary>
public sealed class DocumentTab(string id, DecompilerDocument document, string assemblyName, bool isLoading = false)
{
    private readonly List<(DecompilerDocument Document, string AssemblyName)> back = [];
    private readonly List<(DecompilerDocument Document, string AssemblyName)> forward = [];

    public string Id { get; } = id;
    public DecompilerDocument Document { get; private set; } = document;
    public string AssemblyName { get; private set; } = assemblyName;
    public string Title => Document.Title;
    public bool IsLoading { get; private set; } = isLoading;
    public string? Error { get; private set; }
    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;

    internal void NavigateTo(DecompilerDocument next, string nextAssemblyName)
    {
        if (next.Symbol == Document.Symbol && !IsLoading && Error is null) return;
        if (!IsLoading && Error is null) back.Add((Document, AssemblyName));
        forward.Clear();
        (Document, AssemblyName) = (next, nextAssemblyName);
        IsLoading = false;
        Error = null;
    }

    internal void NavigateToLoading(DecompilerDocument next, string nextAssemblyName)
    {
        if (!IsLoading && Error is null && next.Symbol != Document.Symbol) back.Add((Document, AssemblyName));
        forward.Clear();
        (Document, AssemblyName) = (next, nextAssemblyName);
        IsLoading = true;
        Error = null;
    }

    internal void CompleteLoading(DecompilerDocument document)
    {
        Document = document;
        IsLoading = false;
        Error = null;
    }

    internal void RefreshLoading(DecompilerLanguage language)
    {
        Document = Document with { Language = language.Key(), Text = "" };
        IsLoading = true;
        Error = null;
    }

    internal void FailLoading(string message)
    {
        IsLoading = false;
        Error = message;
    }

    internal bool GoBack() => Step(back, forward);
    internal bool GoForward() => Step(forward, back);

    internal bool Focus(SymbolId symbol)
    {
        if (Document.FocusSymbol == symbol || IsLoading || Error is not null) return false;
        back.Add((Document, AssemblyName));
        forward.Clear();
        Document = Document with { FocusSymbol = symbol };
        return true;
    }

    /// <returns><see langword="true"/> when the current document belongs to the module and the
    /// whole tab should be closed.</returns>
    internal bool RemoveAssembly(Guid moduleMvid)
    {
        back.RemoveAll(entry => entry.Document.Symbol.ModuleMvid == moduleMvid);
        forward.RemoveAll(entry => entry.Document.Symbol.ModuleMvid == moduleMvid);
        return Document.Symbol.ModuleMvid == moduleMvid;
    }

    private bool Step(List<(DecompilerDocument Document, string AssemblyName)> from, List<(DecompilerDocument Document, string AssemblyName)> to)
    {
        if (from.Count == 0) return false;
        to.Add((Document, AssemblyName));
        (Document, AssemblyName) = from[^1];
        from.RemoveAt(from.Count - 1);
        return true;
    }
}
