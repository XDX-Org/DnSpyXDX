using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DecompilerBackendTests
{
    [Fact]
    public async Task Rejects_unsupported_language_values()
    {
        await using var backend = new DecompilerBackend();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            backend.DecompileAsync(new SymbolId(Guid.Empty, 0x02000001), (DecompilerLanguage)99));
        Assert.Equal(DecompilerLanguage.CSharp, ((DecompilerLanguage)99).ValidOrDefault());
    }

    [Fact]
    public async Task Opens_browses_and_decompiles_a_managed_assembly()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        Assert.Equal("DnSpyXDX.Tests", assembly.Name);

        var root = await backend.GetChildrenAsync(assembly.RootNode);
        var namespaces = Assert.Single(root, n => n.Name == "Namespaces");
        var namespaceNodes = await backend.GetChildrenAsync(namespaces.Id);
        var ownNamespace = Assert.Single(namespaceNodes, n => n.Name == "DnSpyXDX.Tests");
        var types = await backend.GetChildrenAsync(ownNamespace.Id);
        var testType = Assert.Single(types, n => n.Name == nameof(DecompilerBackendTests));
        Assert.False(testType.HasChildren);
        Assert.Equal("public", testType.Visibility);
        Assert.Equal("class", testType.TypeDisplay);
        var members = await backend.GetChildrenAsync(testType.Id);
        var method = Assert.Single(members, n => n.Name == nameof(Opens_browses_and_decompiles_a_managed_assembly));
        Assert.Equal("public", method.Visibility);
        Assert.NotNull(method.TypeDisplay);
        var path = await backend.GetPathAsync(method.Symbol!.Value);
        Assert.Equal("root", path[0].Value);
        Assert.Equal("namespaces", path[1].Value);
        Assert.Equal(method.Id, path[^1]);
        var sampleEnum = Assert.Single(members, n => n.Name == nameof(SampleEnum));
        Assert.Equal("enum", sampleEnum.NameClassification);

        var topLevel = await backend.GetChildrenAsync(ownNamespace.Id);
        var staticClass = Assert.Single(topLevel, n => n.Name == nameof(SampleStatic));
        Assert.Equal("staticclass", staticClass.NameClassification);
        Assert.Equal("class", staticClass.TypeDisplay);
        var sampleDelegate = Assert.Single(topLevel, n => n.Name == nameof(SampleDelegate));
        Assert.Equal("delegate", sampleDelegate.NameClassification);
        Assert.Equal("delegate", sampleDelegate.TypeDisplay);
        var document = await backend.DecompileAsync(testType.Symbol!.Value, DecompilerLanguage.CSharp);

        Assert.Contains("class DecompilerBackendTests", document.Text, StringComparison.Ordinal);
        Assert.Contains("namespace DnSpyXDX.Tests;", document.Text, StringComparison.Ordinal);
        Assert.Contains("// Token: 0x", document.Text, StringComparison.Ordinal);
        Assert.Contains("RID:", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decompiled_documents_carry_links_for_types_in_the_same_assembly()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = await backend.GetChildrenAsync((await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces").Id);
        var types = await backend.GetChildrenAsync(namespaces.Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var testType = types.Single(n => n.Name == nameof(DecompilerBackendTests));

        var document = await backend.DecompileAsync(testType.Symbol!.Value, DecompilerLanguage.CSharp);

        Assert.NotNull(document.SymbolLinks);
        Assert.Equal(testType.Symbol!.Value, document.SymbolLinks![nameof(DecompilerBackendTests)]);
        Assert.True(document.SymbolLinks.ContainsKey(nameof(SourceTokenizerTests)));
        // Members of the type on screen are linkable too, scoped to that type.
        Assert.True(document.SymbolLinks.ContainsKey(nameof(Opens_browses_and_decompiles_a_managed_assembly)));
    }

    [Fact]
    public async Task Decompiles_csharp_il_and_sequence_point_annotated_il_independently()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var type = (await backend.SearchAsync(nameof(SampleMembers))).First(result =>
            result.Kind == "Type" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers");

        var csharp = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        var il = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.IL);
        var combined = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.ILWithCSharp);

        Assert.Equal("csharp", csharp.Language);
        Assert.Equal("il", il.Language);
        Assert.Equal("il-csharp", combined.Language);
        Assert.Contains("class SampleMembers", csharp.Text, StringComparison.Ordinal);
        Assert.Contains(".class", il.Text, StringComparison.Ordinal);
        Assert.Contains("IL_0000:", il.Text, StringComparison.Ordinal);
        Assert.Matches(@"// Token: 0x040[0-9A-F]{5} RID: \d+\n\s*\.field public", il.Text);
        Assert.DoesNotMatch(@"\.field\s+/\*\s*040[0-9A-F]{5}", il.Text);
        Assert.DoesNotMatch(@"/\*\s*[0-9A-F]{8}\s*\*/", il.Text);
        Assert.Contains("// C#:", combined.Text, StringComparison.Ordinal);
        Assert.Contains("Later();", combined.Text, StringComparison.Ordinal);
        Assert.Contains("// C#: if (update)", combined.Text, StringComparison.Ordinal);
        Assert.Contains("// C#: SampleField++;", combined.Text, StringComparison.Ordinal);
        Assert.Contains("// C#: return SampleField;", combined.Text, StringComparison.Ordinal);
        Assert.Single(combined.Text.Split('\n'), line => line.Contains("Later();", StringComparison.Ordinal));
        Assert.DoesNotContain("// C#: using ", combined.Text, StringComparison.Ordinal);
        Assert.Contains("IL_0000:", combined.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("// Decompiled C# reference", combined.Text, StringComparison.Ordinal);

        var later = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.Later)), result =>
            result.Kind == "Method" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.Later");
        Assert.Contains(il.References, reference =>
            reference.LocalTarget == later.Symbol && il.Text.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(nameof(SampleMembers.Later)));

        var branchType = Assert.Single(await backend.SearchAsync(nameof(SwitchFormattingSample)), result => result.Kind == "Type");
        var branchDocument = await backend.DecompileAsync(branchType.Symbol, DecompilerLanguage.IL);
        var branch = Assert.Single(branchDocument.References.Where(reference => reference.DocumentOffset is not null).Take(1));
        var label = branchDocument.Text.Substring(branch.StartOffset, branch.Length);
        Assert.StartsWith(label + ":", branchDocument.Text[branch.DocumentOffset!.Value..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_members_the_way_dnSpys_assembly_explorer_does()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = await backend.GetChildrenAsync((await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces").Id);
        var types = await backend.GetChildrenAsync(namespaces.Single(n => n.Name == "DnSpyXDX.Tests").Id);

        var members = await backend.GetChildrenAsync(types.Single(n => n.Name == nameof(SampleMembers)).Id);

        // methods (constructors included) -> properties -> events -> fields -> nested types
        var groups = members.Select(m => Group(m.Kind)).ToArray();
        Assert.Equal(groups.OrderBy(g => g), groups);
        Assert.Equal([0, 1, 2, 3, 4], groups.Distinct());
        // The constructor sits in the method group rather than in one of its own.
        Assert.Contains(members.TakeWhile(m => m.Kind != TreeNodeKind.Property), m => m.Kind == TreeNodeKind.Constructor);
    }

    [Fact]
    public async Task Hides_property_and_event_accessors_from_the_method_list()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = await backend.GetChildrenAsync((await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces").Id);
        var types = await backend.GetChildrenAsync(namespaces.Single(n => n.Name == "DnSpyXDX.Tests").Id);

        var members = await backend.GetChildrenAsync(types.Single(n => n.Name == nameof(SampleMembers)).Id);

        Assert.DoesNotContain(members, m => m.Name.StartsWith("get_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("add_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("remove_", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Name == nameof(SampleMembers.SampleMethod));

        // They are reachable by expanding the property or event that owns them.
        var property = members.Single(m => m.Kind == TreeNodeKind.Property);
        Assert.True(property.HasChildren);
        var accessors = await backend.GetChildrenAsync(property.Id);
        Assert.Equal(["get_SampleProperty", "set_SampleProperty"], accessors.Select(a => a.Name));

        var @event = members.Single(m => m.Kind == TreeNodeKind.Event);
        Assert.True(@event.HasChildren);
        var eventAccessors = await backend.GetChildrenAsync(@event.Id);
        Assert.Equal(["add_SampleEvent", "remove_SampleEvent"], eventAccessors.Select(a => a.Name));
    }

    [Fact]
    public async Task Displays_generic_types_with_their_parameter_names()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var ownNamespace = (await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests");

        var genericType = (await backend.GetChildrenAsync(ownNamespace.Id)).Single(n => n.Name == "GenericSample<TItem>");
        var members = await backend.GetChildrenAsync(genericType.Id);
        var constructor = members.Single(n => n.Kind == TreeNodeKind.Constructor);
        var searchResult = Assert.Single(await backend.SearchAsync("GenericSample"), result => result.Kind == "Type");
        var fieldSearchResult = Assert.Single(await backend.SearchAsync(nameof(GenericSample<object>.Field)), result => result.Kind == "Field" && result.Name == nameof(GenericSample<object>.Field));
        var document = await backend.DecompileAsync(genericType.Symbol!.Value, DecompilerLanguage.CSharp);

        Assert.Equal("GenericSample<TItem>", constructor.Name);
        Assert.Equal("TItem", members.Single(n => n.Name == nameof(GenericSample<object>.Item)).TypeDisplay);
        Assert.Equal("TItem", members.Single(n => n.Name == nameof(GenericSample<object>.Field)).TypeDisplay);
        Assert.Equal("Action<TItem>", members.Single(n => n.Kind == TreeNodeKind.Event && n.Name == nameof(GenericSample<object>.Changed)).TypeDisplay);
        Assert.Equal("TResult", members.Single(n => n.Name == nameof(GenericSample<object>.Convert)).TypeDisplay);
        Assert.Equal("GenericSample<TItem>", searchResult.Name);
        Assert.Equal("DnSpyXDX.Tests.GenericSample<TItem>", searchResult.QualifiedName);
        Assert.Equal("DnSpyXDX.Tests.GenericSample<TItem>.Field", fieldSearchResult.QualifiedName);
        Assert.Equal(genericType.Symbol, searchResult.DeclaringType);
        Assert.Equal(genericType.Symbol, fieldSearchResult.DeclaringType);
        Assert.Equal(genericType.Symbol, await backend.GetDeclaringTypeAsync(fieldSearchResult.Symbol));
        Assert.Equal("GenericSample<TItem>", document.Title);
    }

    [Fact]
    public async Task Searches_fully_qualified_nested_type_and_member_names()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);

        var nested = Assert.Single(await backend.SearchAsync("DnSpyXDX.Tests.SampleMembers.SampleNested"), result => result.Kind == "Type");
        var member = Assert.Single(await backend.SearchAsync("DnSpyXDX.Tests.SampleMembers.SampleField"), result => result.Kind == "Field");

        Assert.Equal("DnSpyXDX.Tests.SampleMembers.SampleNested", nested.QualifiedName);
        Assert.Equal("DnSpyXDX.Tests.SampleMembers.SampleField", member.QualifiedName);
    }

    [Fact]
    public async Task Opens_a_referenced_assembly_from_its_tree_node()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var references = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "References");
        var application = (await backend.GetChildrenAsync(references.Id)).Single(n => n.Name == "DnSpyXDX.Application");

        var opened = await backend.OpenReferenceAsync(application.Id);

        Assert.Equal("DnSpyXDX.Application", opened.Name);
        Assert.Contains(backend.Assemblies, candidate => candidate.SessionId == opened.SessionId);
        Assert.Equal(2, backend.Assemblies.Count);
        Assert.Equal(opened, await backend.OpenReferenceAsync(application.Id));
        Assert.Equal(2, backend.Assemblies.Count);
    }

    [Fact]
    public async Task Token_comments_are_attached_to_declarations_and_include_method_locations()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var ownNamespace = (await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests");
        var sampleType = (await backend.GetChildrenAsync(ownNamespace.Id)).Single(n => n.Name == nameof(SampleMembers));
        var members = await backend.GetChildrenAsync(sampleType.Id);
        var document = await backend.DecompileAsync(sampleType.Symbol!.Value, DecompilerLanguage.CSharp);
        var lines = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (var name in new[] { nameof(SampleMembers.CallsLater), nameof(SampleMembers.Later) })
        {
            var method = members.Single(member => member.Name == name);
            var declaration = Array.FindIndex(lines, line => line.Contains($"void {name}(", StringComparison.Ordinal));
            Assert.True(declaration > 0, $"Could not find the declaration for {name}.");
            var token = method.Symbol!.Value.MetadataToken;
            Assert.StartsWith($"// Token: 0x{token:X8} RID: {token & 0x00FFFFFF} RVA: 0x", lines[declaration - 1].Trim(), StringComparison.Ordinal);
            Assert.Contains(" File Offset: 0x", lines[declaration - 1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Indents_switch_labels_inside_the_switch_body()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var ownNamespace = (await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests");
        var sampleType = (await backend.GetChildrenAsync(ownNamespace.Id)).Single(n => n.Name == nameof(SwitchFormattingSample));

        var document = await backend.DecompileAsync(sampleType.Symbol!.Value, DecompilerLanguage.CSharp);

        Assert.Contains("\n\t\t\tcase 1:\n", document.Text, StringComparison.Ordinal);
        Assert.Contains("\n\t\t\t\tValue = 10;\n", document.Text, StringComparison.Ordinal);
    }

    private static int Group(TreeNodeKind kind) => kind switch
    {
        TreeNodeKind.Constructor or TreeNodeKind.Method => 0,
        TreeNodeKind.Property => 1,
        TreeNodeKind.Event => 2,
        TreeNodeKind.Field => 3,
        TreeNodeKind.Type => 4,
        _ => 5
    };

    private enum SampleEnum { One }
}

#pragma warning disable CS0067, CS0649 // this sample exists purely to be read back out of metadata
/// <summary>A top-level type carrying one of every member kind, so member ordering can be asserted
/// against something stable rather than whatever a test class happens to contain.</summary>
public sealed class SampleMembers
{
    public int SampleField;
    public int SampleProperty { get; set; }
    public event Action? SampleEvent;
    public void SampleMethod() { }
    public void CallsLater() => Later();
    public void Later() { }
    public int Combined(bool update)
    {
        if (update) SampleField++;
        return SampleField;
    }
    public sealed class SampleNested { }
}

public sealed class GenericSample<TItem>
{
    public TItem? Item { get; set; }
    public TItem? Field;
    public event Action<TItem>? Changed;
    public TResult? Convert<TResult>() => default;
}

public static class SampleStatic
{
    public static void Ping() { }
}

public delegate int SampleDelegate(int value);

public sealed class SwitchFormattingSample
{
    public int Value;

    public void Apply(int value)
    {
        switch (value)
        {
            case 1:
                Value = 10;
                break;
            case 2:
                Value = 20;
                break;
            default:
                Value = 0;
                break;
        }
    }
}
#pragma warning restore CS0067, CS0649
