using System.Globalization;
using System.Text.RegularExpressions;
using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

public readonly record struct SourceDocumentKey(Guid ModuleMvid, int MetadataToken, string Language, string SettingsFingerprint)
{
    public static SourceDocumentKey Create(DecompilerDocument document, string settingsFingerprint = "default") =>
        new(document.Symbol.ModuleMvid, document.Symbol.MetadataToken, document.Language, settingsFingerprint);
}

public readonly record struct SourcePosition(int Line, int Column);

public readonly record struct SourceLineSlice(int Number, int StartOffset, int Length)
{
    public ReadOnlyMemory<char> GetText(string source) => source.AsMemory(StartOffset, Length);
}

/// <summary>Immutable, allocation-light index over one decompiled source document.</summary>
public sealed partial class SourceDocumentModel
{
    public const int TabWidth = 4;
    private const int TokenizerCheckpointInterval = 128;

    private readonly int[] lineStarts;
    private readonly int[] lineLengths;
    private readonly Dictionary<int, SourcePosition> tokenLocations;
    private readonly SortedDictionary<int, SourceTokenizerState> tokenizerCheckpoints = new() { [0] = SourceTokenizerState.Initial };
    private readonly SemaphoreSlim tokenizerGate = new(1, 1);

    private SourceDocumentModel(
        SourceDocumentKey key,
        string text,
        int[] lineStarts,
        int[] lineLengths,
        int maximumVisualColumns,
        Dictionary<int, SourcePosition> tokenLocations)
    {
        Key = key;
        Text = text;
        this.lineStarts = lineStarts;
        this.lineLengths = lineLengths;
        MaximumVisualColumns = maximumVisualColumns;
        this.tokenLocations = tokenLocations;
    }

    public SourceDocumentKey Key { get; }
    public string Text { get; }
    public int LineCount => lineStarts.Length;
    public int MaximumVisualColumns { get; }
    public long EstimatedBytes => Text.Length * sizeof(char) + (long)(lineStarts.Length + lineLengths.Length) * sizeof(int) + tokenLocations.Count * 24L;

    public static SourceDocumentModel Create(DecompilerDocument document, CancellationToken cancellationToken = default) =>
        Create(SourceDocumentKey.Create(document), document.Text, cancellationToken);

    public static SourceDocumentModel Create(SourceDocumentKey key, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = new List<int>(Math.Max(1, text.Length / 40)) { 0 };
        var lengths = new List<int>(starts.Capacity);
        var tokens = new Dictionary<int, SourcePosition>();
        var maximumColumns = 0;
        var lineStart = 0;
        var lineNumber = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[index] is not ('\r' or '\n')) continue;

            IndexLine(text, lineStart, index - lineStart, lineNumber, key.Language, tokens, ref maximumColumns);
            lengths.Add(index - lineStart);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            lineStart = index + 1;
            starts.Add(lineStart);
            lineNumber++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IndexLine(text, lineStart, text.Length - lineStart, lineNumber, key.Language, tokens, ref maximumColumns);
        lengths.Add(text.Length - lineStart);
        return new SourceDocumentModel(key, text, starts.ToArray(), lengths.ToArray(), maximumColumns, tokens);
    }

    public SourceLineSlice GetLine(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);
        return new SourceLineSlice(line, lineStarts[line], lineLengths[line]);
    }

    public SourcePosition GetPosition(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Text.Length);
        var line = Array.BinarySearch(lineStarts, offset);
        if (line < 0) line = ~line - 1;
        return new SourcePosition(line, Math.Min(offset - lineStarts[line], lineLengths[line]));
    }

    public int GetOffset(SourcePosition position)
    {
        var line = GetLine(position.Line);
        ArgumentOutOfRangeException.ThrowIfNegative(position.Column);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Column, line.Length);
        return line.StartOffset + position.Column;
    }

    public bool TryGetTokenLocation(int metadataToken, out SourcePosition position) => tokenLocations.TryGetValue(metadataToken, out position);

    public async Task<IReadOnlyList<SourceTokenizedLine>> TokenizeLinesAsync(
        int startLine,
        int count,
        IReadOnlyDictionary<string, SymbolId?>? symbolLinks = null,
        IReadOnlyDictionary<string, string>? typeKinds = null,
        IReadOnlyList<ReferenceSpan>? references = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startLine, LineCount);
        var take = Math.Min(count, LineCount - startLine);
        if (take == 0) return [];

        await tokenizerGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<IReadOnlyList<SourceTokenizedLine>>(() =>
            {
                var checkpoint = tokenizerCheckpoints.Last(pair => pair.Key <= startLine);
                var state = checkpoint.Value;
                var result = new List<SourceTokenizedLine>(take);
                var endLine = startLine + take;
                for (var lineNumber = checkpoint.Key; lineNumber < endLine; lineNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lineNumber > checkpoint.Key && lineNumber % TokenizerCheckpointInterval == 0)
                        tokenizerCheckpoints.TryAdd(lineNumber, state);
                    var line = GetLine(lineNumber);
                    var text = line.GetText(Text).ToString();
                    var startState = state;
                    var tokenized = SourceTokenizer.Tokenize(text, state, symbolLinks, typeKinds, Key.Language);
                    state = tokenized.EndState;
                    if (lineNumber >= startLine)
                        result.Add(new SourceTokenizedLine(lineNumber, line.StartOffset, text, ApplyReferences(tokenized.Tokens, line.StartOffset, references), startState, state));
                }
                return result;
            }, cancellationToken);
        }
        finally { tokenizerGate.Release(); }
    }

    private static IReadOnlyList<SourceToken> ApplyReferences(IReadOnlyList<SourceToken> tokens, int lineOffset, IReadOnlyList<ReferenceSpan>? references)
    {
        if (references is null || references.Count == 0) return tokens;
        var lineEnd = lineOffset + (tokens.Count == 0 ? 0 : tokens.Max(token => token.Start + token.Length));
        var relevant = references.Where(reference => reference.StartOffset < lineEnd && reference.StartOffset + reference.Length > lineOffset).ToArray();
        if (relevant.Length == 0) return tokens;
        var result = new List<SourceToken>(tokens.Count + relevant.Length);
        foreach (var token in tokens)
        {
            var boundaries = new SortedSet<int> { token.Start, token.Start + token.Length };
            foreach (var reference in relevant)
            {
                var start = Math.Clamp(reference.StartOffset - lineOffset, token.Start, token.Start + token.Length);
                var end = Math.Clamp(reference.StartOffset + reference.Length - lineOffset, token.Start, token.Start + token.Length);
                if (start < end) { boundaries.Add(start); boundaries.Add(end); }
            }
            var points = boundaries.ToArray();
            for (var index = 0; index < points.Length - 1; index++)
            {
                var start = points[index];
                var reference = relevant.FirstOrDefault(candidate => candidate.StartOffset <= lineOffset + start && lineOffset + start < candidate.StartOffset + candidate.Length);
                result.Add(token with
                {
                    Start = start,
                    Length = points[index + 1] - start,
                    Target = reference?.LocalTarget ?? token.Target,
                    SymbolName = reference is null ? token.SymbolName : reference.Tooltip,
                    DocumentOffset = reference?.DocumentOffset,
                    Tooltip = reference?.Tooltip
                });
            }
        }
        return result;
    }

    private static void IndexLine(
        string source,
        int start,
        int length,
        int lineNumber,
        string language,
        Dictionary<int, SourcePosition> tokenLocations,
        ref int maximumColumns)
    {
        var line = source.AsSpan(start, length);
        var columns = 0;
        foreach (var character in line)
            columns = character == '\t' ? columns + TabWidth - columns % TabWidth : columns + 1;
        maximumColumns = Math.Max(maximumColumns, columns);

        var match = TokenComment().Match(source, start, length);
        if (match.Success && language.StartsWith("il", StringComparison.Ordinal) && !NextLineIsILDeclaration(source, start + length)) match = Match.Empty;
        if (!match.Success) match = ILDeclarationToken().Match(source, start, length);
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token)) return;
        tokenLocations.TryAdd(token, new SourcePosition(lineNumber, match.Index - start));
    }

    private static bool NextLineIsILDeclaration(string source, int offset)
    {
        while (offset < source.Length && source[offset] is '\r' or '\n') offset++;
        while (offset < source.Length && char.IsWhiteSpace(source[offset]) && source[offset] is not ('\r' or '\n')) offset++;
        return offset < source.Length && ILDeclaration().IsMatch(source, offset);
    }

    [GeneratedRegex(@"//\s*Token:\s*0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex TokenComment();

    [GeneratedRegex(@"\s*\.(?:class|method|field|property|event)\b.*?/\*\s*([0-9A-Fa-f]{8})\s*\*/", RegexOptions.CultureInvariant)]
    private static partial Regex ILDeclarationToken();

    [GeneratedRegex(@"\G\.(?:class|method|field|property|event)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ILDeclaration();
}
