using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using DnSpyXDX.Application;

namespace DnSpyXDX.Decompilation;

public sealed class DecompilerBackend : IDecompilerBackend
{
    private readonly ConcurrentDictionary<Guid, AssemblySession> sessions = new();
    public IReadOnlyList<AssemblyDescriptor> Assemblies => sessions.Values.Select(s => s.Descriptor).OrderBy(s => s.Name).ToArray();

    public Task<AssemblyDescriptor> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Assembly not found.", fullPath);
            var session = AssemblySession.Open(fullPath);
            if (!sessions.TryAdd(session.Descriptor.SessionId, session)) { session.Dispose(); throw new InvalidOperationException("Could not add assembly session."); }
            return session.Descriptor;
        }, cancellationToken);
    }

    public async Task<AssemblyDescriptor> OpenReferenceAsync(NodeId reference, CancellationToken cancellationToken = default)
    {
        if (!sessions.TryGetValue(reference.SessionId, out var source)) throw new KeyNotFoundException("The referencing assembly is no longer open.");
        var name = source.GetReferenceName(reference);
        var loaded = sessions.Values.FirstOrDefault(s => string.Equals(s.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null) return loaded.Descriptor;
        var path = source.ResolveReferencePath(name)
            ?? throw new FileNotFoundException($"Could not find referenced assembly '{name}' beside {Path.GetFileName(source.Descriptor.Path)}.");
        return await OpenAsync(path, cancellationToken);
    }

    public Task CloseAsync(Guid sessionId)
    {
        if (sessions.TryRemove(sessionId, out var session)) session.Dispose();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TreeNodeDescriptor>> GetChildrenAsync(NodeId parent, CancellationToken cancellationToken = default) =>
        sessions.TryGetValue(parent.SessionId, out var session)
            ? Task.Run(() => session.GetChildren(parent, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<TreeNodeDescriptor>>([]);

    public async Task<DecompilerDocument> DecompileAsync(SymbolId symbol, DecompilerLanguage language, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        var session = sessions.Values.FirstOrDefault(s => s.Descriptor.ModuleMvid == symbol.ModuleMvid)
            ?? throw new KeyNotFoundException("The symbol's assembly is no longer open.");
        return await session.DecompileAsync(symbol, language, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetPathAsync(SymbolId symbol, CancellationToken cancellationToken = default)
    {
        var session = sessions.Values.FirstOrDefault(s => s.Descriptor.ModuleMvid == symbol.ModuleMvid)
            ?? throw new KeyNotFoundException("The symbol's assembly is no longer open.");
        return Task.Run(() => session.GetPath(symbol, cancellationToken), cancellationToken);
    }

    public Task<SymbolId> GetDeclaringTypeAsync(SymbolId symbol, CancellationToken cancellationToken = default)
    {
        var session = sessions.Values.FirstOrDefault(s => s.Descriptor.ModuleMvid == symbol.ModuleMvid)
            ?? throw new KeyNotFoundException("The symbol's assembly is no longer open.");
        return Task.Run(() => session.GetDeclaringType(symbol, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, IProgress<IReadOnlyList<SearchResult>>? progress = null) => Task.Run<IReadOnlyList<SearchResult>>(() =>
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var found = new List<SearchResult>();
        foreach (var result in sessions.Values.SelectMany(s => s.Search(query, cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            found.Add(result);
            if (found.Count % 50 == 0) progress?.Report(found.ToArray());
        }
        progress?.Report(found);
        return found;
    }, cancellationToken);

    public bool TryGetAssembly(Guid sessionId, out AssemblyDescriptor? assembly)
    {
        if (sessions.TryGetValue(sessionId, out var session)) { assembly = session.Descriptor; return true; }
        assembly = null; return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
        await Task.CompletedTask;
    }
}

internal sealed class AssemblySession : IDisposable
{
    private readonly PEFile module;
    private readonly MetadataReader metadata;
    private readonly CSharpDecompiler decompiler;
    private readonly MetadataTypeNameProvider typeNames;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(int Token, DecompilerLanguage Language), DecompilerDocument> cache = [];
    private IReadOnlyDictionary<string, SymbolId>? typeLinks;
    private IReadOnlyDictionary<string, string>? typeClassifications;
    public AssemblyDescriptor Descriptor { get; }

    private AssemblySession(PEFile module, CSharpDecompiler decompiler, AssemblyDescriptor descriptor)
    {
        this.module = module;
        metadata = module.Metadata;
        this.decompiler = decompiler;
        typeNames = new MetadataTypeNameProvider(metadata);
        Descriptor = descriptor;
    }

    public static AssemblySession Open(string path)
    {
        PEFile module;
        try { module = new PEFile(path, PEStreamOptions.PrefetchEntireImage); }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException) { throw new BadImageFormatException("The selected file is not a valid managed PE assembly.", ex); }
        if (!module.IsAssembly) { module.Dispose(); throw new BadImageFormatException("The selected file does not contain a managed assembly manifest."); }

        var metadata = module.Metadata;
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var resolver = new UniversalAssemblyResolver(path, false, module.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        settings.CSharpFormattingOptions.IndentSwitchBody = true;
        var decompiler = new CSharpDecompiler(module, resolver, settings);
        var sessionId = Guid.NewGuid();
        var descriptor = new AssemblyDescriptor(sessionId, mvid, name, path, module.DetectTargetFrameworkId() ?? "Unknown", module.Reader.PEHeaders.CoffHeader.Machine.ToString(), new NodeId(sessionId, "root"));
        return new AssemblySession(module, decompiler, descriptor);
    }

    public IReadOnlyList<TreeNodeDescriptor> GetChildren(NodeId parent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (parent.Value == "root") return
        [
            new(new NodeId(Descriptor.SessionId, "references"), "References", TreeNodeKind.Group, metadata.AssemblyReferences.Count > 0, Detail: metadata.AssemblyReferences.Count.ToString()),
            new(new NodeId(Descriptor.SessionId, "resources"), "Resources", TreeNodeKind.Group, metadata.ManifestResources.Count > 0, Detail: metadata.ManifestResources.Count.ToString()),
            new(new NodeId(Descriptor.SessionId, "namespaces"), "Namespaces", TreeNodeKind.Group, true)
        ];
        if (parent.Value == "references") return metadata.AssemblyReferences.Select(h =>
        {
            var r = metadata.GetAssemblyReference(h);
            return new TreeNodeDescriptor(new NodeId(Descriptor.SessionId, $"ref:{MetadataTokens.GetToken(h)}"), metadata.GetString(r.Name), TreeNodeKind.Reference, false, Detail: r.Version.ToString());
        }).OrderBy(x => x.Name).ToArray();
        if (parent.Value == "resources") return metadata.ManifestResources.Select(h =>
        {
            var r = metadata.GetManifestResource(h);
            return new TreeNodeDescriptor(new NodeId(Descriptor.SessionId, $"res:{MetadataTokens.GetToken(h)}"), metadata.GetString(r.Name), TreeNodeKind.Resource, false);
        }).OrderBy(x => x.Name).ToArray();
        if (parent.Value == "namespaces") return metadata.TypeDefinitions.Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Namespace)).Distinct().OrderBy(x => x).Select(ns => new TreeNodeDescriptor(new NodeId(Descriptor.SessionId, $"ns:{Uri.EscapeDataString(ns)}"), string.IsNullOrEmpty(ns) ? "<global>" : ns, TreeNodeKind.Namespace, true)).ToArray();
        if (parent.Value.StartsWith("ns:", StringComparison.Ordinal))
        {
            var ns = Uri.UnescapeDataString(parent.Value[3..]);
            return metadata.TypeDefinitions.Where(h =>
            {
                var t = metadata.GetTypeDefinition(h);
                return t.GetDeclaringType().IsNil && metadata.GetString(t.Namespace) == ns && metadata.GetString(t.Name) != "<Module>";
            }).Select(TypeNode).OrderBy(x => x.Name).ToArray();
        }
        if (parent.Value.StartsWith("type:", StringComparison.Ordinal)) return TypeChildren(MetadataTokens.TypeDefinitionHandle(ParseToken(parent)), ct);
        if (parent.Value.StartsWith("member:", StringComparison.Ordinal)) return AccessorChildren(MetadataTokens.EntityHandle(ParseToken(parent)));
        return [];
    }

    public string GetReferenceName(NodeId reference)
    {
        if (!reference.Value.StartsWith("ref:", StringComparison.Ordinal) ||
            !int.TryParse(reference.Value.AsSpan(4), out var token)) throw new ArgumentException("The node is not an assembly reference.", nameof(reference));
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.AssemblyReference) throw new ArgumentException("The node is not an assembly reference.", nameof(reference));
        return metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)handle).Name);
    }

    public string? ResolveReferencePath(string name)
    {
        var directory = Path.GetDirectoryName(Descriptor.Path)!;
        foreach (var extension in new[] { ".dll", ".exe", ".winmd" })
        {
            var exact = Path.Combine(directory, name + extension);
            if (File.Exists(exact)) return exact;
        }
        return Directory.EnumerateFiles(directory).FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(path) is var extension && (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".winmd", StringComparison.OrdinalIgnoreCase)));
    }

    private TreeNodeDescriptor TypeNode(TypeDefinitionHandle h)
    {
        var t = metadata.GetTypeDefinition(h);
        var kind = Classify(t);
        var keyword = kind == "staticclass" ? "class" : kind;
        return new(new NodeId(Descriptor.SessionId, $"type:{MetadataTokens.GetToken(h):X8}"), TypeDisplayName(t), TreeNodeKind.Type, false, new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(h)), Visibility: TypeVisibility(t.Attributes), TypeDisplay: keyword, NameClassification: kind, TypeClassification: "keyword");
    }

    private IReadOnlyList<TreeNodeDescriptor> TypeChildren(TypeDefinitionHandle handle, CancellationToken ct)
    {
        var t = metadata.GetTypeDefinition(handle);
        var genericContext = typeNames.CreateContext(t);
        var nodes = new List<TreeNodeDescriptor>();
        // dnSpy hangs property and event accessors off their owning node rather than listing them
        // beside real methods; without this the method list is mostly get_/set_/add_/remove_ noise.
        var accessors = PropertyAndEventMethods(t);
        foreach (var h in t.GetFields()) { ct.ThrowIfCancellationRequested(); var x = metadata.GetFieldDefinition(h); nodes.Add(MemberNode(h, metadata.GetString(x.Name), TreeNodeKind.Field, MemberVisibility(x.Attributes), x.DecodeSignature(typeNames, genericContext))); }
        foreach (var h in t.GetProperties()) { var x = metadata.GetPropertyDefinition(h); var access = x.GetAccessors(); nodes.Add(MemberNode(h, metadata.GetString(x.Name), TreeNodeKind.Property, AccessorVisibility(access.Getter, access.Setter), x.DecodeSignature(typeNames, genericContext).ReturnType, HasAny(access.Getter, access.Setter))); }
        foreach (var h in t.GetEvents()) { var x = metadata.GetEventDefinition(h); var access = x.GetAccessors(); nodes.Add(MemberNode(h, metadata.GetString(x.Name), TreeNodeKind.Event, AccessorVisibility(access.Adder, access.Remover), typeNames.GetTypeName(x.Type, genericContext), HasAny(access.Adder, access.Remover, access.Raiser))); }
        foreach (var h in t.GetMethods()) { ct.ThrowIfCancellationRequested(); if (!accessors.Contains(h)) nodes.Add(MethodNode(h, t)); }
        nodes.AddRange(t.GetNestedTypes().Select(TypeNode));
        return nodes.OrderBy(n => MemberRank(n.Kind)).ThenBy(n => n.Name).ToArray();
    }

    private TreeNodeDescriptor MethodNode(MethodDefinitionHandle h, TypeDefinition declaringType)
    {
        var x = metadata.GetMethodDefinition(h);
        var name = metadata.GetString(x.Name);
        var isConstructor = name is ".ctor" or ".cctor";
        return MemberNode(h,
            isConstructor ? TypeDisplayName(declaringType) : name,
            isConstructor ? TreeNodeKind.Constructor : TreeNodeKind.Method,
            MemberVisibility(x.Attributes),
            isConstructor ? null : x.DecodeSignature(typeNames, typeNames.CreateContext(declaringType, x)).ReturnType);
    }

    private HashSet<MethodDefinitionHandle> PropertyAndEventMethods(TypeDefinition type)
    {
        var accessors = new HashSet<MethodDefinitionHandle>();
        foreach (var h in type.GetProperties())
        {
            var access = metadata.GetPropertyDefinition(h).GetAccessors();
            AddAccessor(accessors, access.Getter, access.Setter);
            foreach (var other in access.Others) AddAccessor(accessors, other);
        }
        foreach (var h in type.GetEvents())
        {
            var access = metadata.GetEventDefinition(h).GetAccessors();
            AddAccessor(accessors, access.Adder, access.Remover, access.Raiser);
            foreach (var other in access.Others) AddAccessor(accessors, other);
        }
        return accessors;
    }

    private IReadOnlyList<TreeNodeDescriptor> AccessorChildren(EntityHandle owner)
    {
        IEnumerable<MethodDefinitionHandle> handles;
        TypeDefinitionHandle declaring;
        if (owner.Kind == HandleKind.PropertyDefinition)
        {
            var access = metadata.GetPropertyDefinition((PropertyDefinitionHandle)owner).GetAccessors();
            handles = new[] { access.Getter, access.Setter }.Concat(access.Others);
            declaring = DeclaringTypeOf(owner);
        }
        else if (owner.Kind == HandleKind.EventDefinition)
        {
            var access = metadata.GetEventDefinition((EventDefinitionHandle)owner).GetAccessors();
            handles = new[] { access.Adder, access.Remover, access.Raiser }.Concat(access.Others);
            declaring = DeclaringTypeOf(owner);
        }
        else return [];
        if (declaring.IsNil) return [];
        var type = metadata.GetTypeDefinition(declaring);
        return handles.Where(h => !h.IsNil).Select(h => MethodNode(h, type)).OrderBy(n => n.Name).ToArray();
    }

    private static void AddAccessor(HashSet<MethodDefinitionHandle> accessors, params MethodDefinitionHandle[] handles)
    {
        foreach (var handle in handles) if (!handle.IsNil) accessors.Add(handle);
    }

    private static bool HasAny(params MethodDefinitionHandle[] handles) => handles.Any(h => !h.IsNil);

    // dnSpy's assembly explorer order, from DocumentTreeViewConstants: methods (200), properties
    // (300), events (400), fields (500), nested types (600). Constructors have no group of their
    // own there - they sort by name among the methods.
    private static int MemberRank(TreeNodeKind kind) => kind switch
    {
        TreeNodeKind.Constructor or TreeNodeKind.Method => 200,
        TreeNodeKind.Property => 300,
        TreeNodeKind.Event => 400,
        TreeNodeKind.Field => 500,
        TreeNodeKind.Type => 600,
        _ => 700
    };

    private TreeNodeDescriptor MemberNode(EntityHandle h, string name, TreeNodeKind kind, string visibility, string? typeDisplay, bool hasChildren = false)
    {
        var token = MetadataTokens.GetToken(h);
        return new(new NodeId(Descriptor.SessionId, $"member:{token:X8}"), name, kind, hasChildren, new SymbolId(Descriptor.ModuleMvid, token), Visibility: visibility, TypeDisplay: typeDisplay, NameClassification: kind.ToString().ToLowerInvariant(), TypeClassification: IsStandardType(typeDisplay) ? "standard" : "type");
    }

    private bool IsEnum(TypeDefinition definition)
    {
        var baseType = definition.BaseType;
        return baseType.Kind == HandleKind.TypeReference && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)baseType).Name) == "Enum";
    }

    private static bool IsStandardType(string? type) => type is not null && type.TrimEnd('[', ']', '*', '?') is "bool" or "byte" or "char" or "decimal" or "double" or "float" or "int" or "long" or "object" or "sbyte" or "short" or "string" or "uint" or "ulong" or "ushort" or "void";

    private string AccessorVisibility(params MethodDefinitionHandle[] handles)
    {
        var values = handles.Where(h => !h.IsNil).Select(h => MemberVisibility(metadata.GetMethodDefinition(h).Attributes)).ToArray();
        return values.Contains("public") ? "public" : values.Contains("protected") ? "protected" : values.Contains("internal") ? "internal" : "private";
    }

    private static string MemberVisibility(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public", MethodAttributes.Family => "protected", MethodAttributes.Assembly => "internal",
        MethodAttributes.FamORAssem => "protected internal", MethodAttributes.FamANDAssem => "private protected", _ => "private"
    };
    private static string MemberVisibility(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public", FieldAttributes.Family => "protected", FieldAttributes.Assembly => "internal",
        FieldAttributes.FamORAssem => "protected internal", FieldAttributes.FamANDAssem => "private protected", _ => "private"
    };
    private static string TypeVisibility(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => "public", TypeAttributes.NestedFamily => "protected",
        TypeAttributes.NestedAssembly => "internal", TypeAttributes.NestedFamORAssem => "protected internal",
        TypeAttributes.NestedFamANDAssem => "private protected", TypeAttributes.NestedPrivate => "private", _ => "internal"
    };

    public async Task<DecompilerDocument> DecompileAsync(SymbolId symbol, DecompilerLanguage language, CancellationToken ct)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        var key = (symbol.MetadataToken, language);
        if (cache.TryGetValue(key, out var cached)) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached;
            var handle = MetadataTokens.EntityHandle(symbol.MetadataToken);
            decompiler.CancellationToken = ct;
            var text = await Task.Run(() => language switch
            {
                DecompilerLanguage.CSharp => DecompileCSharp(handle),
                DecompilerLanguage.IL => Disassemble(handle, ct),
                DecompilerLanguage.ILWithCSharp => DisassembleWithCSharp(handle, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(language))
            }, ct);
            ct.ThrowIfCancellationRequested();
            var title = GetEntityName(handle);
            var links = language == DecompilerLanguage.CSharp ? BuildSymbolLinks(handle) : null;
            var result = new DecompilerDocument(symbol, title, language.Key(), text, [], [], links, TypeClassifications: BuildClassifications(handle));
            cache[key] = result;
            return result;
        }
        finally { decompiler.CancellationToken = default; gate.Release(); }
    }

    private string DecompileCSharp(EntityHandle handle)
    {
        var source = decompiler.DecompileAsString([handle]);
        return AddTokenComments(AddNamespaceDeclaration(source, DeclaringTypeOf(handle)), handle);
    }

    private string Disassemble(EntityHandle handle, CancellationToken ct, bool formatDeclarationTokens = true)
    {
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, ct) { DetectControlStructure = true, ShowMetadataTokens = true };
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition: disassembler.DisassembleType(module, (TypeDefinitionHandle)handle); break;
            case HandleKind.MethodDefinition: disassembler.DisassembleMethod(module, (MethodDefinitionHandle)handle); break;
            case HandleKind.FieldDefinition: disassembler.DisassembleField(module, (FieldDefinitionHandle)handle); break;
            case HandleKind.PropertyDefinition: disassembler.DisassembleProperty(module, (PropertyDefinitionHandle)handle); break;
            case HandleKind.EventDefinition: disassembler.DisassembleEvent(module, (EventDefinitionHandle)handle); break;
            default: throw new NotSupportedException($"Cannot disassemble metadata handle {handle.Kind}.");
        }
        var text = output.ToString();
        return formatDeclarationTokens ? FormatMetadataTokens(text) : text;
    }

    private string DisassembleWithCSharp(EntityHandle handle, CancellationToken ct)
    {
        var syntaxTree = decompiler.Decompile([handle]);
        var csharp = syntaxTree.ToString();
        var sequencePoints = decompiler.CreateSequencePoints(syntaxTree)
            .Where(pair => pair.Key.Method?.MetadataToken.Kind == HandleKind.MethodDefinition)
            .GroupBy(pair => MetadataTokens.GetToken(pair.Key.Method!.MetadataToken))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(pair => pair.Value).Where(point => !point.IsHidden).OrderBy(point => point.Offset).ToArray());
        var il = Disassemble(handle, ct, formatDeclarationTokens: false);
        var sourceLines = csharp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(il.Length + csharp.Length / 3);
        IReadOnlyList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> points = [];
        ICSharpCode.Decompiler.DebugInfo.SequencePoint? previous = null;
        foreach (var line in il.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var method = MethodToken.Match(line);
            if (method.Success)
            {
                points = sequencePoints.GetValueOrDefault(Convert.ToInt32(method.Groups[1].Value, 16)) ?? [];
                previous = null;
            }
            var instruction = InstructionOffset.Match(line);
            if (instruction.Success && points.Count > 0)
            {
                var offset = Convert.ToInt32(instruction.Groups[1].Value, 16);
                var point = points.FirstOrDefault(candidate => candidate.Offset <= offset && offset < candidate.EndOffset);
                if (point is not null && !ReferenceEquals(point, previous))
                {
                    var annotation = SourceText(sourceLines, point);
                    if (annotation.Length > 0) output.Append(line.AsSpan(0, line.Length - line.TrimStart().Length)).Append("// C#: ").AppendLine(annotation);
                    previous = point;
                }
            }
            output.AppendLine(line);
        }
        return FormatMetadataTokens(output.ToString());
    }

    private static string FormatMetadataTokens(string text) => MetadataTokenLine.Replace(text, match =>
    {
        var indent = match.Groups[1].Value;
        var content = match.Groups[2].Value;
        var comments = new StringBuilder();
        foreach (Match tokenMatch in InlineMetadataToken.Matches(content))
        {
            var tokenText = tokenMatch.Groups[1].Value;
            var token = Convert.ToInt32(tokenText, 16);
            comments.Append(indent).Append("// Token: 0x").Append(tokenText.ToUpperInvariant()).Append(" RID: ").Append(token & 0x00FFFFFF).Append('\n');
        }
        return comments.Append(indent).Append(InlineMetadataToken.Replace(content, "").TrimEnd()).ToString();
    });

    private static string SourceText(string[] lines, ICSharpCode.Decompiler.DebugInfo.SequencePoint point)
    {
        var start = Math.Clamp(point.StartLine - 1, 0, lines.Length - 1);
        var end = Math.Clamp(point.EndLine - 1, start, lines.Length - 1);
        var text = string.Join(' ', lines[start..(end + 1)].Select(line => line.Trim()).Where(line => line.Length > 0));
        return Regex.Replace(text, "\\s+", " ");
    }

    private static readonly Regex MethodToken = new(@"\.method\s+/\*\s*([0-9A-Fa-f]{8})\s*\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InstructionOffset = new(@"\bIL_([0-9A-Fa-f]+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MetadataTokenLine = new(@"(?m)^([ \t]*)(.*?/\*\s*[0-9A-Fa-f]{8}\s*\*/.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InlineMetadataToken = new(@"\s*/\*\s*([0-9A-Fa-f]{8})\s*\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyDictionary<string, SymbolId> TypeLinks => typeLinks ??= BuildTypeLinks();

    // Maps the simple type names that appear in decompiled source back to their definitions so the
    // UI can turn them into go-to-definition links. Names shared by several types are dropped
    // rather than guessed at, so a click never lands on the wrong class.
    private IReadOnlyDictionary<string, SymbolId> BuildTypeLinks()
    {
        var byName = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in metadata.TypeDefinitions)
        {
            var name = metadata.GetString(metadata.GetTypeDefinition(h).Name);
            if (name.StartsWith('<')) continue;
            var display = name.Split('`')[0];
            if (display.Length == 0) continue;
            if (!byName.TryAdd(display, new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(h)))) ambiguous.Add(display);
        }
        foreach (var name in ambiguous) byName.Remove(name);
        return byName;
    }

    private IReadOnlyDictionary<string, string> TypeClassificationMap => typeClassifications ??= BuildTypeClassifications();

    // Records each type's declared kind (class/interface/enum/struct/delegate) keyed by the simple
    // name that appears in decompiled source, so the viewer can give enums, interfaces, structs and
    // delegates their own dnSpy-style colors. Names shared by several types are dropped, matching
    // BuildTypeLinks, so a color never misrepresents an ambiguous name.
    private IReadOnlyDictionary<string, string> BuildTypeClassifications()
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(h);
            var name = metadata.GetString(definition.Name);
            if (name.StartsWith('<')) continue;
            var display = name.Split('`')[0];
            if (display.Length == 0) continue;
            if (!byName.TryAdd(display, Classify(definition))) ambiguous.Add(display);
        }
        foreach (var name in ambiguous) byName.Remove(name);
        return byName;
    }

    // Combines the assembly-wide type kinds with the members declared by the type being shown, so the
    // viewer can color a name by what it actually is. Members win over a same-named type, mirroring how
    // BuildSymbolLinks resolves the click target.
    private IReadOnlyDictionary<string, string> BuildClassifications(EntityHandle selected)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in TypeClassificationMap) map[pair.Key] = pair.Value;

        var typeHandle = DeclaringTypeOf(selected);
        if (typeHandle.IsNil) return map;
        var type = metadata.GetTypeDefinition(typeHandle);

        void AddMember(string name, string kind)
        {
            if (name.Length == 0 || name.StartsWith('<')) return;
            map[name] = kind;
        }

        foreach (var h in type.GetFields()) AddMember(metadata.GetString(metadata.GetFieldDefinition(h).Name), "field");
        foreach (var h in type.GetProperties()) AddMember(metadata.GetString(metadata.GetPropertyDefinition(h).Name), "property");
        foreach (var h in type.GetEvents()) AddMember(metadata.GetString(metadata.GetEventDefinition(h).Name), "event");
        // Generic parameters are added last so that inside the type's own source a name like T reads as
        // a type parameter rather than a same-named class, matching dnSpy's distinct parameter color.
        foreach (var h in type.GetGenericParameters()) AddMember(metadata.GetString(metadata.GetGenericParameter(h).Name), "typeparam");
        foreach (var m in type.GetMethods())
            foreach (var h in metadata.GetMethodDefinition(m).GetGenericParameters())
                AddMember(metadata.GetString(metadata.GetGenericParameter(h).Name), "typeparam");
        return map;
    }

    private string Classify(TypeDefinition definition)
    {
        if ((definition.Attributes & TypeAttributes.Interface) != 0) return "interface";
        if (IsEnum(definition)) return "enum";
        if (IsDelegate(definition)) return "delegate";
        if (IsValueType(definition)) return "struct";
        return IsStaticClass(definition) ? "staticclass" : "class";
    }

    // A static class compiles to an abstract sealed class; that flag pair is unique to static classes,
    // so it distinguishes them from ordinary, abstract, or sealed classes.
    private static bool IsStaticClass(TypeDefinition definition) =>
        (definition.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Interface)) == (TypeAttributes.Abstract | TypeAttributes.Sealed);

    private bool IsBaseType(TypeDefinition definition, params string[] names)
    {
        var baseType = definition.BaseType;
        if (baseType.Kind != HandleKind.TypeReference) return false;
        var name = metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)baseType).Name);
        return names.Contains(name, StringComparer.Ordinal);
    }

    private bool IsValueType(TypeDefinition definition) => IsBaseType(definition, "ValueType");
    private bool IsDelegate(TypeDefinition definition) => IsBaseType(definition, "MulticastDelegate", "Delegate");

    // Every identifier the viewer can highlight in one document: assembly-wide type names plus the
    // members of the type being shown. Members are scoped to that type so a name like "value"
    // resolves here rather than to an unrelated class. A null target means the name is highlightable
    // but not navigable, which is how overloads are handled - all of them light up, none of them
    // wins the click.
    private IReadOnlyDictionary<string, SymbolId?> BuildSymbolLinks(EntityHandle selected)
    {
        var links = new Dictionary<string, SymbolId?>(StringComparer.Ordinal);
        foreach (var pair in TypeLinks) links[pair.Key] = pair.Value;

        var typeHandle = DeclaringTypeOf(selected);
        if (typeHandle.IsNil) return links;
        var type = metadata.GetTypeDefinition(typeHandle);
        var declared = new HashSet<string>(StringComparer.Ordinal);

        void AddMember(EntityHandle handle, string name)
        {
            if (name.Length == 0 || name.StartsWith('<') || name is ".ctor" or ".cctor") return;
            // A member shadows a same-named type; a repeated member name is an overload set.
            links[name] = declared.Add(name) ? new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(handle)) : null;
        }

        foreach (var h in type.GetFields()) AddMember(h, metadata.GetString(metadata.GetFieldDefinition(h).Name));
        foreach (var h in type.GetProperties()) AddMember(h, metadata.GetString(metadata.GetPropertyDefinition(h).Name));
        foreach (var h in type.GetEvents()) AddMember(h, metadata.GetString(metadata.GetEventDefinition(h).Name));
        foreach (var h in type.GetMethods()) AddMember(h, metadata.GetString(metadata.GetMethodDefinition(h).Name));
        return links;
    }

    private TypeDefinitionHandle DeclaringTypeOf(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => (TypeDefinitionHandle)handle,
        HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType(),
        HandleKind.FieldDefinition => metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType(),
        HandleKind.PropertyDefinition => metadata.GetPropertyDefinition((PropertyDefinitionHandle)handle).GetAccessors() is var a && !a.Getter.IsNil ? metadata.GetMethodDefinition(a.Getter).GetDeclaringType() : FindPropertyDeclaringType((PropertyDefinitionHandle)handle),
        HandleKind.EventDefinition => metadata.GetEventDefinition((EventDefinitionHandle)handle).GetAccessors() is var e && !e.Adder.IsNil ? metadata.GetMethodDefinition(e.Adder).GetDeclaringType() : FindEventDeclaringType((EventDefinitionHandle)handle),
        _ => default
    };

    private string AddTokenComments(string source, EntityHandle selected)
    {
        if (selected.Kind != HandleKind.TypeDefinition) return TokenComment(selected) + Environment.NewLine + source;
        var type = metadata.GetTypeDefinition((TypeDefinitionHandle)selected);
        var declarations = new List<(EntityHandle Handle, string Name, bool Callable)>
        {
            (selected, TypeIdentifier(type), false)
        };
        declarations.AddRange(type.GetFields().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetFieldDefinition(h).Name), false)));
        declarations.AddRange(type.GetProperties().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetPropertyDefinition(h).Name), false)));
        declarations.AddRange(type.GetEvents().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetEventDefinition(h).Name), false)));
        declarations.AddRange(type.GetMethods().Where(h => !metadata.GetMethodDefinition(h).Attributes.HasFlag(MethodAttributes.SpecialName) || metadata.GetString(metadata.GetMethodDefinition(h).Name) is ".ctor" or ".cctor").Select(h =>
        {
            var name = metadata.GetString(metadata.GetMethodDefinition(h).Name);
            return ((EntityHandle)h, name is ".ctor" or ".cctor" ? TypeIdentifier(type) : name, true);
        }));

        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var insertions = new List<(int Index, string Comment)>();
        var used = new HashSet<int>();
        foreach (var declaration in declarations)
        {
            var candidates = Enumerable.Range(0, lines.Count)
                .Where(i => !used.Contains(i) && IsDeclarationLine(lines[i], declaration.Name, declaration.Callable))
                .ToArray();
            if (candidates.Length == 0) continue;
            var declarationIndent = candidates.Min(i => LeadingWhitespace(lines[i]));
            foreach (var i in candidates.Where(i => LeadingWhitespace(lines[i]) == declarationIndent))
            {
                used.Add(i);
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                insertions.Add((i, indent + TokenComment(declaration.Handle)));
                break;
            }
        }
        foreach (var insertion in insertions.OrderByDescending(x => x.Index)) lines.Insert(insertion.Index, insertion.Comment);
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsDeclarationLine(string line, string name, bool callable)
    {
        if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) return false;
        var searchFrom = 0;
        while (TryFindIdentifier(line, name, searchFrom, out var index))
        {
            var position = index + name.Length;
            while (position < line.Length && char.IsWhiteSpace(line[position])) position++;
            if (callable && position < line.Length && line[position] == '<')
            {
                var depth = 0;
                do
                {
                    if (line[position] == '<') depth++;
                    else if (line[position] == '>') depth--;
                    position++;
                }
                while (position < line.Length && depth > 0);
                while (position < line.Length && char.IsWhiteSpace(line[position])) position++;
            }
            var followedByParameters = position < line.Length && line[position] == '(';
            if (callable == followedByParameters) return true;
            searchFrom = index + name.Length;
        }
        return false;
    }

    private static int LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && char.IsWhiteSpace(line[length])) length++;
        return length;
    }

    private static bool TryFindIdentifier(string line, string name, int startIndex, out int index)
    {
        index = line.IndexOf(name, startIndex, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(line[index - 1]) && line[index - 1] != '_';
            var end = index + name.Length;
            var after = end == line.Length || !char.IsLetterOrDigit(line[end]) && line[end] != '_';
            if (before && after) return true;
            index = line.IndexOf(name, end, StringComparison.Ordinal);
        }
        return false;
    }

    private string TokenComment(EntityHandle handle)
    {
        var token = MetadataTokens.GetToken(handle);
        var comment = $"// Token: 0x{token:X8} RID: {token & 0x00FFFFFF}";
        if (handle.Kind != HandleKind.MethodDefinition) return comment;
        var rva = metadata.GetMethodDefinition((MethodDefinitionHandle)handle).RelativeVirtualAddress;
        if (rva == 0) return comment;
        return TryGetFileOffset(rva, out var offset)
            ? $"{comment} RVA: 0x{rva:X8} File Offset: 0x{offset:X8}"
            : $"{comment} RVA: 0x{rva:X8}";
    }

    private bool TryGetFileOffset(int rva, out int offset)
    {
        foreach (var section in module.Reader.PEHeaders.SectionHeaders)
        {
            var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + size) continue;
            offset = rva - section.VirtualAddress + section.PointerToRawData;
            return true;
        }
        offset = 0;
        return false;
    }

    public IEnumerable<SearchResult> Search(string query, CancellationToken ct)
    {
        foreach (var h in metadata.TypeDefinitions)
        {
            ct.ThrowIfCancellationRequested();
            var t = metadata.GetTypeDefinition(h); var metadataName = metadata.GetString(t.Name); var typeName = TypeDisplayName(t);
            var typeResult = Result(h, typeName, "Type");
            if (Matches(typeResult, metadataName, query)) yield return typeResult;
            foreach (var m in t.GetMethods()) { var name = metadata.GetString(metadata.GetMethodDefinition(m).Name); var result = Result(m, name, "Method"); if (Matches(result, name, query)) yield return result; }
            foreach (var f in t.GetFields()) { var name = metadata.GetString(metadata.GetFieldDefinition(f).Name); var result = Result(f, name, "Field"); if (Matches(result, name, query)) yield return result; }
            foreach (var p in t.GetProperties()) { var name = metadata.GetString(metadata.GetPropertyDefinition(p).Name); var result = Result(p, name, "Property"); if (Matches(result, name, query)) yield return result; }
            foreach (var e in t.GetEvents()) { var name = metadata.GetString(metadata.GetEventDefinition(e).Name); var result = Result(e, name, "Event"); if (Matches(result, name, query)) yield return result; }
        }
    }

    private static bool Matches(SearchResult result, string metadataName, string query) =>
        result.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        metadataName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        result.QualifiedName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<NodeId> GetPath(SymbolId symbol, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var handle = MetadataTokens.EntityHandle(symbol.MetadataToken);
        var typeHandle = DeclaringTypeOf(handle);
        if (typeHandle.IsNil) return [Descriptor.RootNode];
        var chain = new Stack<TypeDefinitionHandle>();
        for (var current = typeHandle; !current.IsNil; current = metadata.GetTypeDefinition(current).GetDeclaringType()) chain.Push(current);
        var outer = chain.Peek();
        var ns = metadata.GetString(metadata.GetTypeDefinition(outer).Namespace);
        var path = new List<NodeId> { Descriptor.RootNode, new(Descriptor.SessionId, "namespaces"), new(Descriptor.SessionId, $"ns:{Uri.EscapeDataString(ns)}") };
        while (chain.Count > 0) { var type = chain.Pop(); path.Add(new NodeId(Descriptor.SessionId, $"type:{MetadataTokens.GetToken(type):X8}")); }
        if (handle.Kind != HandleKind.TypeDefinition) path.Add(new NodeId(Descriptor.SessionId, $"member:{symbol.MetadataToken:X8}"));
        return path;
    }

    public SymbolId GetDeclaringType(SymbolId symbol, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var type = DeclaringTypeOf(MetadataTokens.EntityHandle(symbol.MetadataToken));
        if (type.IsNil) throw new ArgumentException("The symbol is not declared by a type.", nameof(symbol));
        return new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(type));
    }

    private TypeDefinitionHandle FindPropertyDeclaringType(PropertyDefinitionHandle target) => metadata.TypeDefinitions.FirstOrDefault(t => metadata.GetTypeDefinition(t).GetProperties().Contains(target));
    private TypeDefinitionHandle FindEventDeclaringType(EventDefinitionHandle target) => metadata.TypeDefinitions.FirstOrDefault(t => metadata.GetTypeDefinition(t).GetEvents().Contains(target));

    private string AddNamespaceDeclaration(string source, TypeDefinitionHandle typeHandle)
    {
        if (typeHandle.IsNil) return source;
        var outer = typeHandle;
        while (!metadata.GetTypeDefinition(outer).GetDeclaringType().IsNil) outer = metadata.GetTypeDefinition(outer).GetDeclaringType();
        var ns = metadata.GetString(metadata.GetTypeDefinition(outer).Namespace);
        if (string.IsNullOrEmpty(ns)) return source;
        if (source.Contains($"namespace {ns}", StringComparison.Ordinal)) return source;

        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var insertion = 0;
        while (insertion < lines.Count)
        {
            var line = lines[insertion].TrimStart();
            if (line.Length == 0 || line.StartsWith("using ", StringComparison.Ordinal) || line.StartsWith("extern alias ", StringComparison.Ordinal) || line.StartsWith('#')) insertion++;
            else break;
        }
        lines.Insert(insertion, $"namespace {ns};");
        lines.Insert(insertion + 1, "");
        return string.Join(Environment.NewLine, lines);
    }

    private SearchResult Result(EntityHandle h, string name, string kind)
    {
        var symbol = new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(h));
        var declaringType = DeclaringTypeOf(h);
        var qualifiedType = QualifiedTypeName(declaringType);
        var qualifiedName = h.Kind == HandleKind.TypeDefinition ? qualifiedType : $"{qualifiedType}.{name}";
        var outer = declaringType;
        while (!metadata.GetTypeDefinition(outer).GetDeclaringType().IsNil) outer = metadata.GetTypeDefinition(outer).GetDeclaringType();
        var ns = metadata.GetString(metadata.GetTypeDefinition(outer).Namespace);
        return new(symbol, name, kind, Descriptor.Name, ns, new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(declaringType)), qualifiedName);
    }

    private string QualifiedTypeName(TypeDefinitionHandle handle)
    {
        var names = new Stack<string>();
        var current = handle;
        while (!current.IsNil)
        {
            var type = metadata.GetTypeDefinition(current);
            names.Push(TypeDisplayName(type));
            current = type.GetDeclaringType();
        }
        var outer = metadata.GetTypeDefinition(handle);
        while (!outer.GetDeclaringType().IsNil) outer = metadata.GetTypeDefinition(outer.GetDeclaringType());
        var ns = metadata.GetString(outer.Namespace);
        var typeName = string.Join('.', names);
        return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
    }
    private string GetEntityName(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => TypeDisplayName(metadata.GetTypeDefinition((TypeDefinitionHandle)h)),
        HandleKind.MethodDefinition => metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)h).Name),
        HandleKind.FieldDefinition => metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)h).Name),
        HandleKind.PropertyDefinition => metadata.GetString(metadata.GetPropertyDefinition((PropertyDefinitionHandle)h).Name),
        HandleKind.EventDefinition => metadata.GetString(metadata.GetEventDefinition((EventDefinitionHandle)h).Name),
        _ => $"0x{MetadataTokens.GetToken(h):X8}"
    };

    private string TypeDisplayName(TypeDefinition type)
    {
        var metadataName = metadata.GetString(type.Name);
        var separator = metadataName.LastIndexOf('`');
        if (separator <= 0 || !int.TryParse(metadataName.AsSpan(separator + 1), out var arity) || arity <= 0) return metadataName;

        var parameters = type.GetGenericParameters()
            .Select(handle => metadata.GetGenericParameter(handle))
            .OrderBy(parameter => parameter.Index)
            .TakeLast(arity)
            .Select((parameter, index) =>
            {
                var name = metadata.GetString(parameter.Name);
                return string.IsNullOrEmpty(name) ? arity == 1 ? "T" : $"T{index + 1}" : name;
            })
            .ToList();
        while (parameters.Count < arity) parameters.Add(arity == 1 ? "T" : $"T{parameters.Count + 1}");
        return $"{metadataName[..separator]}<{string.Join(", ", parameters)}>";
    }

    private string TypeIdentifier(TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var separator = name.LastIndexOf('`');
        return separator > 0 && int.TryParse(name.AsSpan(separator + 1), out _) ? name[..separator] : name;
    }
    private static int ParseToken(NodeId node) => Convert.ToInt32(node.Value[(node.Value.IndexOf(':') + 1)..], 16);
    public void Dispose() { gate.Dispose(); module.Dispose(); }
}

internal sealed class MetadataTypeNameProvider(MetadataReader metadata) : ISignatureTypeProvider<string, object?>
{
    private sealed record GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType.Split('`')[0] + "<" + string.Join(", ", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => genericContext is GenericContext context && index >= 0 && index < context.MethodParameters.Length ? context.MethodParameters[index] : $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => genericContext is GenericContext context && index >= 0 && index < context.TypeParameters.Length ? context.TypeParameters[index] : $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch { PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.Char => "char", PrimitiveTypeCode.Double => "double", PrimitiveTypeCode.Int16 => "short", PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.Object => "object", PrimitiveTypeCode.SByte => "sbyte", PrimitiveTypeCode.Single => "float", PrimitiveTypeCode.String => "string", PrimitiveTypeCode.UInt16 => "ushort", PrimitiveTypeCode.UInt32 => "uint", PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Void => "void", _ => typeCode.ToString() };
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    public string GetTypeName(EntityHandle handle, object? genericContext = null) => handle.Kind switch { HandleKind.TypeDefinition => GetTypeFromDefinition(metadata, (TypeDefinitionHandle)handle, 0), HandleKind.TypeReference => GetTypeFromReference(metadata, (TypeReferenceHandle)handle, 0), HandleKind.TypeSpecification => GetTypeFromSpecification(metadata, genericContext, (TypeSpecificationHandle)handle, 0), _ => "object" };

    public object CreateContext(TypeDefinition type, MethodDefinition? method = null) => new GenericContext(
        ParameterNames(type.GetGenericParameters(), "!"),
        method is { } definition ? ParameterNames(definition.GetGenericParameters(), "!!") : []);

    private ImmutableArray<string> ParameterNames(GenericParameterHandleCollection handles, string fallbackPrefix)
    {
        if (handles.Count == 0) return [];
        var parameters = handles.Select(handle => metadata.GetGenericParameter(handle)).ToArray();
        var names = new string[parameters.Max(parameter => parameter.Index) + 1];
        foreach (var parameter in parameters)
        {
            var name = metadata.GetString(parameter.Name);
            names[parameter.Index] = string.IsNullOrEmpty(name) ? $"{fallbackPrefix}{parameter.Index}" : name;
        }
        for (var index = 0; index < names.Length; index++) names[index] ??= $"{fallbackPrefix}{index}";
        return [.. names];
    }
}
