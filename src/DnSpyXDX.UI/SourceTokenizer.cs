using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

public enum SourceTokenKind
{
    Plain,
    Comment,
    String,
    Number,
    Identifier,
    Keyword,
    Visibility,
    BuiltInType,
    Constant,
    ControlKeyword,
    Type,
    StaticType,
    Interface,
    Enum,
    Struct,
    Delegate,
    TypeParameter,
    Method,
    Field,
    Property,
    Event,
    Namespace,
    Brace
}

public readonly record struct SourceToken(
    int Start,
    int Length,
    SourceTokenKind Kind,
    SymbolId? Target = null,
    string? SymbolName = null,
    int? BraceDepth = null,
    int? BracePair = null,
    int? DocumentOffset = null,
    string? Tooltip = null);

public readonly record struct SourceBrace(int Pair, int Depth, int Column);

public enum SourceLexicalMode { Normal, BlockComment, VerbatimString, RawString }

public sealed record SourceTokenizerState(
    SourceLexicalMode Mode,
    int RawQuoteCount,
    int NextBracePair,
    IReadOnlyList<SourceBrace> Braces)
{
    public static SourceTokenizerState Initial { get; } = new(SourceLexicalMode.Normal, 0, 0, []);
    public int BraceDepth => Braces.Count;
}

public sealed record SourceTokenizedLine(
    int Number,
    int StartOffset,
    string Text,
    IReadOnlyList<SourceToken> Tokens,
    SourceTokenizerState StartState,
    SourceTokenizerState EndState);

public readonly record struct SourceFindSpan(int Start, int Length, int DocumentOffset);

public static class SourceTokenizer
{
    private static readonly HashSet<string> Visibility = ["public", "private", "protected", "internal"];
    private static readonly HashSet<string> BuiltInTypes = ["bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "void", "dynamic"];
    private static readonly HashSet<string> Constants = ["false", "null", "true"];
    // Flow-control keywords render in the "control" color (magenta in VS-style themes); the remaining
    // declaration and modifier keywords stay in the plain keyword color, matching how VS/VS Code split
    // keyword.control from the rest. dnSpy uses one keyword color, so both map to it there.
    private static readonly HashSet<string> ControlKeywords = ["break", "case", "catch", "continue", "do", "else", "finally", "for", "foreach", "goto", "if", "in", "lock", "return", "switch", "throw", "try", "when", "while", "yield"];
    private static readonly HashSet<string> Keywords = ["abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "finally", "fixed", "for", "foreach", "get", "if", "implicit", "in", "interface", "is", "lock", "namespace", "new", "operator", "out", "override", "params", "readonly", "record", "ref", "required", "return", "sealed", "set", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try", "typeof", "unchecked", "unsafe", "using", "virtual", "volatile", "while", "yield"];
    private static readonly HashSet<string> ILKeywords = ["add", "and", "beq", "bge", "bgt", "ble", "blt", "bne", "box", "br", "break", "call", "calli", "callvirt", "castclass", "catch", "cil", "class", "constrained", "conv", "cpblk", "cpobj", "div", "dup", "endfilter", "endfinally", "event", "extends", "fault", "field", "filter", "finally", "hidebysig", "implements", "initblk", "initobj", "instance", "interface", "isinst", "jmp", "ldarg", "ldarga", "ldc", "ldelem", "ldelema", "ldfld", "ldflda", "ldftn", "ldind", "ldlen", "ldloc", "ldloca", "ldnull", "ldobj", "ldsfld", "ldsflda", "ldstr", "ldtoken", "ldvirtftn", "leave", "localloc", "managed", "maxstack", "method", "mul", "neg", "newarr", "newobj", "nop", "not", "or", "pop", "property", "readonly", "rem", "ret", "rethrow", "rtspecialname", "sealed", "shl", "shr", "sizeof", "specialname", "starg", "stelem", "stfld", "stind", "stloc", "stobj", "stsfld", "sub", "switch", "tail", "throw", "try", "unbox", "unaligned", "volatile", "xor"];

    public static (IReadOnlyList<SourceToken> Tokens, SourceTokenizerState EndState) Tokenize(
        string line,
        SourceTokenizerState state,
        IReadOnlyDictionary<string, SymbolId?>? symbolLinks = null,
        IReadOnlyDictionary<string, string>? typeKinds = null,
        string language = "csharp")
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(state);
        var tokens = new List<SourceToken>();
        var braces = state.Braces.ToList();
        var mode = state.Mode;
        // On a using/namespace directive every dotted segment is a namespace, so they get the
        // namespace color instead of falling through to the class color like any capitalized word.
        var namespaceLine = IsDirectiveLine(line);
        var rawQuoteCount = state.RawQuoteCount;
        var nextBracePair = state.NextBracePair;
        var index = 0;

        while (index < line.Length)
        {
            if (mode == SourceLexicalMode.BlockComment)
            {
                var end = line.IndexOf("*/", index, StringComparison.Ordinal);
                if (end < 0) { Add(tokens, index, line.Length - index, SourceTokenKind.Comment); index = line.Length; continue; }
                Add(tokens, index, end + 2 - index, SourceTokenKind.Comment);
                index = end + 2;
                mode = SourceLexicalMode.Normal;
                continue;
            }
            if (mode == SourceLexicalMode.VerbatimString)
            {
                var end = VerbatimEnd(line, index);
                Add(tokens, index, end.Index - index, SourceTokenKind.String);
                index = end.Index;
                if (end.Closed) mode = SourceLexicalMode.Normal;
                continue;
            }
            if (mode == SourceLexicalMode.RawString)
            {
                var end = RawEnd(line, index, rawQuoteCount);
                Add(tokens, index, end.Index - index, SourceTokenKind.String);
                index = end.Index;
                if (end.Closed) { mode = SourceLexicalMode.Normal; rawQuoteCount = 0; }
                continue;
            }

            var character = line[index];
            if (character == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                Add(tokens, index, line.Length - index, SourceTokenKind.Comment);
                break;
            }
            if (character == '/' && index + 1 < line.Length && line[index + 1] == '*')
            {
                var end = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(tokens, index, line.Length - index, SourceTokenKind.Comment);
                    mode = SourceLexicalMode.BlockComment;
                    break;
                }
                Add(tokens, index, end + 2 - index, SourceTokenKind.Comment);
                index = end + 2;
                continue;
            }

            if (TryRawStart(line, index, out var rawPrefixLength, out var quotes))
            {
                var end = RawEnd(line, index + rawPrefixLength, quotes);
                Add(tokens, index, end.Index - index, SourceTokenKind.String);
                index = end.Index;
                if (!end.Closed) { mode = SourceLexicalMode.RawString; rawQuoteCount = quotes; }
                continue;
            }
            if (TryVerbatimStart(line, index, out var verbatimPrefixLength))
            {
                var end = VerbatimEnd(line, index + verbatimPrefixLength);
                Add(tokens, index, end.Index - index, SourceTokenKind.String);
                index = end.Index;
                if (!end.Closed) mode = SourceLexicalMode.VerbatimString;
                continue;
            }
            if (character is '"' or '\'')
            {
                var end = QuotedEnd(line, index + 1, character);
                Add(tokens, index, end - index, SourceTokenKind.String);
                index = end;
                continue;
            }
            if (char.IsDigit(character))
            {
                var end = index + 1;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '.' or '_')) end++;
                Add(tokens, index, end - index, SourceTokenKind.Number);
                index = end;
                continue;
            }
            if (IsIdentifierStart(line, index))
            {
                var end = index + (character == '@' ? 1 : 0) + 1;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
                var word = line[index..end];
                var kind = ClassifyWord(line, word, end, typeKinds, namespaceLine, language);
                var isLinkable = kind is SourceTokenKind.Type or SourceTokenKind.StaticType or SourceTokenKind.Interface or SourceTokenKind.Enum or SourceTokenKind.Struct or SourceTokenKind.Delegate or SourceTokenKind.Method or SourceTokenKind.Field or SourceTokenKind.Property or SourceTokenKind.Event or SourceTokenKind.Identifier;
                SymbolId? target = null;
                string? symbolName = null;
                if (isLinkable && symbolLinks is not null && symbolLinks.TryGetValue(word, out var resolved))
                {
                    target = resolved;
                    symbolName = word;
                }
                tokens.Add(new SourceToken(index, end - index, kind, target, symbolName));
                index = end;
                continue;
            }
            if (character == '{')
            {
                var brace = new SourceBrace(nextBracePair++, braces.Count, VisualColumn(line, index));
                braces.Add(brace);
                tokens.Add(new SourceToken(index++, 1, SourceTokenKind.Brace, BraceDepth: brace.Depth, BracePair: brace.Pair));
                continue;
            }
            if (character == '}')
            {
                SourceBrace? brace = null;
                if (braces.Count > 0) { brace = braces[^1]; braces.RemoveAt(braces.Count - 1); }
                tokens.Add(new SourceToken(index++, 1, SourceTokenKind.Brace, BraceDepth: brace?.Depth ?? braces.Count, BracePair: brace?.Pair));
                continue;
            }

            var plainEnd = index + 1;
            while (plainEnd < line.Length && !StartsToken(line, plainEnd)) plainEnd++;
            Add(tokens, index, plainEnd - index, SourceTokenKind.Plain);
            index = plainEnd;
        }

        if (line.Length == 0) tokens.Add(new SourceToken(0, 0, SourceTokenKind.Plain));
        return (tokens, new SourceTokenizerState(mode, rawQuoteCount, nextBracePair, braces.ToArray()));
    }

    private static void Add(List<SourceToken> tokens, int start, int length, SourceTokenKind kind)
    {
        if (length > 0) tokens.Add(new SourceToken(start, length, kind));
    }

    private static bool StartsToken(string line, int index)
    {
        var character = line[index];
        return character is '/' or '"' or '\'' or '{' or '}' || char.IsDigit(character) || IsIdentifierStart(line, index) || TryVerbatimStart(line, index, out _) || TryRawStart(line, index, out _, out _);
    }

    private static bool IsIdentifierStart(string line, int index) =>
        char.IsLetter(line[index]) || line[index] == '_' || line[index] == '@' && index + 1 < line.Length && (char.IsLetter(line[index + 1]) || line[index + 1] == '_');

    private static SourceTokenKind ClassifyWord(string line, string word, int end, IReadOnlyDictionary<string, string>? typeKinds, bool namespaceLine, string language)
    {
        if (Visibility.Contains(word)) return SourceTokenKind.Visibility;
        if (Constants.Contains(word)) return SourceTokenKind.Constant;
        if (ControlKeywords.Contains(word)) return SourceTokenKind.ControlKeyword;
        if (Keywords.Contains(word)) return SourceTokenKind.Keyword;
        if (language.StartsWith("il", StringComparison.Ordinal) && ILKeywords.Contains(word.Split('.')[0])) return SourceTokenKind.Keyword;
        if (namespaceLine) return SourceTokenKind.Namespace;
        var next = end;
        while (next < line.Length && char.IsWhiteSpace(line[next])) next++;
        if (next < line.Length && line[next] == '(') return SourceTokenKind.Method;
        if (BuiltInTypes.Contains(word)) return SourceTokenKind.BuiltInType;
        // The metadata map tells types (class/interface/enum/struct/delegate) and the displayed
        // type's own members (field/property/event) apart, so properties and PascalCase fields keep
        // their member color instead of falling through to the class color like every capital word.
        if (typeKinds is not null && typeKinds.TryGetValue(word.TrimStart('@'), out var kind)) return MapKind(kind);
        if (char.IsUpper(word.TrimStart('@')[0])) return SourceTokenKind.Type;
        return word.StartsWith('_') ? SourceTokenKind.Field : SourceTokenKind.Identifier;
    }

    // A line is a namespace directive when its first word is using or namespace (optionally after a
    // leading global). Segments after a using alias's '=' are still colored as namespaces, which is a
    // deliberate simplification since aliases are rare in decompiled output.
    private static bool IsDirectiveLine(string line)
    {
        var span = line.AsSpan().TrimStart();
        if (StartsWithWord(span, "global")) span = span[6..].TrimStart();
        return StartsWithWord(span, "using") || StartsWithWord(span, "namespace");
    }

    private static bool StartsWithWord(ReadOnlySpan<char> span, string word) =>
        span.StartsWith(word, StringComparison.Ordinal) &&
        (span.Length == word.Length || !char.IsLetterOrDigit(span[word.Length]) && span[word.Length] != '_');

    private static SourceTokenKind MapKind(string kind) => kind switch
    {
        "staticclass" => SourceTokenKind.StaticType,
        "interface" => SourceTokenKind.Interface,
        "enum" => SourceTokenKind.Enum,
        "struct" => SourceTokenKind.Struct,
        "delegate" => SourceTokenKind.Delegate,
        "typeparam" => SourceTokenKind.TypeParameter,
        "field" => SourceTokenKind.Field,
        "property" => SourceTokenKind.Property,
        "event" => SourceTokenKind.Event,
        _ => SourceTokenKind.Type
    };

    private static int QuotedEnd(string line, int index, char quote)
    {
        var escaped = false;
        while (index < line.Length)
        {
            var character = line[index++];
            if (!escaped && character == quote) break;
            if (!escaped && character == '\\') escaped = true;
            else escaped = false;
        }
        return index;
    }

    private static int VisualColumn(string line, int end)
    {
        var column = 0;
        for (var index = 0; index < end; index++)
            column = line[index] == '\t' ? column + SourceDocumentModel.TabWidth - column % SourceDocumentModel.TabWidth : column + 1;
        return column;
    }

    private static (int Index, bool Closed) VerbatimEnd(string line, int index)
    {
        while (index < line.Length)
        {
            if (line[index++] != '"') continue;
            if (index < line.Length && line[index] == '"') { index++; continue; }
            return (index, true);
        }
        return (index, false);
    }

    private static (int Index, bool Closed) RawEnd(string line, int index, int quoteCount)
    {
        while (index < line.Length)
        {
            if (line[index] != '"') { index++; continue; }
            var run = 1;
            while (index + run < line.Length && line[index + run] == '"') run++;
            if (run >= quoteCount) return (index + quoteCount, true);
            index += run;
        }
        return (index, false);
    }

    private static bool TryVerbatimStart(string line, int index, out int prefixLength)
    {
        prefixLength = 0;
        if (line.AsSpan(index).StartsWith("@\"")) prefixLength = 2;
        else if (line.AsSpan(index).StartsWith("$@\"") || line.AsSpan(index).StartsWith("@$\"")) prefixLength = 3;
        return prefixLength > 0;
    }

    private static bool TryRawStart(string line, int index, out int prefixLength, out int quoteCount)
    {
        prefixLength = 0;
        quoteCount = 0;
        var cursor = index;
        while (cursor < line.Length && line[cursor] == '$') cursor++;
        var quoteStart = cursor;
        while (cursor < line.Length && line[cursor] == '"') cursor++;
        quoteCount = cursor - quoteStart;
        if (quoteCount < 3) return false;
        prefixLength = cursor - index;
        return true;
    }
}
