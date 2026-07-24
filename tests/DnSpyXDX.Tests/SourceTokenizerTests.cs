using DnSpyXDX.Application;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class SourceTokenizerTests
{
    [Fact]
    public async Task Applies_exact_symbol_and_document_links_to_il_tokens()
    {
        const string source = "br.s IL_0004\nIL_0004: call void C::M()";
        var symbol = new SymbolId(Guid.NewGuid(), 0x06000001);
        var references = new ReferenceSpan[]
        {
            new(source.IndexOf("IL_0004", StringComparison.Ordinal), 7, null, null, "Go to IL_0004", source.LastIndexOf("IL_0004", StringComparison.Ordinal)),
            new(source.IndexOf("M()", StringComparison.Ordinal), 1, symbol, null, "Go to M")
        };
        var model = SourceDocumentModel.Create(Key with { Language = "il" }, source);

        var lines = await model.TokenizeLinesAsync(0, 2, references: references);

        Assert.Equal(references[0].DocumentOffset, lines[0].Tokens.Single(token => token.DocumentOffset is not null).DocumentOffset);
        Assert.Equal(symbol, lines[1].Tokens.Single(token => token.Target is not null).Target);
    }

    [Fact]
    public void Highlights_il_directives_and_opcodes()
    {
        var (tokens, _) = SourceTokenizer.Tokenize(".method public hidebysig instance void M() cil managed", SourceTokenizerState.Initial, language: "il");
        var line = ".method public hidebysig instance void M() cil managed";

        Assert.Equal(SourceTokenKind.Keyword, tokens.Single(token => line.Substring(token.Start, token.Length) == "method").Kind);
        Assert.Equal(SourceTokenKind.Keyword, tokens.Single(token => line.Substring(token.Start, token.Length) == "cil").Kind);
        Assert.Equal(SourceTokenKind.Keyword, tokens.Single(token => line.Substring(token.Start, token.Length) == "managed").Kind);
    }

    private static readonly SourceDocumentKey Key = new(Guid.Empty, 0x02000001, "csharp", "default");
    private static readonly Dictionary<string, SymbolId?> Links = new(StringComparer.Ordinal)
    {
        ["AudioSource"] = new SymbolId(Guid.Empty, 0x02000004),
        ["Overloaded"] = null
    };

    [Fact]
    public void Classifies_existing_syntax_categories()
    {
        var (tokens, _) = SourceTokenizer.Tokenize("public AudioSource Run(string value = \"x\", int count = 42)", SourceTokenizerState.Initial, Links);

        Assert.Equal(SourceTokenKind.Visibility, Kind(tokens, "public", "public AudioSource Run(string value = \"x\", int count = 42)"));
        Assert.Equal(SourceTokenKind.Type, Kind(tokens, "AudioSource", "public AudioSource Run(string value = \"x\", int count = 42)"));
        Assert.Equal(SourceTokenKind.Method, Kind(tokens, "Run", "public AudioSource Run(string value = \"x\", int count = 42)"));
        Assert.Contains(tokens, token => token.Kind == SourceTokenKind.String);
        Assert.Contains(tokens, token => token.Kind == SourceTokenKind.Number);
        Assert.Equal(0x02000004, tokens.Single(token => token.SymbolName == "AudioSource").Target?.MetadataToken);
    }

    [Fact]
    public void Refines_type_names_into_their_declared_kind()
    {
        const string line = "public Widget Build(IHandler handler, Mode mode, Callback done, Data data)";
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IHandler"] = "interface",
            ["Mode"] = "enum",
            ["Data"] = "struct",
            ["Callback"] = "delegate"
        };

        var (tokens, _) = SourceTokenizer.Tokenize(line, SourceTokenizerState.Initial, Links, kinds);

        Assert.Equal(SourceTokenKind.Type, Kind(tokens, "Widget", line));
        Assert.Equal(SourceTokenKind.Interface, Kind(tokens, "IHandler", line));
        Assert.Equal(SourceTokenKind.Enum, Kind(tokens, "Mode", line));
        Assert.Equal(SourceTokenKind.Struct, Kind(tokens, "Data", line));
        Assert.Equal(SourceTokenKind.Delegate, Kind(tokens, "Callback", line));
    }

    [Fact]
    public void Distinguishes_static_classes_and_type_parameters()
    {
        const string line = "TResult Convert(Console console, TInput input)";
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Console"] = "staticclass",
            ["TResult"] = "typeparam",
            ["TInput"] = "typeparam"
        };

        var (tokens, _) = SourceTokenizer.Tokenize(line, SourceTokenizerState.Initial, null, kinds);

        Assert.Equal(SourceTokenKind.StaticType, Kind(tokens, "Console", line));
        Assert.Equal(SourceTokenKind.TypeParameter, Kind(tokens, "TResult", line));
        Assert.Equal(SourceTokenKind.TypeParameter, Kind(tokens, "TInput", line));
    }

    [Fact]
    public void Colors_known_members_instead_of_falling_through_to_the_class_color()
    {
        const string line = "Name = Count + Handler;";
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = "property",
            ["Count"] = "field",
            ["Handler"] = "event"
        };

        var (tokens, _) = SourceTokenizer.Tokenize(line, SourceTokenizerState.Initial, null, kinds);

        Assert.Equal(SourceTokenKind.Property, Kind(tokens, "Name", line));
        Assert.Equal(SourceTokenKind.Field, Kind(tokens, "Count", line));
        Assert.Equal(SourceTokenKind.Event, Kind(tokens, "Handler", line));
    }

    [Fact]
    public void Separates_modifier_keywords_from_control_flow_keywords()
    {
        const string declaration = "public abstract override string Run()";
        const string flow = "if (ok) return; else foreach (x) throw;";
        var (declarationTokens, _) = SourceTokenizer.Tokenize(declaration, SourceTokenizerState.Initial);
        var (flowTokens, _) = SourceTokenizer.Tokenize(flow, SourceTokenizerState.Initial);

        Assert.Equal(SourceTokenKind.Visibility, Kind(declarationTokens, "public", declaration));
        Assert.Equal(SourceTokenKind.Keyword, Kind(declarationTokens, "abstract", declaration));
        Assert.Equal(SourceTokenKind.Keyword, Kind(declarationTokens, "override", declaration));
        Assert.Equal(SourceTokenKind.ControlKeyword, Kind(flowTokens, "if", flow));
        Assert.Equal(SourceTokenKind.ControlKeyword, Kind(flowTokens, "return", flow));
        Assert.Equal(SourceTokenKind.ControlKeyword, Kind(flowTokens, "else", flow));
        Assert.Equal(SourceTokenKind.ControlKeyword, Kind(flowTokens, "throw", flow));
    }

    [Fact]
    public void Colors_using_and_namespace_segments_as_namespaces()
    {
        const string usingLine = "using System.Collections.Generic;";
        const string namespaceLine = "namespace DnSpyXDX.Tests;";
        var (usingTokens, _) = SourceTokenizer.Tokenize(usingLine, SourceTokenizerState.Initial, Links);
        var (namespaceTokens, _) = SourceTokenizer.Tokenize(namespaceLine, SourceTokenizerState.Initial, Links);

        Assert.Equal(SourceTokenKind.Keyword, Kind(usingTokens, "using", usingLine));
        Assert.Equal(SourceTokenKind.Namespace, Kind(usingTokens, "System", usingLine));
        Assert.Equal(SourceTokenKind.Namespace, Kind(usingTokens, "Collections", usingLine));
        Assert.Equal(SourceTokenKind.Namespace, Kind(usingTokens, "Generic", usingLine));
        Assert.Equal(SourceTokenKind.Keyword, Kind(namespaceTokens, "namespace", namespaceLine));
        Assert.Equal(SourceTokenKind.Namespace, Kind(namespaceTokens, "DnSpyXDX", namespaceLine));
        Assert.Equal(SourceTokenKind.Namespace, Kind(namespaceTokens, "Tests", namespaceLine));
    }

    [Fact]
    public void Does_not_resolve_names_in_comments_or_strings()
    {
        var (comment, _) = SourceTokenizer.Tokenize("// AudioSource", SourceTokenizerState.Initial, Links);
        var (literal, _) = SourceTokenizer.Tokenize("\"AudioSource\"", SourceTokenizerState.Initial, Links);

        Assert.All(comment, token => Assert.Null(token.Target));
        Assert.All(literal, token => Assert.Null(token.Target));
    }

    [Fact]
    public void Carries_block_comment_state_across_lines()
    {
        var (first, state) = SourceTokenizer.Tokenize("/* open", SourceTokenizerState.Initial);
        var (second, end) = SourceTokenizer.Tokenize("still */ public", state);

        Assert.Equal(SourceLexicalMode.BlockComment, state.Mode);
        Assert.Equal(SourceTokenKind.Comment, first.Single().Kind);
        Assert.Equal(SourceTokenKind.Comment, second[0].Kind);
        Assert.Contains(second, token => token.Kind == SourceTokenKind.Visibility);
        Assert.Equal(SourceLexicalMode.Normal, end.Mode);
    }

    [Fact]
    public void Carries_verbatim_and_raw_string_state_across_lines()
    {
        var (_, verbatim) = SourceTokenizer.Tokenize("var x = @\"open", SourceTokenizerState.Initial);
        var (verbatimEnd, normal) = SourceTokenizer.Tokenize("close\"; public", verbatim);
        var (_, raw) = SourceTokenizer.Tokenize("var y = \"\"\"open", SourceTokenizerState.Initial);
        var (rawEnd, rawNormal) = SourceTokenizer.Tokenize("close\"\"\"; public", raw);

        Assert.Equal(SourceLexicalMode.VerbatimString, verbatim.Mode);
        Assert.Contains(verbatimEnd, token => token.Kind == SourceTokenKind.Visibility);
        Assert.Equal(SourceLexicalMode.Normal, normal.Mode);
        Assert.Equal(SourceLexicalMode.RawString, raw.Mode);
        Assert.Contains(rawEnd, token => token.Kind == SourceTokenKind.Visibility);
        Assert.Equal(SourceLexicalMode.Normal, rawNormal.Mode);
    }

    [Fact]
    public void Carries_brace_pairs_and_ignores_braces_in_literals()
    {
        var (first, state) = SourceTokenizer.Tokenize("void M() { var x = \"{\";", SourceTokenizerState.Initial);
        var (second, end) = SourceTokenizer.Tokenize("}", state);
        var opening = first.Single(token => token.Kind == SourceTokenKind.Brace);
        var closing = second.Single(token => token.Kind == SourceTokenKind.Brace);

        Assert.Equal(opening.BracePair, closing.BracePair);
        Assert.Equal(0, opening.BraceDepth);
        Assert.Equal(0, end.BraceDepth);
    }

    [Fact]
    public async Task Range_tokenization_resumes_from_checkpoints_with_correct_state()
    {
        var lines = Enumerable.Range(0, 300).Select(index => index switch
        {
            120 => "/* open",
            140 => "close */ public class C",
            _ => $"line_{index}"
        });
        var model = SourceDocumentModel.Create(Key, string.Join('\n', lines));

        var warmup = await model.TokenizeLinesAsync(260, 1);
        var range = await model.TokenizeLinesAsync(130, 12);

        Assert.Single(warmup);
        Assert.Equal(SourceLexicalMode.BlockComment, range[0].StartState.Mode);
        Assert.All(range.Take(10), line => Assert.All(line.Tokens, token => Assert.Equal(SourceTokenKind.Comment, token.Kind)));
        Assert.Contains(range[^2].Tokens, token => token.Kind == SourceTokenKind.Visibility);
    }

    [Fact]
    public async Task Tokenized_lines_expose_starting_brace_depth_for_visible_guides()
    {
        var model = SourceDocumentModel.Create(Key, "class C\n{\n    void M()\n    {\n        Call();\n    }\n}");

        var lines = await model.TokenizeLinesAsync(4, 1);

        Assert.Equal(2, Assert.Single(lines).StartState.BraceDepth);
        Assert.Equal([0, 4], Assert.Single(lines).StartState.Braces.Select(brace => brace.Column));
    }

    [Fact]
    public void Brace_guides_use_visual_columns_with_tabs()
    {
        var (_, state) = SourceTokenizer.Tokenize("\t{", SourceTokenizerState.Initial);

        Assert.Equal(4, Assert.Single(state.Braces).Column);
    }

    [Fact]
    public async Task Range_tokenization_honors_cancellation()
    {
        var model = SourceDocumentModel.Create(Key, string.Join('\n', Enumerable.Repeat("public class C {}", 10_000)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.TokenizeLinesAsync(9_000, 100, cancellationToken: cancellation.Token));
    }

    private static SourceTokenKind Kind(IEnumerable<SourceToken> tokens, string value, string line) =>
        tokens.Single(token => line.AsSpan(token.Start, token.Length).SequenceEqual(value)).Kind;
}
