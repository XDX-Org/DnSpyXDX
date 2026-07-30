using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.UI;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DecompilerBackendTests
{
    [Fact]
    public async Task Reuses_an_existing_session_when_the_same_assembly_is_opened_again()
    {
        await using var backend = new DecompilerBackend();
        var path = typeof(DecompilerBackendTests).Assembly.Location;

        var first = await backend.OpenAsync(path);
        var second = await backend.OpenAsync(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)));

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(backend.Assemblies);
    }

    [Fact]
    public async Task Concurrent_duplicate_opens_create_one_session()
    {
        await using var backend = new DecompilerBackend();
        var path = typeof(DecompilerBackendTests).Assembly.Location;

        var opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => backend.OpenAsync(path)));

        Assert.Single(opened.Select(assembly => assembly.SessionId).Distinct());
        Assert.Single(backend.Assemblies);
    }

    [Fact]
    public async Task Unloading_immediately_after_open_is_safe_during_background_warmup()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);

        await backend.CloseAsync(assembly.SessionId);

        Assert.Empty(backend.Assemblies);
    }

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
        Assert.False(new RuntimeDisplaySettings().ShowMetadataTokens);
        Assert.False(new UiSessionState().ShowMetadataTokens);
        Assert.False(new RuntimeDisplaySettings().ShowTypeMembers);
        Assert.False(new UiSessionState().ShowTypeMembers);
        Assert.False(new RuntimeDisplaySettings().ShowCompilerGenerated);
        Assert.False(new UiSessionState().ShowCompilerGenerated);
        Assert.NotNull(document.DebugMap);
        Assert.Equal(document.Symbol, document.DebugMap.Document);
        Assert.NotEmpty(document.DebugMap.SequencePoints);
        Assert.All(document.DebugMap.SequencePoints, point =>
        {
            Assert.Equal(assembly.ModuleMvid, point.Location.Method.ModuleMvid);
            Assert.Equal(0x06, point.Location.Method.MetadataToken >> 24);
            Assert.True(point.Location.ILOffset >= 0);
            Assert.True(point.EndILOffset > point.Location.ILOffset);
            Assert.InRange(point.StartOffset, 0, document.Text.Length - 1);
            Assert.InRange(point.StartOffset + point.Length, 1, document.Text.Length);
        });
    }

    [Fact]
    public async Task Decompiled_breakpoints_use_control_flow_join_offsets()
    {
        var worker = Path.Combine(
            AppContext.BaseDirectory,
            "DnSpyXDX.Debugger.TestWorker.dll");
        Assert.True(File.Exists(worker), $"Missing test worker: {worker}");
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(worker);
        var target = Assert.Single(
            await backend.SearchAsync("DebuggerBreakpointTarget"),
            result =>
                result.Kind == "Type" &&
                result.Name == "DebuggerBreakpointTarget" &&
                result.Symbol.ModuleMvid == assembly.ModuleMvid);

        var document = await backend.DecompileAsync(
            target.Symbol,
            DecompilerLanguage.CSharp);
        var runMethod = Assert.Single(
            await backend.SearchAsync("Run"),
            result =>
                result.Kind == "Method" &&
                result.QualifiedName == "DebuggerBreakpointTarget.Run");
        var point = Assert.Single(
            document.DebugMap!.SequencePoints,
            candidate =>
                candidate.Location.Method.MetadataToken ==
                    runMethod.Symbol.MetadataToken &&
                document.Text.Substring(
                    candidate.StartOffset,
                    candidate.Length)
                    .Contains("return value", StringComparison.Ordinal));

        var breakpointLocation = Assert.IsType<DebugCodeLocation>(
            point.BreakpointLocation);
        Assert.Equal(point.Location.Method, breakpointLocation.Method);
        Assert.InRange(
            breakpointLocation.ILOffset,
            point.Location.ILOffset + 1,
            point.EndILOffset - 1);
    }

    [Fact]
    public async Task Decompiled_async_breakpoints_use_move_next_method_body()
    {
        var worker = Path.Combine(
            AppContext.BaseDirectory,
            "DnSpyXDX.Debugger.TestWorker.dll");
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(worker);
        var target = Assert.Single(
            await backend.SearchAsync("DebuggerBreakpointTarget"),
            result =>
                result.Kind == "Type" &&
                result.Name == "DebuggerBreakpointTarget" &&
                result.Symbol.ModuleMvid == assembly.ModuleMvid);
        var asyncMethod = Assert.Single(
            await backend.SearchAsync("RunAsync"),
            result =>
                result.Kind == "Method" &&
                result.QualifiedName ==
                    "DebuggerBreakpointTarget.RunAsync");
        var document = await backend.DecompileAsync(
            target.Symbol,
            DecompilerLanguage.CSharp);
        var point = Assert.Single(
            document.DebugMap!.SequencePoints,
            candidate =>
                document.Text.Substring(
                    candidate.StartOffset,
                    candidate.Length)
                    .Contains("await Task.Yield", StringComparison.Ordinal));

        Assert.NotEqual(
            asyncMethod.Symbol.MetadataToken,
            point.Location.Method.MetadataToken);
        using var stream = File.OpenRead(worker);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var runtimeMethod = metadata.GetMethodDefinition(
            (MethodDefinitionHandle)MetadataTokens.EntityHandle(
                point.Location.Method.MetadataToken));
        var body = peReader.GetMethodBody(
            runtimeMethod.RelativeVirtualAddress);
        var il = body.GetILBytes();
        Assert.NotNull(il);
        var codeSize = il.Length;
        Assert.InRange(point.Location.ILOffset, 0, codeSize - 1);
        Assert.InRange(
            (point.BreakpointLocation ?? point.Location).ILOffset,
            0,
            codeSize - 1);
    }

    [Fact]
    public async Task Opens_embedded_text_resources()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var resources = Assert.Single(await backend.GetChildrenAsync(assembly.RootNode), node => node.Name == "Resources");
        var resourceNodes = await backend.GetChildrenAsync(resources.Id);
        var resource = Assert.Single(resourceNodes, node => node.Name == "DnSpyXDX.Tests.sample-resource.txt");
        var suspicious = Assert.Single(resourceNodes, node => node.Name == "uSoY");

        var document = await backend.GetResourceAsync(resource.Id);

        Assert.Equal("Text", document.Kind);
        Assert.Equal("DnSpyXDX embedded resource test\n", document.Text?.ReplaceLineEndings("\n"));
        Assert.NotEmpty(document.Data);
        Assert.Contains("obfuscator", suspicious.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metadata_token_setting_updates_decompiled_output()
    {
        var displaySettings = new RuntimeDisplaySettings { ShowMetadataTokens = true };
        await using var backend = new DecompilerBackend(displaySettings);
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var type = Assert.Single(await backend.SearchAsync(nameof(SampleMembers)), result =>
            result.Kind == "Type" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers");

        var visible = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        displaySettings.ShowMetadataTokens = false;
        var hidden = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        var hiddenIl = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.IL);
        var hiddenCombined = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.ILWithCSharp);

        Assert.Contains("// Token: 0x", visible.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("// Token: 0x", hidden.Text, StringComparison.Ordinal);
        Assert.NotNull(hidden.SymbolLocations);
        Assert.Contains(hidden.SymbolLocations!, location => (location.Key >> 24) == 0x04);
        Assert.Contains(hidden.SymbolLocations!, location => (location.Key >> 24) == 0x06);
        Assert.Contains(hidden.SymbolLocations!, location => (location.Key >> 24) == 0x17);
        var field = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.SampleField)), result => result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.SampleField");
        var property = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.SampleProperty)), result => result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.SampleProperty");
        Assert.StartsWith("\tpublic int SampleField", hidden.Text[hidden.SymbolLocations[field.Symbol.MetadataToken]..], StringComparison.Ordinal);
        Assert.StartsWith("\tpublic int SampleProperty", hidden.Text[hidden.SymbolLocations[property.Symbol.MetadataToken]..], StringComparison.Ordinal);
        Assert.DoesNotMatch(@"/\*\s*[0-9A-Fa-f]{8}\s*\*/", hiddenIl.Text);
        Assert.Contains("// C#:", hiddenCombined.Text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"/\*\s*[0-9A-Fa-f]{8}\s*\*/", hiddenCombined.Text);
    }

    [Fact]
    public async Task Dnspy_member_order_groups_by_kind_in_the_configured_order()
    {
        var displaySettings = new RuntimeDisplaySettings();
        await using var backend = new DecompilerBackend(displaySettings);
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var type = Assert.Single(await backend.SearchAsync(nameof(SampleMembers)), result =>
            result.Kind == "Type" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers");

        var ilspy = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        displaySettings.MemberOrder = MemberOrder.DnSpy;
        var dnspy = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);

        Assert.NotEqual(ilspy.Text, dnspy.Text);

        // dnSpy groups members by kind into contiguous blocks, in the default order Methods, Properties,
        // Events, Fields, Nested Types. Within a block members keep declaration order (so the three properties
        // stay in source order). This guards the grouping and that fields/nested types are not floated to the
        // top by their low metadata-table tokens.
        int At(string text) => dnspy.Text.IndexOf(text, StringComparison.Ordinal);
        var blocks = new[] { "void SampleMethod", "int SampleProperty", "event Action", "public int SampleField;", "class SampleNested" };
        var positions = blocks.Select(At).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
        // Declaration order within the Properties block.
        Assert.True(At("int SampleProperty") < At("int CalculatedProperty") && At("int CalculatedProperty") < At("int AfterEventProperty"));

        // dnSpy mode also spells out a calculated getter-only property as a full accessor block instead of
        // ILSpy's expression body.
        Assert.Contains("public int CalculatedProperty => SampleField;", ilspy.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("=> SampleField;", dnspy.Text, StringComparison.Ordinal);
        Assert.Matches(@"public int CalculatedProperty\s*\{\s*get\s*\{\s*return SampleField;", dnspy.Text);

        Assert.Equal(MemberOrder.Ilspy, new RuntimeDisplaySettings().MemberOrder);
        Assert.Equal(MemberOrder.Ilspy, new UiSessionState().MemberOrder);
        Assert.Equal(MemberGroups.DefaultOrder, new RuntimeDisplaySettings().MemberGroupOrder);
        Assert.Null(new UiSessionState().MemberGroupOrder);
    }

    [Fact]
    public async Task Dnspy_member_group_order_is_configurable()
    {
        var displaySettings = new RuntimeDisplaySettings { MemberOrder = MemberOrder.DnSpy };
        await using var backend = new DecompilerBackend(displaySettings);
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var type = Assert.Single(await backend.SearchAsync(nameof(SampleMembers)), result =>
            result.Kind == "Type" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers");

        var defaultOrder = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        // Reverse the groups: fields and nested types to the top, methods to the bottom.
        displaySettings.MemberGroupOrder =
            [MemberGroup.Fields, MemberGroup.NestedTypes, MemberGroup.Events, MemberGroup.Properties, MemberGroup.Methods];
        var reordered = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);

        Assert.NotEqual(defaultOrder.Text, reordered.Text);
        int At(string text) => reordered.Text.IndexOf(text, StringComparison.Ordinal);
        Assert.True(At("public int SampleField;") < At("class SampleNested"), "Fields should now precede nested types.");
        Assert.True(At("class SampleNested") < At("int SampleProperty"), "Nested types should now precede properties.");
        Assert.True(At("int SampleProperty") < At("void SampleMethod"), "Methods should now come last.");
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
    public async Task Decompiled_documents_link_exact_symbols_in_other_open_assemblies()
    {
        await using var backend = new DecompilerBackend();
        var tests = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var testType = Assert.Single(await backend.SearchAsync(nameof(CrossAssemblyReferenceSample)), result =>
            result.Kind == "Type" && result.Name == nameof(CrossAssemblyReferenceSample) && result.Symbol.ModuleMvid == tests.ModuleMvid);

        var closedDocument = await backend.DecompileAsync(testType.Symbol, DecompilerLanguage.CSharp);
        var closedReference = Assert.Single(closedDocument.References, reference =>
            closedDocument.Text.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(nameof(DecompilerBackend)));
        Assert.NotNull(closedReference.LocalTarget);
        Assert.Equal("DnSpyXDX.Decompilation", closedReference.ExternalAssembly);

        var dependency = await backend.OpenAssemblyForSymbolAsync(closedReference.LocalTarget!.Value);
        var openDocument = await backend.DecompileAsync(testType.Symbol, DecompilerLanguage.CSharp);
        var openReference = Assert.Single(openDocument.References, reference =>
            openDocument.Text.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(nameof(DecompilerBackend)));
        Assert.Equal(dependency.ModuleMvid, openReference.LocalTarget?.ModuleMvid);
        Assert.Equal(openReference.LocalTarget, await backend.GetDeclaringTypeAsync(openReference.LocalTarget!.Value));

        await backend.CloseAsync(dependency.SessionId);
        var reopened = await backend.OpenAssemblyForSymbolAsync(openReference.LocalTarget!.Value);
        Assert.Equal(dependency.ModuleMvid, reopened.ModuleMvid);
    }

    [Fact]
    public async Task Decompiles_csharp_il_sequence_point_annotated_il_and_hex_independently()
    {
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings { ShowMetadataTokens = true });
        await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var type = (await backend.SearchAsync(nameof(SampleMembers))).First(result =>
            result.Kind == "Type" && result.QualifiedName == "DnSpyXDX.Tests.SampleMembers");

        var csharp = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.CSharp);
        var il = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.IL);
        var combined = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.ILWithCSharp);
        var hex = await backend.DecompileAsync(type.Symbol, DecompilerLanguage.Hex);

        Assert.Equal("csharp", csharp.Language);
        Assert.Equal("il", il.Language);
        Assert.Equal("il-csharp", combined.Language);
        Assert.Equal("hex", hex.Language);
        Assert.Contains("class SampleMembers", csharp.Text, StringComparison.Ordinal);
        Assert.Contains("\tpublic int SampleField", csharp.Text, StringComparison.Ordinal);
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
        Assert.NotNull(il.DebugMap);
        Assert.NotEmpty(il.DebugMap.SequencePoints);
        Assert.NotNull(combined.DebugMap);
        Assert.NotEmpty(combined.DebugMap.SequencePoints);
        Assert.All(il.DebugMap.SequencePoints.Concat(combined.DebugMap.SequencePoints), point =>
        {
            Assert.Equal(type.Symbol.ModuleMvid, point.Location.Method.ModuleMvid);
            Assert.Equal(0x06, point.Location.Method.MetadataToken >> 24);
            Assert.Equal(
                $"IL_{point.Location.ILOffset:X4}:",
                point.Length >= 8
                    ? il.Text.Substring(
                        il.DebugMap.SequencePoints
                            .First(candidate =>
                                candidate.Location == point.Location).StartOffset,
                        8)
                    : "",
                ignoreCase: true);
        });
        Assert.Empty(hex.Text);
        Assert.NotNull(hex.Binary);
        Assert.Equal([0x4D, 0x5A], hex.Binary![..2]);
        Assert.NotNull(hex.BinarySelectionOffset);
        Assert.True(hex.BinarySelectionLength > 0);
        Assert.InRange(hex.BinarySelectionOffset.Value + hex.BinarySelectionLength, 1, hex.Binary.Length);
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip == ".NET metadata");
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip.Contains("metadata heap", StringComparison.Ordinal));
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip.Contains("DecompilerBackendTests", StringComparison.Ordinal));
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip.StartsWith("#Blob 0x", StringComparison.Ordinal));
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip.Contains("TypeDef row", StringComparison.Ordinal));
        Assert.Contains(hex.BinaryRegions!, region => region.Tooltip.Contains("AssemblyRef row", StringComparison.Ordinal));
        Assert.True(hex.BinaryRegions!.Count(region => region.IsEntity) > 5);

        var methodResult = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.Later)), result => result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.Later");
        var fieldResult = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.SampleField)), result => result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.SampleField");
        var methodHex = await backend.DecompileAsync(methodResult.Symbol, DecompilerLanguage.Hex);
        var fieldHex = await backend.DecompileAsync(fieldResult.Symbol, DecompilerLanguage.Hex);
        Assert.Contains(methodHex.BinaryRegions!, region => region.Tooltip.Contains("MethodDef row", StringComparison.Ordinal));
        Assert.Contains(fieldHex.BinaryRegions!, region => region.Tooltip.Contains("Field row", StringComparison.Ordinal));

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
    public async Task Makes_extension_methods_and_cross_type_members_clickable_in_csharp()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var consumer = Assert.Single(await backend.SearchAsync(nameof(ExtensionConsumer)), result => result.Kind == "Type");
        var document = await backend.DecompileAsync(consumer.Symbol, DecompilerLanguage.CSharp);

        var doubled = Assert.Single(await backend.SearchAsync(nameof(SampleExtensions.Doubled)), result =>
            result.QualifiedName == "DnSpyXDX.Tests.SampleExtensions.Doubled");
        var sampleMethod = Assert.Single(await backend.SearchAsync(nameof(SampleMembers.SampleMethod)), result =>
            result.QualifiedName == "DnSpyXDX.Tests.SampleMembers.SampleMethod");

        // The extension method call site resolves to the extension's own type, which a name-based link map
        // (limited to the type being shown) could never reach.
        Assert.Contains(document.References, reference =>
            reference.LocalTarget == doubled.Symbol &&
            document.Text.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(nameof(SampleExtensions.Doubled)));
        // A plain call to another type's member is clickable too, pointing at that member.
        Assert.Contains(document.References, reference =>
            reference.LocalTarget == sampleMethod.Symbol &&
            document.Text.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(nameof(SampleMembers.SampleMethod)));
        // Every emitted reference lines up with the identifier text at its offset.
        Assert.All(document.References, reference => Assert.InRange(reference.StartOffset + reference.Length, 0, document.Text.Length));
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
    public async Task Assembly_browser_settings_control_type_expansion_and_compiler_generated_nodes()
    {
        var displaySettings = new RuntimeDisplaySettings { ShowTypeMembers = true };
        await using var backend = new DecompilerBackend(displaySettings);
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var ownNamespace = (await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests");

        var types = await backend.GetChildrenAsync(ownNamespace.Id);
        var host = types.Single(n => n.Name == nameof(CompilerGeneratedHost));
        Assert.True(host.HasChildren);
        Assert.DoesNotContain(types, n => n.Name == nameof(GeneratedTopLevel));

        var members = await backend.GetChildrenAsync(host.Id);
        Assert.Contains(members, n => n.Name == nameof(CompilerGeneratedHost.VisibleField));
        Assert.Contains(members, n => n.Name == nameof(CompilerGeneratedHost.VisibleNested));
        Assert.DoesNotContain(members, n => n.Name == nameof(CompilerGeneratedHost.GeneratedField));
        Assert.DoesNotContain(members, n => n.Name == nameof(CompilerGeneratedHost.GeneratedNested));

        displaySettings.ShowCompilerGenerated = true;
        types = await backend.GetChildrenAsync(ownNamespace.Id);
        Assert.Contains(types, n => n.Name == nameof(GeneratedTopLevel));
        host = types.Single(n => n.Name == nameof(CompilerGeneratedHost));
        members = await backend.GetChildrenAsync(host.Id);
        Assert.Contains(members, n => n.Name == nameof(CompilerGeneratedHost.GeneratedField));
        Assert.Contains(members, n => n.Name == nameof(CompilerGeneratedHost.GeneratedNested));

        displaySettings.ShowTypeMembers = false;
        types = await backend.GetChildrenAsync(ownNamespace.Id);
        Assert.False(types.Single(n => n.Name == nameof(CompilerGeneratedHost)).HasChildren);
    }

    [Fact]
    public async Task Compiler_generated_nested_types_are_included_in_the_type_document_when_enabled()
    {
        var displaySettings = new RuntimeDisplaySettings();
        await using var backend = new DecompilerBackend(displaySettings);
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var ownNamespace = (await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests");
        var host = (await backend.GetChildrenAsync(ownNamespace.Id)).Single(n => n.Name == nameof(CompilerGeneratedHost));

        var hidden = await backend.DecompileAsync(host.Symbol!.Value, DecompilerLanguage.CSharp);
        Assert.DoesNotContain("GeneratedAsync>d__", hidden.Text);

        displaySettings.ShowCompilerGenerated = true;
        var shown = await backend.DecompileAsync(host.Symbol!.Value, DecompilerLanguage.CSharp);
        Assert.Contains("GeneratedAsync>d__", shown.Text);
        var generatedLine = shown.Text.Split('\n').Single(line => line.Contains("GeneratedAsync>d__", StringComparison.Ordinal) && line.Contains("class", StringComparison.Ordinal));
        Assert.StartsWith("\t", generatedLine);
        var generatedIndex = shown.Text.IndexOf(generatedLine, StringComparison.Ordinal);
        var attributeBlock = shown.Text.LastIndexOf("\n\t[", generatedIndex, StringComparison.Ordinal);
        Assert.True(attributeBlock > 0 && shown.Text[attributeBlock - 1] == '\n');
    }

    [Fact]
    public async Task Hides_property_and_event_accessors_from_the_method_list()
    {
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings { ShowCompilerGenerated = true });
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
        var property = members.Single(m => m.Kind == TreeNodeKind.Property && m.Name == nameof(SampleMembers.SampleProperty));
        Assert.True(property.HasChildren);
        var accessors = await backend.GetChildrenAsync(property.Id);
        Assert.Equal(["get_SampleProperty", "set_SampleProperty"], accessors.Select(a => a.Name));

        var @event = members.Single(m => m.Kind == TreeNodeKind.Event);
        Assert.True(@event.HasChildren);
        var eventAccessors = await backend.GetChildrenAsync(@event.Id);
        Assert.Equal(["add_SampleEvent", "remove_SampleEvent"], eventAccessors.Select(a => a.Name));
    }

    [Fact]
    public async Task Semantic_spans_color_each_token_by_its_resolved_symbol()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var types = await backend.GetChildrenAsync((await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var sample = types.Single(n => n.Name == nameof(HighlightSample));

        var document = await backend.DecompileAsync(sample.Symbol!.Value, DecompilerLanguage.CSharp);
        var text = document.Text;
        var spans = document.SemanticSpans!;
        string KindAt(int index) => spans.Single(s => index >= s.Start && index < s.Start + s.Length).Kind;

        // "public Marker Marker;" — the type reference and the same-named field must be colored apart,
        // which only per-token symbol resolution can do.
        var field = text.IndexOf("Marker Marker;", StringComparison.Ordinal);
        Assert.Equal("struct", KindAt(field));                // the type reference
        Assert.Equal("field", KindAt(field + "Marker ".Length)); // the field named the same

        // Property access on an external type, an enum type, and an enum member.
        Assert.Equal("property", KindAt(text.IndexOf(".Length", StringComparison.Ordinal) + 1));
        Assert.Equal("enum", KindAt(text.IndexOf("HighlightChoice State", StringComparison.Ordinal)));
        Assert.Equal("property", KindAt(text.IndexOf("State {", StringComparison.Ordinal)));

        // An attribute name and a constructor name both take their type's color, not the method color.
        Assert.Equal("class", KindAt(text.IndexOf("Obsolete", StringComparison.Ordinal)));
        Assert.Equal("class", KindAt(text.IndexOf("HighlightSample(int", StringComparison.Ordinal)));

        // A genuine method call keeps the method color.
        Assert.Equal("method", KindAt(text.IndexOf("Measure(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Semantic_spans_cover_literals_and_control_flow()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var types = await backend.GetChildrenAsync((await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var sample = types.Single(n => n.Name == nameof(HighlightSample));

        var document = await backend.DecompileAsync(sample.Symbol!.Value, DecompilerLanguage.CSharp);
        var text = document.Text;
        var spans = document.SemanticSpans!;
        string KindAt(int index) => spans.Single(s => index >= s.Start && index < s.Start + s.Length).Kind;

        // Control-flow keywords, a numeric literal, an interpolated string and a plain string literal each
        // get their own classification straight from the syntax tree.
        Assert.Equal("control", KindAt(text.IndexOf("if (", StringComparison.Ordinal)));
        Assert.Equal("control", KindAt(text.IndexOf("return ", StringComparison.Ordinal)));
        Assert.Equal("keyword", KindAt(text.IndexOf("public ", StringComparison.Ordinal)));
        Assert.Equal("number", KindAt(text.IndexOf("> 0", StringComparison.Ordinal) + 2));
        Assert.Equal("string", KindAt(text.IndexOf("\"none\"", StringComparison.Ordinal)));
        Assert.Equal("string", KindAt(text.IndexOf("n={", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Generic_type_parameters_are_classified_from_the_syntax_tree()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var types = await backend.GetChildrenAsync((await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var generic = types.Single(n => n.Name == "GenericSample<TItem>");

        var document = await backend.DecompileAsync(generic.Symbol!.Value, DecompilerLanguage.CSharp);
        var text = document.Text;
        var spans = document.SemanticSpans!;
        string KindAt(int index) => spans.Single(s => index >= s.Start && index < s.Start + s.Length).Kind;

        Assert.Equal("typeparam", KindAt(text.IndexOf("TItem>", StringComparison.Ordinal)));
        Assert.Equal("typeparam", KindAt(text.IndexOf("TItem? Item", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Semantic_spans_repaint_rendered_tokens_end_to_end()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var types = await backend.GetChildrenAsync((await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var sample = types.Single(n => n.Name == nameof(HighlightSample));

        var document = await backend.DecompileAsync(sample.Symbol!.Value, DecompilerLanguage.CSharp);
        var model = SourceDocumentModel.Create(document);
        var lines = await model.TokenizeLinesAsync(0, model.LineCount, document.SymbolLinks, document.TypeClassifications, document.References, document.SemanticSpans);

        // The rendered line, not just the raw spans, must carry the resolved kinds: the type reference and
        // the same-named field on "public Marker Marker;" end up as distinct token kinds.
        var line = lines.Single(l => l.Text.Contains("Marker Marker;", StringComparison.Ordinal));
        var markers = line.Tokens.Where(t => line.Text.AsSpan(t.Start, t.Length).SequenceEqual("Marker")).ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Equal(SourceTokenKind.Struct, markers[0].Kind);
        Assert.Equal(SourceTokenKind.Field, markers[1].Kind);
    }

    [Fact]
    public async Task Classifies_members_and_types_of_nested_classes_from_the_outer_type()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(DecompilerBackendTests).Assembly.Location);
        var namespaces = (await backend.GetChildrenAsync(assembly.RootNode)).Single(n => n.Name == "Namespaces");
        var types = await backend.GetChildrenAsync((await backend.GetChildrenAsync(namespaces.Id)).Single(n => n.Name == "DnSpyXDX.Tests").Id);
        var outer = types.Single(n => n.Name == nameof(NestedHost));

        var document = await backend.DecompileAsync(outer.Symbol!.Value, DecompilerLanguage.CSharp);
        var text = document.Text;
        var spans = document.SemanticSpans!;
        string KindAt(int index) => spans.Single(s => index >= s.Start && index < s.Start + s.Length).Kind;

        // Members and types declared inside a nested class must be classified when the outer type is shown,
        // because the decompiled document renders those nested classes in full and the syntax tree carries
        // their bound symbols.
        Assert.Equal("enum", KindAt(text.IndexOf("NestedChoice Choice", StringComparison.Ordinal)));
        Assert.Equal("property", KindAt(text.IndexOf("Choice {", StringComparison.Ordinal)));
        Assert.Equal("field", KindAt(text.IndexOf("NestedField;", StringComparison.Ordinal)));
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
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings { ShowMetadataTokens = true });
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
    public int CalculatedProperty => SampleField;
    public event Action? SampleEvent;
    public int AfterEventProperty => SampleField;
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

[System.Runtime.CompilerServices.CompilerGenerated]
public sealed class GeneratedTopLevel { }

public sealed class CompilerGeneratedHost
{
    public int VisibleField;
    [System.Runtime.CompilerServices.CompilerGenerated]
    public int GeneratedField;
    public sealed class VisibleNested { }
    [System.Runtime.CompilerServices.CompilerGenerated]
    public sealed class GeneratedNested { }
    public async Task<int> GeneratedAsync()
    {
        await Task.Yield();
        return 1;
    }
}

public struct Marker { }

public sealed class HighlightSample
{
    public Marker Marker;
    public HighlightChoice State { get; set; }
    public HighlightSample(int seed) { }
    [System.Obsolete("x")]
    public int Measure(string text) => text.Length;
    public string Describe(int count)
    {
        if (count > 0)
        {
            return $"n={count}";
        }
        return "none";
    }
    public enum HighlightChoice { On, Off }
}

public sealed class NestedHost
{
    public sealed class NestedBody
    {
        public NestedChoice Choice { get; set; }
        public int NestedField;
        public enum NestedChoice { First, Second }
    }
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

public static class SampleExtensions
{
    public static int Doubled(this SampleMembers member) => member.SampleField * 2;
}

public sealed class ExtensionConsumer
{
    public int Use(SampleMembers member)
    {
        member.SampleMethod();
        return member.Doubled();
    }
}

public sealed class CrossAssemblyReferenceSample
{
    public DecompilerBackend? Backend { get; set; }
}

public delegate int SampleDelegate(int value);

public abstract class AnalyzerBase
{
    public virtual void Run() { }
}

public sealed class AnalyzerDerived : AnalyzerBase
{
    public override void Run() { }
}

public sealed class AnalyzerFactory
{
    public AnalyzerBase Make() => new AnalyzerDerived();
}

public interface IAnalyzerService
{
    void Serve();
}

public sealed class AnalyzerService : IAnalyzerService
{
    public void Serve() { }
}

public class AnalyzerPropertyHost
{
    public virtual int Level { get; set; }
    public event Action? Pinged;
    public int ReadLevel() => Level;                 // calls get_Level
    public void SetLevel(int value) => Level = value; // calls set_Level
    public void Subscribe(Action handler) => Pinged += handler; // calls add_Pinged
    public void Raise() => Pinged?.Invoke();          // loads the Pinged field and invokes it
}

public sealed class AnalyzerPropertyOverride : AnalyzerPropertyHost
{
    public override int Level { get; set; }
}

public interface IAnalyzerProperty
{
    int Count { get; }
}

public sealed class AnalyzerPropertyImpl : IAnalyzerProperty
{
    public int Count => 0;
}

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
