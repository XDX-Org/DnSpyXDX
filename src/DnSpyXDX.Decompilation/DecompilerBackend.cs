using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Resources;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using DnSpyXDX.Application;

namespace DnSpyXDX.Decompilation;

public sealed class DecompilerBackend : IDecompilerBackend
{
    private readonly ConcurrentDictionary<Guid, AssemblySession> sessions = new();
    private readonly RuntimeDisplaySettings displaySettings;

    public DecompilerBackend() : this(new RuntimeDisplaySettings()) { }
    public DecompilerBackend(RuntimeDisplaySettings displaySettings) => this.displaySettings = displaySettings;
    public IReadOnlyList<AssemblyDescriptor> Assemblies => sessions.Values.Select(s => s.Descriptor).OrderBy(s => s.Name).ToArray();

    public Task<AssemblyDescriptor> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Assembly not found.", fullPath);
            var session = AssemblySession.Open(fullPath, displaySettings);
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

    public async Task<AssemblyDescriptor> OpenAssemblyForSymbolAsync(SymbolId symbol, CancellationToken cancellationToken = default)
    {
        var loaded = sessions.Values.FirstOrDefault(session => session.Descriptor.ModuleMvid == symbol.ModuleMvid);
        if (loaded is not null) return loaded.Descriptor;
        var paths = sessions.Values.Select(session => Path.GetDirectoryName(session.Descriptor.Path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadModuleMvid(path) == symbol.ModuleMvid) return await OpenAsync(path, cancellationToken);
        }
        throw new FileNotFoundException($"Could not find the assembly containing token 0x{symbol.MetadataToken:X8} beside an open assembly.");
    }

    private static Guid? TryReadModuleMvid(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata) return null;
            var metadata = reader.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
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

    public Task<ResourceDocument> GetResourceAsync(NodeId resource, CancellationToken cancellationToken = default) =>
        sessions.TryGetValue(resource.SessionId, out var session)
            ? Task.Run(() => session.GetResource(resource, cancellationToken), cancellationToken)
            : throw new KeyNotFoundException("The resource's assembly is no longer open.");

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
    private readonly DecompilerSettings settings;
    private readonly MetadataTypeNameProvider typeNames;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RuntimeDisplaySettings displaySettings;
    private readonly Dictionary<(int Token, DecompilerLanguage Language, bool ShowMetadataTokens), DecompilerDocument> cache = [];
    private byte[]? image;
    private IReadOnlyList<BinaryRegion>? binaryRegions;
    private IReadOnlyDictionary<string, SymbolId>? typeLinks;
    private IReadOnlyDictionary<string, string>? typeClassifications;
    public AssemblyDescriptor Descriptor { get; }

    private AssemblySession(PEFile module, CSharpDecompiler decompiler, DecompilerSettings settings, AssemblyDescriptor descriptor, RuntimeDisplaySettings displaySettings)
    {
        this.module = module;
        metadata = module.Metadata;
        this.decompiler = decompiler;
        this.settings = settings;
        this.displaySettings = displaySettings;
        typeNames = new MetadataTypeNameProvider(metadata);
        Descriptor = descriptor;
    }

    public static AssemblySession Open(string path, RuntimeDisplaySettings displaySettings)
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
        settings.CSharpFormattingOptions.IndentationString = "\t";
        settings.CSharpFormattingOptions.IndentSwitchBody = true;
        var decompiler = new CSharpDecompiler(module, resolver, settings);
        var sessionId = Guid.NewGuid();
        var descriptor = new AssemblyDescriptor(sessionId, mvid, name, path, module.DetectTargetFrameworkId() ?? "Unknown", module.Reader.PEHeaders.CoffHeader.Machine.ToString(), new NodeId(sessionId, "root"));
        return new AssemblySession(module, decompiler, settings, descriptor, displaySettings);
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
            var name = metadata.GetString(r.Name);
            var tooltip = IsLikelyObfuscatedResourceName(name)
                ? "This short, extensionless mixed-case name may have been generated by an obfuscator. Open it to inspect the content."
                : null;
            return new TreeNodeDescriptor(new NodeId(Descriptor.SessionId, $"res:{MetadataTokens.GetToken(h)}"), name, TreeNodeKind.Resource, false, Tooltip: tooltip);
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

    private static bool IsLikelyObfuscatedResourceName(string name) =>
        name.Length is >= 3 and <= 12 &&
        Path.GetExtension(name).Length == 0 &&
        name.All(char.IsLetterOrDigit) &&
        name.Any(char.IsLower) &&
        name.Any(char.IsUpper);

    public ResourceDocument GetResource(NodeId resource, CancellationToken ct)
    {
        if (!resource.Value.StartsWith("res:", StringComparison.Ordinal) ||
            !int.TryParse(resource.Value.AsSpan(4), out var token)) throw new ArgumentException("The node is not a manifest resource.", nameof(resource));
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.ManifestResource) throw new ArgumentException("The node is not a manifest resource.", nameof(resource));
        var definition = metadata.GetManifestResource((ManifestResourceHandle)handle);
        var name = metadata.GetString(definition.Name);
        if (!definition.Implementation.IsNil) throw new NotSupportedException($"Resource '{name}' is stored in an external assembly or file.");
        var directory = module.Reader.PEHeaders.CorHeader?.ResourcesDirectory
            ?? throw new BadImageFormatException("The assembly has no managed resource directory.");
        var block = module.Reader.GetSectionData(directory.RelativeVirtualAddress + checked((int)definition.Offset));
        if (block.Length < 4) throw new BadImageFormatException($"Resource '{name}' has an invalid data header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(block.GetContent(0, 4).AsSpan());
        if (length < 0 || length > block.Length - 4) throw new BadImageFormatException($"Resource '{name}' has an invalid data length.");
        ct.ThrowIfCancellationRequested();
        var data = block.GetContent(4, length).ToArray();
        var symbol = new SymbolId(Descriptor.ModuleMvid, token);
        if (TryReadResources(data, ct, out var listing)) return new ResourceDocument(resource, symbol, name, data, ".resources container", listing);
        if (TryImageMimeType(data) is { } mime) return new ResourceDocument(resource, symbol, name, data, "Image", MimeType: mime);
        if (TryReadText(data) is { } text) return new ResourceDocument(resource, symbol, name, data, TextKind(text), text);
        return new ResourceDocument(resource, symbol, name, data, "Binary data");
    }

    private static bool TryReadResources(byte[] data, CancellationToken ct, out string? listing)
    {
        listing = null;
        try
        {
            using var reader = new ResourceReader(new MemoryStream(data, writable: false));
            var entries = new List<string>();
            var enumerator = reader.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var key = (string)enumerator.Key;
                reader.GetResourceData(key, out var type, out var value);
                entries.Add($"{key} = {FormatResourceValue(type, value)}");
            }
            listing = string.Join(Environment.NewLine, entries.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private static string FormatResourceValue(string type, byte[] value)
    {
        if (type == "ResourceTypeCode.String")
        {
            try { using var reader = new BinaryReader(new MemoryStream(value, writable: false), Encoding.UTF8); return $"\"{reader.ReadString()}\""; }
            catch (Exception ex) when (ex is EndOfStreamException or IOException) { }
        }
        if (type is "ResourceTypeCode.ByteArray" or "ResourceTypeCode.Stream")
        {
            var length = value.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(value) : value.Length;
            return $"{type[17..]}[{Math.Max(0, length)}]";
        }
        return $"{type} [{value.Length} encoded bytes]";
    }

    private static string? TryReadText(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            string text;
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF');
            return text.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t') ? text : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static string TextKind(string text) => text.TrimStart() switch
    {
        var value when value.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || value.StartsWith('<') => "XML text",
        var value when value.StartsWith('{') || value.StartsWith('[') => "JSON text",
        _ => "Text"
    };

    private static string? TryImageMimeType(byte[] data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x52, 0x49, 0x46, 0x46, ..] when data.Length >= 12 && data.AsSpan(8, 4).SequenceEqual("WEBP"u8) => "image/webp",
        _ => null
    };

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
        var showMetadataTokens = displaySettings.ShowMetadataTokens;
        var key = (symbol.MetadataToken, language, showMetadataTokens);
        if (cache.TryGetValue(key, out var cached)) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached;
            var handle = MetadataTokens.EntityHandle(symbol.MetadataToken);
            decompiler.CancellationToken = ct;
            string text;
            IReadOnlyList<ClassifiedSpan>? semanticSpans = null;
            IReadOnlyList<ReferenceSpan> csharpReferences = [];
            if (language == DecompilerLanguage.CSharp)
                (text, semanticSpans, csharpReferences) = await Task.Run(() => DecompileCSharp(handle, showMetadataTokens), ct);
            else
                text = await Task.Run(() => language switch
                {
                    DecompilerLanguage.IL => Disassemble(handle, ct, showMetadataTokens),
                    DecompilerLanguage.ILWithCSharp => DisassembleWithCSharp(handle, ct, showMetadataTokens),
                    DecompilerLanguage.Hex => "",
                    _ => throw new ArgumentOutOfRangeException(nameof(language))
                }, ct);
            ct.ThrowIfCancellationRequested();
            var title = GetEntityName(handle);
            var links = language == DecompilerLanguage.CSharp ? BuildSymbolLinks(handle) : null;
            var symbolLocations = language == DecompilerLanguage.CSharp ? BuildSymbolLocations(text, handle) : null;
            var references = language is DecompilerLanguage.IL or DecompilerLanguage.ILWithCSharp ? BuildILReferences(text) : csharpReferences;
            var binary = language == DecompilerLanguage.Hex ? image ??= module.Reader.GetEntireImage().GetContent().ToArray() : null;
            var selection = language == DecompilerLanguage.Hex ? GetHexEntityRegion(handle) : null;
            var baseRegions = language == DecompilerLanguage.Hex ? binaryRegions ??= BuildHexRegions() : null;
            var regions = baseRegions;
            // C# colors come from the syntax-tree spans, so the assembly-wide name map (which walks every
            // referenced assembly) is only built for the IL view that still relies on lexical classification.
            var classifications = language == DecompilerLanguage.CSharp ? null : BuildClassifications(handle);
            var result = new DecompilerDocument(symbol, title, language.Key(), text, references, [], links, TypeClassifications: classifications, Binary: binary,
                BinarySelectionOffset: selection?.Offset, BinarySelectionLength: selection?.Length ?? 0, BinaryRegions: regions, SymbolLocations: symbolLocations, SemanticSpans: semanticSpans);
            cache[key] = result;
            return result;
        }
        finally { decompiler.CancellationToken = default; gate.Release(); }
    }

    private (string Text, IReadOnlyList<ClassifiedSpan> Spans, IReadOnlyList<ReferenceSpan> References) DecompileCSharp(EntityHandle handle, bool showMetadataTokens)
    {
        // Decompile to a syntax tree and paint each token from its bound symbol (dnSpy's approach) rather
        // than lexically. The namespace header and dnSpy-style token comments are then folded back in while
        // keeping the classification spans and navigable references aligned to the text.
        var tree = decompiler.Decompile([handle]);
        var (text, spans, references) = SemanticHighlighter.Highlight(tree, settings.CSharpFormattingOptions);
        var lines = SplitIntoClassifiedLines(text, spans, references);
        InsertNamespaceLine(lines, DeclaringTypeOf(handle));
        if (showMetadataTokens) InsertTokenCommentLines(lines, handle);
        return FlattenClassifiedLines(lines);
    }

    private sealed class ClassifiedLine(string text)
    {
        public string Text { get; } = text;
        // Spans and references are stored relative to the start of the line so inserting whole lines never
        // disturbs them; they are rebased to absolute offsets when the lines are flattened back to text.
        public List<ClassifiedSpan> Spans { get; } = [];
        public List<ReferenceSpan> References { get; } = [];
    }

    private static List<ClassifiedLine> SplitIntoClassifiedLines(string text, IReadOnlyList<ClassifiedSpan> spans, IReadOnlyList<ReferenceSpan> references)
    {
        var lines = new List<ClassifiedLine>();
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++) if (text[index] == '\n') starts.Add(index + 1);
        for (var line = 0; line < starts.Count; line++)
        {
            var start = starts[line];
            var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            lines.Add(new ClassifiedLine(text[start..end]));
        }
        foreach (var span in spans)
        {
            // A span may straddle line breaks (a verbatim string); clip it onto each line it covers.
            var remaining = span;
            var line = LineOf(starts, remaining.Start);
            while (remaining.Length > 0 && line < lines.Count)
            {
                var lineStart = starts[line];
                var column = remaining.Start - lineStart;
                var take = Math.Min(remaining.Length, lines[line].Text.Length - column);
                if (take > 0) lines[line].Spans.Add(new ClassifiedSpan(column, take, remaining.Kind));
                var consumed = lines[line].Text.Length - column + 1;
                remaining = new ClassifiedSpan(remaining.Start + consumed, remaining.Length - consumed, remaining.Kind);
                line++;
            }
        }
        foreach (var reference in references)
        {
            // A reference always covers a single identifier, so it lives on exactly one line.
            var line = LineOf(starts, reference.StartOffset);
            if (line < 0 || line >= lines.Count) continue;
            var column = reference.StartOffset - starts[line];
            if (column < 0 || column + reference.Length > lines[line].Text.Length) continue;
            lines[line].References.Add(reference with { StartOffset = column });
        }
        return lines;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var line = lineStarts.BinarySearch(offset);
        return line >= 0 ? line : ~line - 1;
    }

    private static (string Text, IReadOnlyList<ClassifiedSpan> Spans, IReadOnlyList<ReferenceSpan> References) FlattenClassifiedLines(List<ClassifiedLine> lines)
    {
        var builder = new StringBuilder();
        var spans = new List<ClassifiedSpan>();
        var references = new List<ReferenceSpan>();
        for (var index = 0; index < lines.Count; index++)
        {
            var offset = builder.Length;
            builder.Append(lines[index].Text);
            foreach (var span in lines[index].Spans) spans.Add(new ClassifiedSpan(offset + span.Start, span.Length, span.Kind));
            foreach (var reference in lines[index].References) references.Add(reference with { StartOffset = offset + reference.StartOffset });
            if (index + 1 < lines.Count) builder.Append('\n');
        }
        return (builder.ToString(), spans, references);
    }

    private void InsertNamespaceLine(List<ClassifiedLine> lines, TypeDefinitionHandle typeHandle)
    {
        if (typeHandle.IsNil) return;
        var outer = typeHandle;
        while (!metadata.GetTypeDefinition(outer).GetDeclaringType().IsNil) outer = metadata.GetTypeDefinition(outer).GetDeclaringType();
        var ns = metadata.GetString(metadata.GetTypeDefinition(outer).Namespace);
        if (string.IsNullOrEmpty(ns)) return;
        if (lines.Any(line => line.Text.Contains($"namespace {ns}", StringComparison.Ordinal))) return;

        var insertion = 0;
        while (insertion < lines.Count)
        {
            var text = lines[insertion].Text.TrimStart();
            if (text.Length == 0 || text.StartsWith("using ", StringComparison.Ordinal) || text.StartsWith("extern alias ", StringComparison.Ordinal) || text.StartsWith('#')) insertion++;
            else break;
        }
        var declaration = new ClassifiedLine($"namespace {ns};");
        declaration.Spans.Add(new ClassifiedSpan(0, "namespace".Length, "keyword"));
        declaration.Spans.Add(new ClassifiedSpan("namespace ".Length, ns.Length, "namespace"));
        lines.Insert(insertion, declaration);
        lines.Insert(insertion + 1, new ClassifiedLine(""));
    }

    private void InsertTokenCommentLines(List<ClassifiedLine> lines, EntityHandle selected)
    {
        if (selected.Kind != HandleKind.TypeDefinition)
        {
            lines.Insert(0, CommentLine(TokenComment(selected)));
            return;
        }
        var type = metadata.GetTypeDefinition((TypeDefinitionHandle)selected);
        var declarations = new List<(EntityHandle Handle, string Name, bool Callable)> { (selected, TypeIdentifier(type), false) };
        declarations.AddRange(type.GetFields().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetFieldDefinition(h).Name), false)));
        declarations.AddRange(type.GetProperties().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetPropertyDefinition(h).Name), false)));
        declarations.AddRange(type.GetEvents().Select(h => ((EntityHandle)h, metadata.GetString(metadata.GetEventDefinition(h).Name), false)));
        declarations.AddRange(type.GetMethods().Where(h => !metadata.GetMethodDefinition(h).Attributes.HasFlag(MethodAttributes.SpecialName) || metadata.GetString(metadata.GetMethodDefinition(h).Name) is ".ctor" or ".cctor").Select(h =>
        {
            var name = metadata.GetString(metadata.GetMethodDefinition(h).Name);
            return ((EntityHandle)h, name is ".ctor" or ".cctor" ? TypeIdentifier(type) : name, true);
        }));

        var insertions = new List<(int Index, ClassifiedLine Line)>();
        var used = new HashSet<int>();
        foreach (var declaration in declarations)
        {
            var candidates = Enumerable.Range(0, lines.Count)
                .Where(i => !used.Contains(i) && IsDeclarationLine(lines[i].Text, declaration.Name, declaration.Callable))
                .ToArray();
            if (candidates.Length == 0) continue;
            var declarationIndent = candidates.Min(i => LeadingWhitespace(lines[i].Text));
            foreach (var i in candidates.Where(i => LeadingWhitespace(lines[i].Text) == declarationIndent))
            {
                used.Add(i);
                var indent = lines[i].Text[..(lines[i].Text.Length - lines[i].Text.TrimStart().Length)];
                insertions.Add((i, CommentLine(indent + TokenComment(declaration.Handle))));
                break;
            }
        }
        foreach (var insertion in insertions.OrderByDescending(x => x.Index)) lines.Insert(insertion.Index, insertion.Line);
    }

    private static ClassifiedLine CommentLine(string text)
    {
        var line = new ClassifiedLine(text);
        line.Spans.Add(new ClassifiedSpan(0, text.Length, "comment"));
        return line;
    }

    private BinaryRegion? GetHexEntityRegion(EntityHandle handle)
    {
        var table = handle.Kind switch
        {
            HandleKind.TypeDefinition => TableIndex.TypeDef,
            HandleKind.MethodDefinition => TableIndex.MethodDef,
            HandleKind.FieldDefinition => TableIndex.Field,
            HandleKind.PropertyDefinition => TableIndex.Property,
            HandleKind.EventDefinition => TableIndex.Event,
            _ => (TableIndex?)null
        };
        if (table is null || module.Reader.PEHeaders.CorHeader is not { } corHeader ||
            !module.Reader.PEHeaders.TryGetDirectoryOffset(corHeader.MetadataDirectory, out var metadataOffset)) return null;
        var row = MetadataTokens.GetRowNumber(handle);
        var rowSize = metadata.GetTableRowSize(table.Value);
        var offset = metadataOffset + metadata.GetTableMetadataOffset(table.Value) + (row - 1) * rowSize;
        return offset >= 0 && offset + rowSize <= module.Reader.GetEntireImage().Length
            ? new BinaryRegion(offset, rowSize, $"{table} row {row}: {GetEntityName(handle)} (token 0x{MetadataTokens.GetToken(handle):X8})", IsEntity: true)
            : null;
    }

    private IReadOnlyList<BinaryRegion> BuildHexRegions()
    {
        var headers = module.Reader.PEHeaders;
        var regions = new List<BinaryRegion>();
        AddRegion(regions, 0, Math.Min(64, headers.PEHeaderStartOffset), "DOS header");
        AddRegion(regions, 64, headers.PEHeaderStartOffset - 64, "DOS stub");
        AddRegion(regions, headers.PEHeaderStartOffset, 4, "PE signature");
        AddRegion(regions, headers.PEHeaderStartOffset + 4, 20, "COFF file header");
        AddRegion(regions, headers.PEHeaderStartOffset + 24, headers.CoffHeader.SizeOfOptionalHeader, "PE optional header");
        var sectionHeaderOffset = headers.PEHeaderStartOffset + 24 + headers.CoffHeader.SizeOfOptionalHeader;
        for (var index = 0; index < headers.SectionHeaders.Length; index++)
        {
            var section = headers.SectionHeaders[index];
            AddRegion(regions, sectionHeaderOffset + index * 40, 40, $"{section.Name} section header");
            AddRegion(regions, section.PointerToRawData, section.SizeOfRawData, $"{section.Name} section data");
        }
        if (headers.PEHeader is { } peHeader && headers.TryGetDirectoryOffset(peHeader.CorHeaderTableDirectory, out var clrOffset))
            AddRegion(regions, clrOffset, peHeader.CorHeaderTableDirectory.Size, "CLR header");
        if (headers.CorHeader is { } corHeader && headers.TryGetDirectoryOffset(corHeader.MetadataDirectory, out var metadataOffset))
        {
            AddRegion(regions, metadataOffset, corHeader.MetadataDirectory.Size, ".NET metadata");
            foreach (var heap in Enum.GetValues<HeapIndex>())
            {
                var size = metadata.GetHeapSize(heap);
                if (size > 0)
                {
                    var heapOffset = metadataOffset + metadata.GetHeapMetadataOffset(heap);
                    AddRegion(regions, heapOffset, size, $"#{heap} metadata heap");
                    AddHeapEntries(regions, heap, heapOffset, size);
                }
            }
            var firstTableOffset = Enum.GetValues<TableIndex>()
                .Where(table => metadata.GetTableRowCount(table) > 0)
                .Select(metadata.GetTableMetadataOffset)
                .DefaultIfEmpty(0)
                .Min();
            if (firstTableOffset > 0) AddRegion(regions, metadataOffset, firstTableOffset, ".NET metadata root, stream headers, and tables header");
            foreach (var table in Enum.GetValues<TableIndex>())
            {
            var rows = metadata.GetTableRowCount(table);
            var rowSize = metadata.GetTableRowSize(table);
            if (rows == 0 || rowSize == 0) continue;
            for (var row = 1; row <= rows; row++)
            {
                var offset = metadataOffset + metadata.GetTableMetadataOffset(table) + (row - 1) * rowSize;
                var token = ((int)table << 24) | row;
                var name = table switch
                {
                    TableIndex.TypeDef => metadata.GetString(metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row)).Name),
                    TableIndex.MethodDef => metadata.GetString(metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row)).Name),
                    TableIndex.Field => metadata.GetString(metadata.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row)).Name),
                    TableIndex.Property => metadata.GetString(metadata.GetPropertyDefinition(MetadataTokens.PropertyDefinitionHandle(row)).Name),
                    TableIndex.Event => metadata.GetString(metadata.GetEventDefinition(MetadataTokens.EventDefinitionHandle(row)).Name),
                    _ => null
                };
                regions.Add(new BinaryRegion(offset, rowSize, $"{table} row {row}{(name is null ? "" : $": {name}")} (token 0x{token:X8}, {rowSize} bytes)", IsEntity: true));
            }
            }
        }
        return regions;
    }

    private void AddHeapEntries(List<BinaryRegion> regions, HeapIndex heap, int offset, int size)
    {
        var bytes = image!;
        var end = Math.Min(bytes.Length, offset + size);
        if (heap == HeapIndex.Guid)
        {
            for (var position = offset; position + 16 <= end; position += 16)
                regions.Add(new BinaryRegion(position, 16, $"#GUID {(position - offset) / 16 + 1}: {new Guid(bytes.AsSpan(position, 16))}", IsEntity: true));
            return;
        }
        for (var position = offset + 1; position < end;)
        {
            if (heap == HeapIndex.String)
            {
                var terminator = Array.IndexOf(bytes, (byte)0, position, end - position);
                if (terminator < 0) terminator = end;
                var length = terminator - position;
                var value = Encoding.UTF8.GetString(bytes, position, length);
                regions.Add(new BinaryRegion(position, Math.Min(length + 1, end - position), $"#Strings 0x{position - offset:X}: \"{Preview(value)}\"", IsEntity: true));
                position = terminator + 1;
                continue;
            }
            if (!TryReadCompressedInteger(bytes, position, end, out var payloadLength, out var prefixLength)) break;
            var totalLength = Math.Min(prefixLength + payloadLength, end - position);
            var tooltip = heap == HeapIndex.UserString
                ? $"#US 0x{position - offset:X}: \"{Preview(Encoding.Unicode.GetString(bytes, position + prefixLength, Math.Max(0, totalLength - prefixLength - 1) & ~1))}\""
                : $"#Blob 0x{position - offset:X}: {payloadLength} data bytes";
            regions.Add(new BinaryRegion(position, totalLength, tooltip, IsEntity: true));
            position += Math.Max(1, totalLength);
        }
    }

    private static bool TryReadCompressedInteger(byte[] bytes, int offset, int end, out int value, out int length)
    {
        value = 0;
        length = 0;
        if (offset >= end) return false;
        var first = bytes[offset];
        if ((first & 0x80) == 0) { value = first; length = 1; return true; }
        if ((first & 0xC0) == 0x80 && offset + 1 < end) { value = ((first & 0x3F) << 8) | bytes[offset + 1]; length = 2; return true; }
        if ((first & 0xE0) == 0xC0 && offset + 3 < end)
        {
            value = ((first & 0x1F) << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
            length = 4;
            return true;
        }
        return false;
    }

    private static string Preview(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return cleaned.Length <= 80 ? cleaned : cleaned[..77] + "…";
    }

    private void AddRegion(List<BinaryRegion> regions, int offset, int length, string tooltip)
    {
        var imageLength = module.Reader.GetEntireImage().Length;
        if (offset >= 0 && length > 0 && offset < imageLength)
            regions.Add(new BinaryRegion(offset, Math.Min(length, imageLength - offset), tooltip));
    }

    private string Disassemble(EntityHandle handle, CancellationToken ct, bool showMetadataTokens, bool formatDeclarationTokens = true)
    {
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, ct) { DetectControlStructure = true, ShowMetadataTokens = showMetadataTokens };
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
        return showMetadataTokens && formatDeclarationTokens ? FormatMetadataTokens(text) : text;
    }

    private string DisassembleWithCSharp(EntityHandle handle, CancellationToken ct, bool showMetadataTokens)
    {
        var syntaxTree = decompiler.Decompile([handle]);
        using var writer = new StringWriter();
        var tokenWriter = TokenWriter.CreateWriterThatSetsLocationsInAST(writer, "");
        syntaxTree.AcceptVisitor(new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));
        var csharp = writer.ToString();
        var sequencePoints = decompiler.CreateSequencePoints(syntaxTree)
            .Where(pair => pair.Key.Method?.MetadataToken.Kind == HandleKind.MethodDefinition)
            .GroupBy(pair => MetadataTokens.GetToken(pair.Key.Method!.MetadataToken))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(pair => pair.Value).Where(point => !point.IsHidden).OrderBy(point => point.Offset).ToArray());
        // Method tokens are required to associate sequence points with IL methods even when
        // their presentation is disabled.
        var il = Disassemble(handle, ct, showMetadataTokens: true, formatDeclarationTokens: false);
        var sourceLines = csharp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(il.Length + csharp.Length / 3);
        IReadOnlyList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> points = [];
        string? previousAnnotation = null;
        foreach (var line in il.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var method = MethodToken.Match(line);
            if (method.Success)
            {
                points = sequencePoints.GetValueOrDefault(Convert.ToInt32(method.Groups[1].Value, 16)) ?? [];
                previousAnnotation = null;
            }
            var instruction = InstructionOffset.Match(line);
            if (instruction.Success && points.Count > 0)
            {
                var offset = Convert.ToInt32(instruction.Groups[1].Value, 16);
                var point = points.FirstOrDefault(candidate => candidate.Offset <= offset && offset < candidate.EndOffset);
                if (point is not null)
                {
                    var annotation = SourceText(sourceLines, point);
                    if (annotation.Length > 0 && annotation != previousAnnotation)
                        output.Append(line.AsSpan(0, line.Length - line.TrimStart().Length)).Append("// C#: ").AppendLine(annotation);
                    previousAnnotation = annotation;
                }
                else previousAnnotation = null;
            }
            output.AppendLine(line);
        }
        var text = output.ToString();
        return showMetadataTokens ? FormatMetadataTokens(text) : InlineMetadataToken.Replace(text, "");
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
        var parts = new List<string>(end - start + 1);
        for (var line = start; line <= end; line++)
        {
            var from = line == start ? Math.Clamp(point.StartColumn - 1, 0, lines[line].Length) : 0;
            var to = line == end ? Math.Clamp(point.EndColumn - 1, from, lines[line].Length) : lines[line].Length;
            var part = lines[line][from..to].Trim();
            if (part.Length > 0) parts.Add(part);
        }
        var text = string.Join(' ', parts);
        return Regex.Replace(text, "\\s+", " ");
    }

    private static readonly Regex MethodToken = new(@"\.method\s+/\*\s*([0-9A-Fa-f]{8})\s*\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InstructionOffset = new(@"\bIL_([0-9A-Fa-f]+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MetadataTokenLine = new(@"(?m)^([ \t]*)(.*?/\*\s*[0-9A-Fa-f]{8}\s*\*/.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InlineMetadataToken = new(@"\s*/\*\s*([0-9A-Fa-f]{8})\s*\*/", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenCommentLine = new(@"^\s*//\s*Token:\s*0x([0-9A-Fa-f]{8})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ILLabel = new(@"\bIL_[0-9A-Fa-f]+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyList<ReferenceSpan> BuildILReferences(string text)
    {
        var lines = SourceLines(text);
        var method = -1;
        var definitions = new Dictionary<(int Method, string Label), int>();
        var methodByLine = new int[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            var content = lines[index].Text;
            if (content.TrimStart().StartsWith(".method ", StringComparison.Ordinal)) method++;
            methodByLine[index] = method;
            var label = ILLabel.Match(content);
            if (method >= 0 && label.Success && content.AsSpan(label.Index + label.Length).TrimStart().StartsWith(":"))
                definitions[(method, label.Value)] = lines[index].Offset + label.Index;
        }

        var references = new List<ReferenceSpan>();
        for (var index = 0; index < lines.Count; index++)
        {
            var content = lines[index].Text;
            foreach (Match label in ILLabel.Matches(content))
            {
                var isDefinition = content.AsSpan(label.Index + label.Length).TrimStart().StartsWith(":");
                if (!isDefinition && definitions.TryGetValue((methodByLine[index], label.Value), out var target))
                    references.Add(new ReferenceSpan(lines[index].Offset + label.Index, label.Length, null, null, $"Go to {label.Value}", target));
            }

            var tokenMatch = TokenCommentLine.Match(content);
            if (!tokenMatch.Success || !int.TryParse(tokenMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var token)) continue;
            if (!TryResolveLocalILTarget(token, out var targetSymbol, out var name)) continue;
            var targetLine = index + 1;
            while (targetLine < lines.Count && TokenCommentLine.IsMatch(lines[targetLine].Text)) targetLine++;
            if (targetLine >= lines.Count) continue;
            var nameIndex = lines[targetLine].Text.IndexOf(name, StringComparison.Ordinal);
            if (nameIndex >= 0)
                references.Add(new ReferenceSpan(lines[targetLine].Offset + nameIndex, name.Length, targetSymbol, null, $"Go to {name}"));
        }
        return references;
    }

    private bool TryResolveLocalILTarget(int token, out SymbolId symbol, out string name)
    {
        symbol = default;
        name = "";
        EntityHandle handle;
        try { handle = MetadataTokens.EntityHandle(token); }
        catch (ArgumentException) { return false; }
        if (handle.Kind == HandleKind.MethodSpecification) handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        if (handle.Kind is not (HandleKind.TypeDefinition or HandleKind.MethodDefinition or HandleKind.FieldDefinition or HandleKind.PropertyDefinition or HandleKind.EventDefinition))
        {
            var entity = ((ICSharpCode.Decompiler.TypeSystem.MetadataModule)decompiler.TypeSystem.MainModule).ResolveEntity(handle, default);
            if (entity is null || entity.ParentModule?.IsMainModule != true || entity.MetadataToken.IsNil) return false;
            handle = entity.MetadataToken;
        }
        symbol = new SymbolId(Descriptor.ModuleMvid, MetadataTokens.GetToken(handle));
        name = handle.Kind == HandleKind.TypeDefinition
            ? metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name)
            : GetEntityName(handle);
        return name.Length > 0;
    }

    private static List<(int Offset, string Text)> SourceLines(string text)
    {
        var lines = new List<(int, string)>();
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            lines.Add((offset, content));
            offset += line.Length + 1;
        }
        return lines;
    }

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

    // Records each type's declared kind (class/interface/enum/struct/delegate) keyed by the simple name
    // that appears in decompiled source, so the viewer can give enums, interfaces, structs, delegates and
    // static classes their own dnSpy-style colors. This walks the decompiler's whole type system - the
    // module plus every referenced assembly - so framework types such as IDisposable, Action and
    // KeyValuePair are colored by their real kind, exactly as dnSpy resolves them. A name whose kind is
    // not consistent across the types that share it is dropped so a color never misrepresents it.
    private IReadOnlyDictionary<string, string> BuildTypeClassifications()
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in decompiler.TypeSystem.GetAllTypeDefinitions())
        {
            var name = type.Name;
            if (string.IsNullOrEmpty(name) || name.StartsWith('<')) continue;
            if (ClassifyKind(type) is not { } kind) continue;
            if (byName.TryGetValue(name, out var existing)) { if (existing != kind) conflicting.Add(name); }
            else byName[name] = kind;
        }
        foreach (var name in conflicting) byName.Remove(name);
        return byName;
    }

    private static string? ClassifyKind(ITypeDefinition type) => type.Kind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        TypeKind.Struct => "struct",
        TypeKind.Class => type.IsStatic ? "staticclass" : "class",
        _ => null
    };

    // Layers the members declared by the type being shown - and by its nested types, since the decompiled
    // document contains them too - over the assembly-wide type kinds, so the viewer can color a name by
    // what it actually is. Members shadow a same-named type, mirroring how BuildSymbolLinks resolves the
    // click target. The shared type map is referenced rather than copied so opening a document over a large
    // reference set stays cheap.
    private IReadOnlyDictionary<string, string> BuildClassifications(EntityHandle selected)
    {
        var typeHandle = DeclaringTypeOf(selected);
        if (typeHandle.IsNil) return TypeClassificationMap;

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddMember(string name, string kind)
        {
            if (name.Length == 0 || name.StartsWith('<')) return;
            members[name] = kind;
        }

        void AddType(TypeDefinition type)
        {
            var fieldKind = IsEnum(type) ? "enumfield" : "field";
            foreach (var h in type.GetFields()) AddMember(metadata.GetString(metadata.GetFieldDefinition(h).Name), fieldKind);
            foreach (var h in type.GetProperties()) AddMember(metadata.GetString(metadata.GetPropertyDefinition(h).Name), "property");
            foreach (var h in type.GetEvents()) AddMember(metadata.GetString(metadata.GetEventDefinition(h).Name), "event");
            // Generic parameters are added last so that inside the type's own source a name like T reads
            // as a type parameter rather than a same-named class, matching dnSpy's distinct parameter color.
            foreach (var h in type.GetGenericParameters()) AddMember(metadata.GetString(metadata.GetGenericParameter(h).Name), "typeparam");
            foreach (var m in type.GetMethods())
                foreach (var h in metadata.GetMethodDefinition(m).GetGenericParameters())
                    AddMember(metadata.GetString(metadata.GetGenericParameter(h).Name), "typeparam");
            foreach (var nested in type.GetNestedTypes()) AddType(metadata.GetTypeDefinition(nested));
        }

        AddType(metadata.GetTypeDefinition(typeHandle));
        return new LayeredClassifications(members, TypeClassificationMap);
    }

    private string Classify(TypeDefinition definition)
    {
        if ((definition.Attributes & TypeAttributes.Interface) != 0) return "interface";
        if (IsEnum(definition)) return "enum";
        if (IsDelegate(definition)) return "delegate";
        if (IsValueType(definition)) return "struct";
        return IsStaticClass(definition) ? "staticclass" : "class";
    }

    // Answers a name from the document's own member overlay first, then the shared assembly-wide type
    // map, so that large map is referenced rather than copied into every document's classifications.
    private sealed class LayeredClassifications(IReadOnlyDictionary<string, string> overlay, IReadOnlyDictionary<string, string> baseMap) : IReadOnlyDictionary<string, string>
    {
        public bool TryGetValue(string key, out string value) => overlay.TryGetValue(key, out value!) || baseMap.TryGetValue(key, out value!);
        public bool ContainsKey(string key) => overlay.ContainsKey(key) || baseMap.ContainsKey(key);
        public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public IEnumerable<string> Keys => this.Select(pair => pair.Key);
        public IEnumerable<string> Values => this.Select(pair => pair.Value);
        public int Count => overlay.Count + baseMap.Count(pair => !overlay.ContainsKey(pair.Key));
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            overlay.Concat(baseMap.Where(pair => !overlay.ContainsKey(pair.Key))).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    private IReadOnlyDictionary<int, int> BuildSymbolLocations(string source, EntityHandle selected)
    {
        var typeHandle = DeclaringTypeOf(selected);
        if (typeHandle.IsNil) return new Dictionary<int, int>();
        var type = metadata.GetTypeDefinition(typeHandle);
        var declarations = new List<(EntityHandle Handle, string Name, bool Callable)>
        {
            (typeHandle, TypeIdentifier(type), false)
        };
        declarations.AddRange(type.GetFields().Select(handle => ((EntityHandle)handle, metadata.GetString(metadata.GetFieldDefinition(handle).Name), false)));
        declarations.AddRange(type.GetProperties().Select(handle => ((EntityHandle)handle, metadata.GetString(metadata.GetPropertyDefinition(handle).Name), false)));
        declarations.AddRange(type.GetEvents().Select(handle => ((EntityHandle)handle, metadata.GetString(metadata.GetEventDefinition(handle).Name), false)));
        declarations.AddRange(type.GetMethods().Where(handle => !metadata.GetMethodDefinition(handle).Attributes.HasFlag(MethodAttributes.SpecialName)).Select(handle =>
            ((EntityHandle)handle, metadata.GetString(metadata.GetMethodDefinition(handle).Name), true)));

        var lines = SourceLines(source);
        var used = new HashSet<int>();
        var locations = new Dictionary<int, int>();
        foreach (var declaration in declarations)
        {
            var candidates = Enumerable.Range(0, lines.Count)
                .Where(index => !used.Contains(index) && IsDeclarationLine(lines[index].Text, declaration.Name, declaration.Callable))
                .ToArray();
            if (candidates.Length == 0) continue;
            var declarationIndent = candidates.Min(index => LeadingWhitespace(lines[index].Text));
            var line = candidates.First(index => LeadingWhitespace(lines[index].Text) == declarationIndent);
            used.Add(line);
            locations[MetadataTokens.GetToken(declaration.Handle)] = lines[line].Offset;
        }
        return locations;
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
