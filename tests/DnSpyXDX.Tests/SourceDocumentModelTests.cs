using DnSpyXDX.Application;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class SourceDocumentModelTests
{
    private static readonly SourceDocumentKey Key = new(Guid.Parse("fd4da291-c9d7-4db5-8d0c-12fa28425ac8"), 0x02000001, "csharp", "default");

    [Fact]
    public void Indexes_il_declaration_tokens_without_using_operand_tokens()
    {
        const string source = "// Token: 0x0600002A RID: 42\nIL_0001: call void C::M()\n// Token: 0x0600002A RID: 42\n.method public void M() cil managed";
        var model = SourceDocumentModel.Create(Key with { Language = "il" }, source);

        Assert.True(model.TryGetTokenLocation(0x0600002A, out var position));
        Assert.Equal(2, position.Line);
    }

    [Fact]
    public void Indexes_lf_crlf_empty_and_final_lines_without_normalizing_text()
    {
        const string source = "first\r\n\nthird\n";
        var model = SourceDocumentModel.Create(Key, source);

        Assert.Equal(4, model.LineCount);
        Assert.Equal("first", Text(model, 0));
        Assert.Equal("", Text(model, 1));
        Assert.Equal("third", Text(model, 2));
        Assert.Equal("", Text(model, 3));
        Assert.Equal(source, model.Text);
    }

    [Fact]
    public void Empty_document_has_one_empty_line()
    {
        var model = SourceDocumentModel.Create(Key, "");

        Assert.Equal(1, model.LineCount);
        Assert.Equal("", Text(model, 0));
        Assert.Equal(new SourcePosition(0, 0), model.GetPosition(0));
    }

    [Fact]
    public void Converts_utf16_offsets_including_surrogate_pairs()
    {
        const string source = "a😀b\nnext";
        var model = SourceDocumentModel.Create(Key, source);

        Assert.Equal(new SourcePosition(0, 3), model.GetPosition(3));
        Assert.Equal(3, model.GetOffset(new SourcePosition(0, 3)));
        Assert.Equal(new SourcePosition(1, 0), model.GetPosition(5));
    }

    [Fact]
    public void Maps_newline_offsets_to_the_previous_line_and_content_to_the_next()
    {
        var model = SourceDocumentModel.Create(Key, "one\r\ntwo");

        Assert.Equal(new SourcePosition(0, 3), model.GetPosition(3));
        Assert.Equal(new SourcePosition(0, 3), model.GetPosition(4));
        Assert.Equal(new SourcePosition(1, 0), model.GetPosition(5));
    }

    [Fact]
    public void Calculates_visual_columns_using_four_column_tab_stops()
    {
        var model = SourceDocumentModel.Create(Key, "a\tb\n12345\n\t\t");

        Assert.Equal(8, model.MaximumVisualColumns);
    }

    [Fact]
    public void Finds_metadata_token_comment_locations()
    {
        var model = SourceDocumentModel.Create(Key, "class C\n{\n    // Token: 0x0600002A RID: 42\n    void M() {}\n}");

        Assert.True(model.TryGetTokenLocation(0x0600002A, out var position));
        Assert.Equal(new SourcePosition(2, 4), position);
        Assert.False(model.TryGetTokenLocation(0x0600002B, out _));
    }

    [Fact]
    public void Builds_large_line_index_and_supports_boundary_lookup()
    {
        var source = string.Join('\n', Enumerable.Range(0, 100_000).Select(index => $"line {index}"));
        var model = SourceDocumentModel.Create(Key, source);

        Assert.Equal(100_000, model.LineCount);
        Assert.Equal("line 99999", Text(model, 99_999));
        Assert.Equal(99_999, model.GetPosition(source.Length).Line);
        Assert.True(model.EstimatedBytes >= source.Length * sizeof(char));
    }

    [Fact]
    public void Honors_cancellation_during_indexing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SourceDocumentModel.Create(Key, new string('x', 100_000), cancellation.Token));
    }

    [Fact]
    public void Creates_stable_document_key_from_document_identity()
    {
        var symbol = new SymbolId(Key.ModuleMvid, Key.MetadataToken);
        var document = new DecompilerDocument(symbol, "C", "csharp", "class C {}", [], []);

        Assert.Equal(Key, SourceDocumentKey.Create(document));
        Assert.NotEqual(Key, SourceDocumentKey.Create(document, "other-settings"));
    }

    private static string Text(SourceDocumentModel model, int line) => model.GetLine(line).GetText(model.Text).ToString();
}
