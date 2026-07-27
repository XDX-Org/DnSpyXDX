using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ICSharpCode.Decompiler.CSharp;
using DnSpyXDX.Application;

namespace DnSpyXDX.Decompilation;

/// <summary>
/// A disk-backed cache of decompiled documents so a type does not have to be decompiled from scratch every
/// time it is opened - most importantly when a saved session is restored on start-up. Entries are gzipped
/// JSON keyed by a hash of the whole assembly file, the member token, the language, and the metadata-token
/// display flag. The content hash (rather than the module MVID) is deliberate: a post-compilation rewrite -
/// an IL patcher, obfuscator, or the kind of assembly patching SPT applies to Assembly-CSharp.dll - changes
/// the bytes while often preserving the MVID, so an MVID key could serve stale source; any byte difference
/// yields a different hash and therefore a clean miss. The cache root is additionally segmented by decompiler
/// version so an ILSpy upgrade, whose output can differ, never serves stale text. Every operation is
/// best-effort: a corrupt, missing, or unwritable entry degrades to a normal decompile.
/// </summary>
public sealed class PersistentDecompileCache
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string root;

    public PersistentDecompileCache(string directory)
    {
        var ilspyVersion = typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString() ?? "0";
        var leaf = $"v{SchemaVersion}-ilspy{ilspyVersion}";
        root = Path.Combine(directory, leaf);
        PruneStaleVersions(directory, leaf);
    }

    // Entries from a previous schema or ILSpy version can never be read again (the version is baked into the
    // root), so their directories are removed to keep the cache from growing without bound across upgrades.
    private static void PruneStaleVersions(string directory, string currentLeaf)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var stale in Directory.EnumerateDirectories(directory)
                         .Where(path => !string.Equals(Path.GetFileName(path), currentLeaf, StringComparison.Ordinal)))
                try { Directory.Delete(stale, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The cache under the current user's local application data, beside the saved session.</summary>
    public static PersistentDecompileCache Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnSpyXDX", "decompile-cache"));

    // Only text views are worth persisting; the hex view is cheap to produce and would store the whole image.
    public static bool IsCacheable(DecompilerLanguage language) =>
        language is DecompilerLanguage.CSharp or DecompilerLanguage.IL or DecompilerLanguage.ILWithCSharp;

    /// <summary>A stable identity for an assembly derived from its exact bytes, so any modification - even a
    /// single patched instruction that leaves the MVID untouched - produces a different key. A 128-bit prefix
    /// of SHA-256 is far beyond any practical collision risk for a per-user cache.</summary>
    public static string ComputeAssemblyId(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content).AsSpan(0, 16)).ToLowerInvariant();

    public DecompilerDocument? TryLoad(string assemblyId, int metadataToken, DecompilerLanguage language, bool showMetadataTokens)
    {
        var path = PathFor(assemblyId, metadataToken, language, showMetadataTokens);
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<DecompilerDocument>(gzip, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            // A half-written or format-incompatible entry is treated as a miss and cleared so it gets rewritten.
            try { File.Delete(path); } catch (Exception delete) when (delete is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    public void Save(string assemblyId, DecompilerDocument document, DecompilerLanguage language, bool showMetadataTokens)
    {
        var path = PathFor(assemblyId, document.Symbol.MetadataToken, language, showMetadataTokens);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a unique temp file and move it into place so a reader never sees a partial entry, and
            // two concurrent writers of the same entry cannot corrupt each other.
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var file = File.Create(temporary))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, document, JsonOptions);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Best-effort: a failed write just means the next open decompiles again.
        }
    }

    /// <summary>Remove every persisted entry for one assembly. Used when the user explicitly unloads it in
    /// the UI (a deliberate "forget this" gesture); app shutdown deliberately does not call this, so a saved
    /// session still restores from the cache on the next launch.</summary>
    public void Evict(string assemblyId)
    {
        var directory = Path.Combine(root, assemblyId);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string PathFor(string assemblyId, int metadataToken, DecompilerLanguage language, bool showMetadataTokens) =>
        Path.Combine(root, assemblyId, $"{metadataToken:X8}-{language.Key()}-{(showMetadataTokens ? 1 : 0)}.json.gz");
}
