namespace DnSpyXDX.Application;

public enum DecompilerLanguage
{
    CSharp = 0,
    IL = 1,
    ILWithCSharp = 2,
    Hex = 3
}

public static class DecompilerLanguages
{
    public static string Key(this DecompilerLanguage language) => language switch
    {
        DecompilerLanguage.CSharp => "csharp",
        DecompilerLanguage.IL => "il",
        DecompilerLanguage.ILWithCSharp => "il-csharp",
        DecompilerLanguage.Hex => "hex",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    public static DecompilerLanguage ValidOrDefault(this DecompilerLanguage language) =>
        Enum.IsDefined(language) ? language : DecompilerLanguage.CSharp;
}

public readonly record struct SymbolId(Guid ModuleMvid, int MetadataToken);
public readonly record struct NodeId(Guid SessionId, string Value);

public enum TreeNodeKind
{
    Assembly, Group, Reference, Resource, Namespace, Type, Field, Property, Event, Constructor, Method
}

public sealed record AssemblyDescriptor(
    Guid SessionId,
    Guid ModuleMvid,
    string Name,
    string Path,
    string TargetFramework,
    string Architecture,
    NodeId RootNode);

public sealed record TreeNodeDescriptor(
    NodeId Id,
    string Name,
    TreeNodeKind Kind,
    bool HasChildren,
    SymbolId? Symbol = null,
    string? Detail = null,
    string? Visibility = null,
    string? TypeDisplay = null,
    string? NameClassification = null,
    string? TypeClassification = null,
    string? Tooltip = null);

public sealed record ReferenceSpan(
    int StartOffset,
    int Length,
    SymbolId? LocalTarget,
    string? ExternalAssembly,
    string Tooltip,
    int? DocumentOffset = null);

public sealed record DiagnosticMessage(string Severity, string Message);
public sealed record BinaryRegion(int Offset, int Length, string Tooltip, bool IsEntity = false);
public sealed record ResourceDocument(NodeId Id, SymbolId Symbol, string Name, byte[] Data, string Kind, string? Text = null, string? MimeType = null);

public sealed record DecompilerDocument(
    SymbolId Symbol,
    string Title,
    string Language,
    string Text,
    IReadOnlyList<ReferenceSpan> References,
    IReadOnlyList<DiagnosticMessage> Diagnostics,
    IReadOnlyDictionary<string, SymbolId?>? SymbolLinks = null,
    SymbolId? FocusSymbol = null,
    IReadOnlyDictionary<string, string>? TypeClassifications = null,
    byte[]? Binary = null,
    int? BinarySelectionOffset = null,
    int BinarySelectionLength = 0,
    IReadOnlyList<BinaryRegion>? BinaryRegions = null,
    ResourceDocument? Resource = null,
    IReadOnlyDictionary<int, int>? SymbolLocations = null);

public sealed record SearchResult(SymbolId Symbol, string Name, string Kind, string AssemblyName, string Namespace, SymbolId DeclaringType, string? QualifiedName = null);

/// <summary>A request to show a symbol; <paramref name="NewTab"/> mirrors dnSpy's Ctrl+click.</summary>
public readonly record struct NavigationRequest(SymbolId Symbol, bool NewTab, TreeNodeKind? Kind = null, string? DisplayName = null);

public sealed record ExportRequest(IReadOnlyList<Guid> SessionIds, string Destination, bool ValidateBuild = false);
public sealed record ExportProgress(int Completed, int Total, string Message);
public sealed record ExportReport(bool Success, string Destination, IReadOnlyList<string> Projects, IReadOnlyList<string> Warnings, string? BuildOutput = null);

public sealed record UiSessionState(
    double ExplorerWidth = 300,
    double SearchPanelHeight = 230,
    bool SearchOpen = false,
    string SearchKind = "All",
    string SearchScope = "All",
    int ZoomPercent = 100,
    string ThemeId = "default",
    bool DebugLogging = false,
    DecompilerLanguage Language = DecompilerLanguage.CSharp,
    bool ShowMetadataTokens = true,
    bool WrapLines = false,
    string CodeFontFamily = "");
