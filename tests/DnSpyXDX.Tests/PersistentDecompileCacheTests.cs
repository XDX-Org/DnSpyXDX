using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class PersistentDecompileCacheTests
{
    // A throwaway cache directory that is removed when the test finishes.
    private sealed class TempCacheDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnspyxdx-cache-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static DecompilerDocument SampleDocument(Guid mvid, int token = 0x02000123) => new(
        new SymbolId(mvid, token),
        "Widget",
        "csharp",
        "public class Widget\n{\n\tpublic int Value;\n}\n",
        References: new[]
        {
            new ReferenceSpan(13, 6, new SymbolId(mvid, 0x0A000001), "OtherAssembly", "Go to Widget", 41),
            new ReferenceSpan(31, 3, null, null, "Go to Value")
        },
        Diagnostics: Array.Empty<DiagnosticMessage>(),
        // A null value models an overload set (highlightable but not navigable) - it must survive the round-trip.
        SymbolLinks: new Dictionary<string, SymbolId?> { ["Widget"] = new SymbolId(mvid, token), ["Overloaded"] = null },
        TypeClassifications: new Dictionary<string, string> { ["Widget"] = "class", ["Value"] = "field" },
        SymbolLocations: new Dictionary<int, int> { [token] = 0, [0x06000045] = 24 },
        SemanticSpans: new[] { new ClassifiedSpan(0, 6, "keyword"), new ClassifiedSpan(20, 6, "class") });

    private static void AssertDocumentsEquivalent(DecompilerDocument expected, DecompilerDocument actual)
    {
        Assert.Equal(expected.Symbol, actual.Symbol);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.Text, actual.Text);
        // References and spans are ordered lists whose order is meaningful and preserved.
        Assert.Equal(expected.References, actual.References);
        Assert.Equal(expected.SemanticSpans, actual.SemanticSpans);
        // Dictionaries are compared by content, not enumeration order, which JSON does not guarantee.
        AssertDictionaryEqual(expected.SymbolLinks, actual.SymbolLinks);
        AssertDictionaryEqual(expected.TypeClassifications, actual.TypeClassifications);
        AssertDictionaryEqual(expected.SymbolLocations, actual.SymbolLocations);
    }

    private static void AssertDictionaryEqual<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? expected, IReadOnlyDictionary<TKey, TValue>? actual)
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual!.Count);
        foreach (var pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out var value), $"missing key {pair.Key}");
            Assert.Equal(pair.Value, value);
        }
    }

    private const string AssemblyId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Fact]
    public void Round_trips_every_document_field_through_disk()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        var document = SampleDocument(Guid.NewGuid());

        cache.Save(AssemblyId, document, DecompilerLanguage.CSharp, showMetadataTokens: false);
        var loaded = cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false);

        Assert.NotNull(loaded);
        AssertDocumentsEquivalent(document, loaded!);
        // The null-valued (overload) link in particular must come back as a present key with a null value.
        Assert.True(loaded!.SymbolLinks!.TryGetValue("Overloaded", out var overloaded));
        Assert.Null(overloaded);
    }

    [Fact]
    public void Returns_null_when_no_entry_exists()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);

        Assert.Null(cache.TryLoad(AssemblyId, 0x02000001, DecompilerLanguage.CSharp, showMetadataTokens: false));
    }

    [Fact]
    public void The_assembly_id_changes_with_any_byte_of_content()
    {
        var content = new byte[4096];
        new Random(1).NextBytes(content);
        var flipped = (byte[])content.Clone();
        flipped[2048] ^= 0x01; // a single changed bit

        Assert.NotEqual(PersistentDecompileCache.ComputeAssemblyId(content), PersistentDecompileCache.ComputeAssemblyId(flipped));
        Assert.Equal(PersistentDecompileCache.ComputeAssemblyId(content), PersistentDecompileCache.ComputeAssemblyId((byte[])content.Clone()));
    }

    [Fact]
    public void A_differently_patched_assembly_does_not_read_the_original_entry()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        var document = SampleDocument(Guid.NewGuid());

        // Two builds that share an MVID but differ by a byte produce different ids, so one never serves the
        // other's cached source - the scenario a patched Assembly-CSharp.dll would otherwise hit.
        var original = new byte[1024]; new Random(7).NextBytes(original);
        var patched = (byte[])original.Clone(); patched[512]++;
        var originalId = PersistentDecompileCache.ComputeAssemblyId(original);
        var patchedId = PersistentDecompileCache.ComputeAssemblyId(patched);

        cache.Save(originalId, document, DecompilerLanguage.CSharp, showMetadataTokens: false);

        Assert.Null(cache.TryLoad(patchedId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
        Assert.NotNull(cache.TryLoad(originalId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
    }

    [Fact]
    public void Entries_are_keyed_by_language_and_metadata_token_flag()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        var document = SampleDocument(Guid.NewGuid());

        cache.Save(AssemblyId, document, DecompilerLanguage.CSharp, showMetadataTokens: false);

        // The same token under a different language or a different metadata-token setting is a distinct entry.
        Assert.Null(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.IL, showMetadataTokens: false));
        Assert.Null(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: true));
        Assert.NotNull(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
    }

    [Fact]
    public void A_corrupt_entry_is_treated_as_a_miss_and_removed()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        var document = SampleDocument(Guid.NewGuid());
        cache.Save(AssemblyId, document, DecompilerLanguage.CSharp, showMetadataTokens: false);

        var file = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.json.gz", SearchOption.AllDirectories));
        File.WriteAllText(file, "this is not gzip");

        Assert.Null(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Evict_removes_one_assemblys_entries_and_leaves_others()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        var kept = PersistentDecompileCache.ComputeAssemblyId(new byte[] { 1, 2, 3 });
        var document = SampleDocument(Guid.NewGuid());
        cache.Save(AssemblyId, document, DecompilerLanguage.CSharp, showMetadataTokens: false);
        cache.Save(AssemblyId, document, DecompilerLanguage.IL, showMetadataTokens: false);
        cache.Save(kept, document, DecompilerLanguage.CSharp, showMetadataTokens: false);

        cache.Evict(AssemblyId);

        // Every language/flag entry for the evicted assembly is gone; another assembly is untouched.
        Assert.Null(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
        Assert.Null(cache.TryLoad(AssemblyId, document.Symbol.MetadataToken, DecompilerLanguage.IL, showMetadataTokens: false));
        Assert.NotNull(cache.TryLoad(kept, document.Symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false));
    }

    [Fact]
    public void Evicting_a_missing_assembly_is_a_no_op()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);

        cache.Evict(AssemblyId); // must not throw
    }

    [Fact]
    public async Task Unloading_an_assembly_in_the_ui_evicts_its_persistent_cache()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings(), cache);
        var assembly = await backend.OpenAsync(typeof(PersistentDecompileCacheTests).Assembly.Location);
        var symbol = new SymbolId(assembly.ModuleMvid, GetTypeToken(backend, assembly, nameof(PersistentDecompileCacheTests)));

        await backend.DecompileAsync(symbol, DecompilerLanguage.CSharp);
        Assert.True(await WaitUntilAsync(() => CachedFileCount(directory.Path) > 0), "decompile should have written a cache entry");

        await backend.CloseAsync(assembly.SessionId); // the UI unload path

        Assert.True(await WaitUntilAsync(() => CachedFileCount(directory.Path) == 0),
            "closing the assembly should have evicted its cached document");
    }

    [Fact]
    public async Task Disposing_the_backend_keeps_the_cache_for_a_later_restore()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        await using (var backend = new DecompilerBackend(new RuntimeDisplaySettings(), cache))
        {
            var assembly = await backend.OpenAsync(typeof(PersistentDecompileCacheTests).Assembly.Location);
            var symbol = new SymbolId(assembly.ModuleMvid, GetTypeToken(backend, assembly, nameof(PersistentDecompileCacheTests)));
            await backend.DecompileAsync(symbol, DecompilerLanguage.CSharp);
            Assert.True(await WaitUntilAsync(() => CachedFileCount(directory.Path) > 0), "decompile should have written a cache entry");
        }
        // Disposal is the app-shutdown path (session is saved separately); it must NOT evict the cache.
        await Task.Delay(200);

        Assert.True(CachedFileCount(directory.Path) > 0, "disposing the backend must not evict the cache");
    }

    private static int CachedFileCount(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json.gz", SearchOption.AllDirectories).Count() : 0;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        // Saves and evictions run on background tasks; allow generous time in case the thread pool is busy.
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public void Only_text_languages_are_cacheable()
    {
        Assert.True(PersistentDecompileCache.IsCacheable(DecompilerLanguage.CSharp));
        Assert.True(PersistentDecompileCache.IsCacheable(DecompilerLanguage.IL));
        Assert.True(PersistentDecompileCache.IsCacheable(DecompilerLanguage.ILWithCSharp));
        Assert.False(PersistentDecompileCache.IsCacheable(DecompilerLanguage.Hex));
    }

    [Fact]
    public void Construction_prunes_entries_from_other_versions()
    {
        using var directory = new TempCacheDirectory();
        // A leftover directory from a previous schema or ILSpy version can never be read again.
        var stale = Path.Combine(directory.Path, "v0-ilspy0.0.0.0");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.json.gz"), "stale");

        var cache = new PersistentDecompileCache(directory.Path);
        // The current version's entries still work after pruning.
        cache.Save(AssemblyId, SampleDocument(Guid.NewGuid()), DecompilerLanguage.CSharp, showMetadataTokens: false);

        Assert.False(Directory.Exists(stale));
        Assert.NotNull(cache.TryLoad(AssemblyId, 0x02000123, DecompilerLanguage.CSharp, showMetadataTokens: false));
    }

    [Fact]
    public async Task Backend_serves_a_decompile_from_the_cache_instead_of_ilspy()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings(), cache);
        var assembly = await backend.OpenAsync(typeof(PersistentDecompileCacheTests).Assembly.Location);

        var symbol = new SymbolId(assembly.ModuleMvid, 0x02000123);
        // The session keys the cache on a hash of the assembly's bytes; compute the same id here so the seed
        // lands where DecompileAsync will look for it.
        var assemblyId = PersistentDecompileCache.ComputeAssemblyId(await File.ReadAllBytesAsync(assembly.Path));
        // Pre-seed a sentinel document; if DecompileAsync consults the cache it returns this verbatim rather
        // than running ILSpy over that token.
        var seeded = SampleDocument(assembly.ModuleMvid) with { Symbol = symbol, Text = "SENTINEL FROM CACHE" };
        cache.Save(assemblyId, seeded, DecompilerLanguage.CSharp, showMetadataTokens: false);

        var document = await backend.DecompileAsync(symbol, DecompilerLanguage.CSharp);

        Assert.Equal("SENTINEL FROM CACHE", document.Text);
    }

    [Fact]
    public async Task Backend_writes_a_real_decompile_into_the_cache()
    {
        using var directory = new TempCacheDirectory();
        var cache = new PersistentDecompileCache(directory.Path);
        await using var backend = new DecompilerBackend(new RuntimeDisplaySettings(), cache);
        var assembly = await backend.OpenAsync(typeof(PersistentDecompileCacheTests).Assembly.Location);
        var symbol = new SymbolId(assembly.ModuleMvid, GetTypeToken(backend, assembly, nameof(PersistentDecompileCacheTests)));

        var produced = await backend.DecompileAsync(symbol, DecompilerLanguage.CSharp);
        var assemblyId = PersistentDecompileCache.ComputeAssemblyId(await File.ReadAllBytesAsync(assembly.Path));

        // The write happens off the decompile path; give it a moment, then a fresh cache over the same
        // directory must read back the same source.
        DecompilerDocument? reloaded = null;
        for (var attempt = 0; attempt < 50 && reloaded is null; attempt++)
        {
            reloaded = new PersistentDecompileCache(directory.Path).TryLoad(assemblyId, symbol.MetadataToken, DecompilerLanguage.CSharp, showMetadataTokens: false);
            if (reloaded is null) await Task.Delay(20);
        }

        Assert.NotNull(reloaded);
        Assert.Equal(produced.Text, reloaded!.Text);
        Assert.Equal(produced.References, reloaded.References);
        Assert.Equal(produced.SemanticSpans, reloaded.SemanticSpans);
        Assert.Equal(produced.SymbolLocations, reloaded.SymbolLocations);
    }

    private static int GetTypeToken(DecompilerBackend backend, AssemblyDescriptor assembly, string typeName)
    {
        var results = backend.SearchAsync(typeName).GetAwaiter().GetResult();
        var type = results.First(r => r.Kind == "Type" && r.Name == typeName && r.Symbol.ModuleMvid == assembly.ModuleMvid);
        return type.Symbol.MetadataToken;
    }
}
