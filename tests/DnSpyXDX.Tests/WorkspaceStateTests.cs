using DnSpyXDX.Application;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class WorkspaceStateTests
{
    private static DecompilerDocument Document(int token) =>
        new(new SymbolId(Guid.Empty, token), $"Type{token}", "csharp", "// body", [], []);
    private static DecompilerDocument Document(Guid moduleMvid, int token) =>
        new(new SymbolId(moduleMvid, token), $"Type{token}", "csharp", "// body", [], []);

    [Fact]
    public void Refreshing_language_keeps_tab_and_history_in_place()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample");
        var tab = workspace.ActiveTab!;

        Assert.True(workspace.RefreshLoading(tab.Id, DecompilerLanguage.IL));

        Assert.Equal(tab.Id, workspace.ActiveTabId);
        Assert.Equal("il", tab.Document.Language);
        Assert.True(tab.IsLoading);
        Assert.True(tab.CanGoBack);
        Assert.False(tab.CanGoForward);
    }

    [Fact]
    public void Plain_navigation_reuses_the_active_tab()
    {
        var workspace = new WorkspaceState();

        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample");

        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal("Type2", tab.Title);
    }

    [Fact]
    public void Focusing_a_member_updates_the_document_without_loading_and_supports_back()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        var member = new SymbolId(Guid.Empty, 0x06000002);

        Assert.True(workspace.FocusActive(member));
        Assert.Equal(member, workspace.ActiveTab!.Document.FocusSymbol);
        Assert.False(workspace.ActiveTab.IsLoading);
        Assert.True(workspace.GoBack());
        Assert.Null(workspace.ActiveTab.Document.FocusSymbol);
    }

    [Fact]
    public void New_tab_navigation_appends_and_activates()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");

        workspace.Open(Document(2), "Sample", newTab: true);

        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal("Type2", workspace.ActiveTab!.Title);
        Assert.Equal(workspace.Tabs[1].Id, workspace.ActiveTabId);
    }

    [Fact]
    public void Back_and_forward_walk_the_active_tabs_history()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample");
        workspace.Open(Document(3), "Sample");

        Assert.True(workspace.GoBack());
        Assert.Equal("Type2", workspace.ActiveTab!.Title);
        Assert.True(workspace.GoBack());
        Assert.Equal("Type1", workspace.ActiveTab!.Title);
        Assert.False(workspace.GoBack());

        Assert.True(workspace.GoForward());
        Assert.Equal("Type2", workspace.ActiveTab!.Title);
        Assert.True(workspace.GoForward());
        Assert.Equal("Type3", workspace.ActiveTab!.Title);
        Assert.False(workspace.GoForward());
    }

    [Fact]
    public void Navigating_after_going_back_discards_the_forward_history()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample");
        workspace.GoBack();

        workspace.Open(Document(3), "Sample");

        Assert.False(workspace.ActiveTab!.CanGoForward);
        Assert.True(workspace.ActiveTab.CanGoBack);
    }

    [Fact]
    public void History_is_tracked_per_tab()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample");
        workspace.Open(Document(3), "Sample", newTab: true);

        // The new tab starts with no history of its own.
        Assert.False(workspace.ActiveTab!.CanGoBack);
        Assert.False(workspace.GoBack());

        workspace.Activate(workspace.Tabs[0].Id);
        Assert.True(workspace.GoBack());
        Assert.Equal("Type1", workspace.ActiveTab!.Title);
    }

    [Fact]
    public void Re_navigating_to_the_same_symbol_does_not_grow_history()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");

        workspace.Open(Document(1), "Sample");

        Assert.False(workspace.ActiveTab!.CanGoBack);
    }

    [Fact]
    public void Closing_the_active_tab_activates_a_neighbour()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Open(Document(2), "Sample", newTab: true);
        workspace.Activate(workspace.Tabs[0].Id);

        workspace.Close(workspace.Tabs[0].Id);

        Assert.Equal("Type2", workspace.ActiveTab!.Title);
    }

    [Fact]
    public void Plain_navigation_opens_a_tab_after_the_last_tab_is_closed()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        workspace.Close(workspace.Tabs[0].Id);

        workspace.Open(Document(2), "Sample");

        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal("Type2", tab.Title);
        Assert.Equal(tab.Id, workspace.ActiveTabId);
    }

    [Fact]
    public void Loading_navigation_reuses_the_active_tab_unless_a_new_tab_is_requested()
    {
        var workspace = new WorkspaceState();
        workspace.Open(Document(1), "Sample");
        var originalTab = workspace.ActiveTabId;

        var reusedTab = workspace.OpenLoading(Document(2).Symbol, "Type2", "Sample", DecompilerLanguage.CSharp);

        Assert.Equal(originalTab, reusedTab);
        Assert.Single(workspace.Tabs);
        Assert.True(workspace.ActiveTab!.IsLoading);
        Assert.True(workspace.ActiveTab.CanGoBack);

        var newTab = workspace.OpenLoading(Document(3).Symbol, "Type3", "Sample", DecompilerLanguage.CSharp, newTab: true);

        Assert.NotEqual(reusedTab, newTab);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal(newTab, workspace.ActiveTabId);
    }

    [Fact]
    public void Closing_an_assembly_removes_its_tabs_and_history_only()
    {
        var workspace = new WorkspaceState();
        var firstAssembly = Guid.NewGuid();
        var secondAssembly = Guid.NewGuid();
        workspace.Open(Document(firstAssembly, 1), "SameName");
        workspace.Open(Document(secondAssembly, 2), "SameName");
        workspace.Open(Document(firstAssembly, 3), "SameName", newTab: true);

        workspace.CloseAssembly(firstAssembly);

        var remaining = Assert.Single(workspace.Tabs);
        Assert.Equal(secondAssembly, remaining.Document.Symbol.ModuleMvid);
        Assert.Equal(remaining.Id, workspace.ActiveTabId);
        Assert.False(remaining.CanGoBack);
        Assert.False(remaining.CanGoForward);
    }
}
