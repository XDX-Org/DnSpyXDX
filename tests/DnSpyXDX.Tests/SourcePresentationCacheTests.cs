using DnSpyXDX.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class SourcePresentationCacheTests
{
    [Fact]
    public async Task Reuses_models_and_token_batches()
    {
        var cache = Create();
        var key = Key(1);
        var model = await cache.GetModelAsync(key, "class C {}\n");
        var sameModel = await cache.GetModelAsync(key, "class C {}\n");
        var first = await cache.GetLinesAsync(model, 0, 1, null, null, null, default);
        var second = await cache.GetLinesAsync(model, 0, 1, null, null, null, default);

        Assert.Same(model, sameModel);
        Assert.Same(first, second);
        Assert.Equal(1, cache.Statistics.Models);
        Assert.Equal(1, cache.Statistics.Batches);
        Assert.Equal(2, cache.Statistics.Hits);
    }

    [Fact]
    public async Task Evicts_least_recently_used_inactive_model_but_not_active_model()
    {
        var cache = Create(maximumModels: 2);
        var first = await cache.GetModelAsync(Key(1), "first");
        using var active = cache.Activate(first.Key);
        await cache.GetModelAsync(Key(2), "second");
        await cache.GetModelAsync(Key(3), "third");

        Assert.Equal(2, cache.Statistics.Models);
        Assert.Same(first, await cache.GetModelAsync(Key(1), "first"));
        Assert.True(cache.Statistics.Evictions >= 1);
    }

    [Fact]
    public async Task Assembly_removal_releases_its_models_and_batches()
    {
        var cache = Create();
        var key = Key(1);
        var model = await cache.GetModelAsync(key, "class C {}");
        await cache.GetLinesAsync(model, 0, 1, null, null, null, default);

        cache.RemoveAssembly(key.ModuleMvid);

        Assert.Equal(0, cache.Statistics.Models);
        Assert.Equal(0, cache.Statistics.Batches);
    }

    private static SourcePresentationCache Create(int maximumModels = SourcePresentationCache.DefaultMaximumModels) =>
        new(NullLogger<SourcePresentationCache>.Instance) { MaximumModels = maximumModels };

    private static SourceDocumentKey Key(int token) => new(Guid.Parse("fd4da291-c9d7-4db5-8d0c-12fa28425ac8"), token, "csharp", "default");
}
