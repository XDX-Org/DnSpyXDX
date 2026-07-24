using DnSpyXDX.Application;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DnSpyXDX.UI;

public readonly record struct SourceCacheStatistics(int Models, int Batches, long EstimatedBytes, long Hits, long Misses, long Evictions);

public sealed class SourcePresentationCache(ILogger<SourcePresentationCache> logger)
{
    public const int DefaultMaximumModels = 12;
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
    private const long MaximumBatchBytes = 24L * 1024 * 1024;
    private readonly object gate = new();
    private readonly object noLinks = new();
    private readonly object noKinds = new();
    private readonly object noReferences = new();
    private readonly Dictionary<SourceDocumentKey, ModelEntry> models = [];
    private readonly Dictionary<BatchKey, BatchEntry> batches = [];
    private long clock;
    private long hits;
    private long misses;
    private long evictions;

    public int MaximumModels { get; init; } = DefaultMaximumModels;
    public long MaximumBytes { get; init; } = DefaultMaximumBytes;

    public async Task<SourceDocumentModel> GetModelAsync(SourceDocumentKey key, string text, CancellationToken token = default)
    {
        lock (gate)
        {
            if (models.TryGetValue(key, out var cached) && cached.Model.Text == text)
            {
                cached.Used = ++clock;
                hits++;
                return cached.Model;
            }
            misses++;
        }

        var created = await Task.Run(() => SourceDocumentModel.Create(key, text, token), token);
        lock (gate)
        {
            if (models.TryGetValue(key, out var cached) && cached.Model.Text == text) return cached.Model;
            RemoveDocumentLocked(key);
            models[key] = new ModelEntry(created, ++clock);
            TrimLocked(key);
            LogLocked();
            return created;
        }
    }

    public async Task<IReadOnlyList<SourceTokenizedLine>> GetLinesAsync(SourceDocumentModel model, int start, int count,
        IReadOnlyDictionary<string, SymbolId?>? links, IReadOnlyDictionary<string, string>? typeKinds, IReadOnlyList<ReferenceSpan>? references, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = new BatchKey(model.Key, start, count, links ?? noLinks, typeKinds ?? noKinds, references ?? noReferences);
        CancellationToken evictionToken;
        lock (gate)
        {
            if (batches.TryGetValue(key, out var cached))
            {
                cached.Used = ++clock;
                hits++;
                return cached.Lines;
            }
            misses++;
            evictionToken = models.TryGetValue(model.Key, out var document) ? document.Cancellation.Token : CancellationToken.None;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, evictionToken);
        var timer = Stopwatch.StartNew();
        var lines = await model.TokenizeLinesAsync(start, count, links, typeKinds, references, linked.Token);
        timer.Stop();
        token.ThrowIfCancellationRequested();
        var bytes = Estimate(lines);
        lock (gate)
        {
            if (batches.TryGetValue(key, out var cached)) return cached.Lines;
            batches[key] = new BatchEntry(lines, bytes, ++clock);
            if (models.TryGetValue(model.Key, out var document)) document.Used = clock;
            TrimLocked(model.Key);
            logger.LogDebug("Source tokenization lines {Start}-{End} took {ElapsedMs} ms; {Bytes} bytes cached", start, start + lines.Count, timer.ElapsedMilliseconds, bytes);
            return lines;
        }
    }

    public IDisposable Activate(SourceDocumentKey key)
    {
        lock (gate)
        {
            if (models.TryGetValue(key, out var entry)) entry.Active++;
        }
        return new Lease(this, key);
    }

    public void RemoveAssembly(Guid moduleMvid)
    {
        lock (gate)
        {
            foreach (var key in models.Keys.Where(key => key.ModuleMvid == moduleMvid).ToArray()) RemoveDocumentLocked(key);
            LogLocked();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            foreach (var entry in models.Values) entry.Dispose();
            models.Clear();
            batches.Clear();
        }
    }

    public SourceCacheStatistics Statistics
    {
        get { lock (gate) return new(models.Count, batches.Count, EstimatedBytesLocked(), hits, misses, evictions); }
    }

    private void Release(SourceDocumentKey key)
    {
        lock (gate)
        {
            if (models.TryGetValue(key, out var entry)) entry.Active = Math.Max(0, entry.Active - 1);
            TrimLocked(null);
        }
    }

    private void TrimLocked(SourceDocumentKey? protectedKey)
    {
        while (models.Count > MaximumModels || EstimatedBytesLocked() > MaximumBytes)
        {
            var victim = models.Where(pair => pair.Key != protectedKey && pair.Value.Active == 0).MinBy(pair => pair.Value.Used);
            if (victim.Equals(default(KeyValuePair<SourceDocumentKey, ModelEntry>))) break;
            RemoveDocumentLocked(victim.Key);
        }
        while (batches.Values.Sum(batch => batch.Bytes) > MaximumBatchBytes)
        {
            var victim = batches.Where(pair => pair.Key.Document != protectedKey).MinBy(pair => pair.Value.Used);
            if (victim.Equals(default(KeyValuePair<BatchKey, BatchEntry>))) break;
            batches.Remove(victim.Key);
            evictions++;
        }
    }

    private void RemoveDocumentLocked(SourceDocumentKey key)
    {
        if (!models.Remove(key, out var entry)) return;
        entry.Dispose();
        foreach (var batch in batches.Keys.Where(batch => batch.Document == key).ToArray()) batches.Remove(batch);
        evictions++;
    }

    private long EstimatedBytesLocked() => models.Values.Sum(entry => entry.Model.EstimatedBytes) + batches.Values.Sum(entry => entry.Bytes);
    private void LogLocked() => logger.LogDebug("Source cache: {Models} models, {Batches} batches, {Bytes} bytes, {Hits} hits, {Misses} misses, {Evictions} evictions", models.Count, batches.Count, EstimatedBytesLocked(), hits, misses, evictions);
    private static long Estimate(IReadOnlyList<SourceTokenizedLine> lines) => lines.Sum(line => 96L + line.Text.Length * sizeof(char) + line.Tokens.Count * 56L + (line.StartState.Braces.Count + line.EndState.Braces.Count) * 16L);

    private sealed class ModelEntry(SourceDocumentModel model, long used) : IDisposable
    {
        public SourceDocumentModel Model { get; } = model;
        public long Used { get; set; } = used;
        public int Active { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); }
    }
    private sealed class BatchEntry(IReadOnlyList<SourceTokenizedLine> lines, long bytes, long used) { public IReadOnlyList<SourceTokenizedLine> Lines { get; } = lines; public long Bytes { get; } = bytes; public long Used { get; set; } = used; }
    private readonly record struct BatchKey(SourceDocumentKey Document, int Start, int Count, object Links, object TypeKinds, object References);
    private sealed class Lease(SourcePresentationCache owner, SourceDocumentKey key) : IDisposable { private SourcePresentationCache? cache = owner; public void Dispose() => Interlocked.Exchange(ref cache, null)?.Release(key); }
}
