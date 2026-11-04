using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class CloudTileCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfrnav-clouds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Slots_are_15_minutes_long()
    {
        var t = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(CloudTileCache.SlotOf(t), CloudTileCache.SlotOf(t.AddMinutes(14)));
        Assert.Equal(CloudTileCache.SlotOf(t) + 1, CloudTileCache.SlotOf(t.AddMinutes(15)));
    }

    [Fact]
    public void A_stored_tile_is_read_back_for_the_same_layer_slot_and_index_only()
    {
        var cache = new CloudTileCache(_root);
        cache.Put("msg_fes:clm", 100, 5, 17, 11, [1, 2, 3]);

        Assert.Equal([1, 2, 3], cache.Get("msg_fes:clm", 100, 5, 17, 11));
        Assert.Null(cache.Get("msg_fes:clm", 101, 5, 17, 11));        // autre créneau : image plus récente à redemander
        Assert.Null(cache.Get("mtg_fd:ir105_hrfi", 100, 5, 17, 11));  // autre couche
        Assert.Null(cache.Get("msg_fes:clm", 100, 5, 17, 12));        // autre tuile
    }

    [Fact]
    public void Old_slots_are_pruned_and_the_current_one_is_kept()
    {
        var cache = new CloudTileCache(_root);
        cache.Put("a", 98, 1, 0, 0, [9]);
        cache.Put("a", 99, 1, 0, 0, [9]);
        cache.Put("a", 100, 1, 0, 0, [9]);

        cache.PruneOlderThan(100);

        Assert.Null(cache.Get("a", 98, 1, 0, 0));
        Assert.Null(cache.Get("a", 99, 1, 0, 0));
        Assert.NotNull(cache.Get("a", 100, 1, 0, 0));
    }

    [Fact]
    public void An_unwritable_or_missing_cache_never_throws()
    {
        var cache = new CloudTileCache(Path.Combine(_root, "absent"));
        Assert.Null(cache.Get("a", 1, 1, 0, 0));
        cache.PruneOlderThan(10);
    }
}
