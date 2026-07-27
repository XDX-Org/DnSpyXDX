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

/// <summary>A run of decompiled text with a semantic classification resolved from the decompiler's
/// syntax tree (e.g. "class", "interface", "field", "property", "method"), so the viewer can color each
/// token by its bound symbol the way dnSpy does rather than by lexical guesswork.</summary>
public readonly record struct ClassifiedSpan(int Start, int Length, string Kind);
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
    IReadOnlyDictionary<int, int>? SymbolLocations = null,
    IReadOnlyList<ClassifiedSpan>? SemanticSpans = null,
    DebugDocumentMap? DebugMap = null);

public sealed record SearchResult(SymbolId Symbol, string Name, string Kind, string AssemblyName, string Namespace, SymbolId DeclaringType, string? QualifiedName = null);

/// <summary>A dnSpy-style analysis relation. <see cref="UsedBy"/> and <see cref="Uses"/> are the
/// callers/callees pair behind the Analyzer's Used By / Uses nodes; the rest mirror dnSpy's other
/// relation nodes and are added in later phases.</summary>
public enum AnalyzerRelation
{
    UsedBy,
    Uses,
    DerivedTypes,
    Overrides,
    OverriddenBy,
    ImplementedBy,
    InstantiatedBy,
    ExposedBy,
    EventFiredBy
}

/// <summary>One row under an analyzer relation: a symbol that participates in the relation (a caller,
/// a callee, a derived type, …). <paramref name="ILOffset"/> is the byte offset of the referencing
/// instruction when the result came from an IL scan, so navigation can land on the exact use.</summary>
public sealed record AnalyzerResult(
    SymbolId Symbol,
    string Name,
    TreeNodeKind Kind,
    string AssemblyName,
    string Namespace,
    SymbolId DeclaringType,
    string? QualifiedName = null,
    int? ILOffset = null);

/// <summary>A request to show a symbol; <paramref name="NewTab"/> mirrors dnSpy's Ctrl+click.</summary>
public readonly record struct NavigationRequest(SymbolId Symbol, bool NewTab, TreeNodeKind? Kind = null, string? DisplayName = null);

public sealed record ExportRequest(IReadOnlyList<Guid> SessionIds, string Destination, bool ValidateBuild = false);
public sealed record ExportProgress(int Completed, int Total, string Message);
public sealed record ExportReport(bool Success, string Destination, IReadOnlyList<string> Projects, IReadOnlyList<string> Warnings, string? BuildOutput = null);

/// <summary>A persisted analyzer root: the symbol the user chose to analyze, kept by module MVID and
/// token so it can be rehydrated after the assembly is reopened in a later session.</summary>
public sealed record AnalyzerRootState(Guid ModuleMvid, int MetadataToken, string Name, TreeNodeKind Kind);

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
    bool ShowMetadataTokens = false,
    bool WrapLines = false,
    string CodeFontFamily = "",
    string BottomTab = "search",
    IReadOnlyList<AnalyzerRootState>? AnalyzerRoots = null,
    bool McpEnabled = false,
    IReadOnlyList<string>? McpAllowedRoots = null);
