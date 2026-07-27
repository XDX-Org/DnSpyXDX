using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using DnSpyXDX.Application;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Output;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace DnSpyXDX.Decompilation;

/// <summary>
/// Turns a decompiled C# syntax tree into text plus a list of <see cref="ClassifiedSpan"/>, classifying
/// every identifier by the symbol the decompiler bound it to. This is how dnSpy colors code: each token is
/// painted by what it actually resolves to (a class vs. a struct, a field vs. a property, a type reference
/// vs. a same-named member), which a purely lexical pass cannot recover.
/// </summary>
internal static class SemanticHighlighter
{
    public static (string Text, IReadOnlyList<ClassifiedSpan> Spans, IReadOnlyList<ReferenceSpan> References) Highlight(SyntaxTree tree, CSharpFormattingOptions formatting)
    {
        var buffer = new StringWriter { NewLine = "\n" };
        var inner = new TextWriterTokenWriter(buffer) { IndentationString = "\t" };
        var writer = new HighlightingTokenWriter(inner, buffer.GetStringBuilder());
        tree.AcceptVisitor(new CSharpOutputVisitor(writer, formatting));
        return (buffer.ToString(), writer.Spans, writer.References);
    }

    private static readonly HashSet<string> ControlKeywords =
        ["break", "case", "catch", "continue", "do", "else", "finally", "for", "foreach", "goto", "if", "in", "lock", "return", "switch", "throw", "try", "when", "while", "yield"];

    private sealed class HighlightingTokenWriter(TextWriterTokenWriter inner, StringBuilder buffer) : DecoratingTokenWriter(inner)
    {
        private readonly List<ClassifiedSpan> spans = [];
        private readonly List<ReferenceSpan> references = [];
        public IReadOnlyList<ClassifiedSpan> Spans => spans;
        public IReadOnlyList<ReferenceSpan> References => references;

        private void Record(int start, string? kind)
        {
            var end = buffer.Length;
            if (kind is not null && end > start) spans.Add(new ClassifiedSpan(start, end - start, kind));
        }

        public override void WriteIdentifier(Identifier identifier)
        {
            var start = buffer.Length;
            base.WriteIdentifier(identifier);
            Record(start, ClassifyIdentifier(identifier));
            RecordReference(identifier, start);
        }

        // Turns every identifier the decompiler bound to a definition in the assembly being shown into a
        // navigable link, targeting the exact symbol it resolved to. This is how extension methods and any
        // cross-type member become clickable: a purely name-based map can only reach the current type's own
        // members, whereas the bound symbol knows precisely which method, field, or type each token means.
        // The backend enables targets only when the exact defining module is open.
        private void RecordReference(Identifier identifier, int start)
        {
            var end = buffer.Length;
            if (end <= start) return;
            var node = identifier.Parent;
            if (node is null) return;
            var symbol = node.GetSymbol();
            while (symbol is null && node is VariableInitializer && node.Parent is not null)
            {
                node = node.Parent;
                symbol = node.GetSymbol();
            }
            // A method call's target (member.Method / Method) is a method group with no single symbol; the
            // resolved overload lives on the enclosing invocation, so reach for it. This is what makes
            // extension-method and cross-type call sites navigable rather than dead text.
            if (symbol is null && node is MemberReferenceExpression or IdentifierExpression &&
                node.Parent is InvocationExpression invocation && invocation.Target == node)
                symbol = invocation.GetSymbol();
            if (symbol is not IEntity entity || entity.ParentModule is not { MetadataFile: { } file } parentModule) return;
            var handle = entity.MetadataToken;
            if (handle.IsNil || handle.Kind is not (HandleKind.TypeDefinition or HandleKind.MethodDefinition or
                HandleKind.FieldDefinition or HandleKind.PropertyDefinition or HandleKind.EventDefinition)) return;
            var metadata = file.Metadata;
            var moduleMvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            var target = new SymbolId(moduleMvid, MetadataTokens.GetToken(handle));
            var name = entity.SymbolKind is SymbolKind.Constructor or SymbolKind.Destructor
                ? entity.DeclaringType?.Name ?? entity.Name
                : entity.Name;
            references.Add(new ReferenceSpan(start, end - start, target, parentModule.IsMainModule ? null : parentModule.AssemblyName, $"Go to {name}"));
        }

        public override void WriteKeyword(Role role, string keyword)
        {
            var start = buffer.Length;
            base.WriteKeyword(role, keyword);
            Record(start, keyword switch
            {
                "true" or "false" or "null" => "constant",
                _ when ControlKeywords.Contains(keyword) => "control",
                _ => "keyword"
            });
        }

        public override void WritePrimitiveType(string type)
        {
            var start = buffer.Length;
            base.WritePrimitiveType(type);
            Record(start, "keyword");
        }

        public override void WritePrimitiveValue(object value, LiteralFormat format = LiteralFormat.None)
        {
            var start = buffer.Length;
            base.WritePrimitiveValue(value, format);
            Record(start, value is string or char ? "string" : value is bool || value is null ? "constant" : "number");
        }

        public override void WriteInterpolatedText(string text)
        {
            var start = buffer.Length;
            base.WriteInterpolatedText(text);
            Record(start, "string");
        }

        public override void WriteComment(CommentType commentType, string content)
        {
            var start = buffer.Length;
            base.WriteComment(commentType, content);
            Record(start, "comment");
        }

        private static string? ClassifyIdentifier(Identifier identifier)
        {
            var node = identifier.Parent;
            if (node is null) return null;
            var symbol = node.GetSymbol();
            // A field or event name sits in a VariableInitializer whose symbol lives on the parent
            // declaration, so walk out of the initializer to recover it.
            while (symbol is null && node is VariableInitializer && node.Parent is not null)
            {
                node = node.Parent;
                symbol = node.GetSymbol();
            }
            if (symbol is null)
            {
                // A generic parameter's declaring occurrence (class Foo<T>, void M<T>()) carries no symbol.
                if (node is TypeParameterDeclaration) return "typeparam";
                // A type reference without a bound entity symbol - notably a type parameter used as a
                // member's type, e.g. T? Field - still resolves to a type; walk the type syntax to find it.
                for (var current = node; current is SimpleType or MemberType or ComposedType; current = current.Parent)
                    if (current.GetResolveResult() is TypeResolveResult typeResult) return ClassifyType(typeResult.Type);
                return null;
            }
            return symbol.SymbolKind switch
            {
                SymbolKind.TypeDefinition => ClassifyType(symbol as IType),
                SymbolKind.TypeParameter => "typeparam",
                SymbolKind.Field => symbol is IField field && field.DeclaringType?.Kind == TypeKind.Enum ? "enummember" : "field",
                SymbolKind.Property or SymbolKind.Indexer => "property",
                SymbolKind.Event => "event",
                SymbolKind.Method or SymbolKind.Operator or SymbolKind.Accessor => "method",
                // dnSpy paints a constructor's name with its type's color, not the method color.
                SymbolKind.Constructor or SymbolKind.Destructor => ClassifyType((symbol as IMember)?.DeclaringType),
                SymbolKind.Namespace => "namespace",
                SymbolKind.Parameter or SymbolKind.Variable => "local",
                _ => null
            };
        }

        private static string ClassifyType(IType? type) => type?.Kind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.TypeParameter => "typeparam",
            TypeKind.Class => type is ITypeDefinition { IsStatic: true } ? "staticclass" : "class",
            _ => "class"
        };
    }
}
