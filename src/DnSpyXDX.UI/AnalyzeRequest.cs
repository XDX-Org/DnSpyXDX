using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

/// <summary>A request to analyze the symbol under the cursor, raised by right-clicking a resolved
/// reference in the source view. Carries the click position so the host can place its context menu.</summary>
public readonly record struct AnalyzeRequest(SymbolId Symbol, string Name, TreeNodeKind Kind, double ClientX, double ClientY);
