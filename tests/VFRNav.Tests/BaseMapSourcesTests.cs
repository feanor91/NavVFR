using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class BaseMapSourcesTests
{
    [Fact]
    public void The_light_map_has_a_base_layer_and_a_names_layer_with_the_esri_row_and_column_order()
    {
        Assert.Equal(2, BaseMapSources.LayerCount("clair", ""));
        Assert.EndsWith("World_Light_Gray_Base/MapServer/tile/8/90/129", BaseMapSources.TileUrl("clair", "", 0, 8, 129, 90));
        Assert.EndsWith("World_Light_Gray_Reference/MapServer/tile/8/90/129", BaseMapSources.TileUrl("clair", "", 1, 8, 129, 90));
    }

    [Fact]
    public void A_carto_map_without_a_key_falls_back_to_the_light_map_like_the_application_does()
    {
        Assert.Equal("clair", BaseMapSources.Effective("carto-light", ""));
        Assert.Equal("carto-light", BaseMapSources.Effective("carto-light", "KEY"));
        Assert.Contains("light_all/5/16/11.png?key=KEY", BaseMapSources.TileUrl("carto-light", "KEY", 0, 5, 16, 11));
    }

    [Fact]
    public void The_street_map_has_one_layer()
    {
        Assert.Equal(1, BaseMapSources.LayerCount("rues", ""));
        Assert.Equal("https://tile.openstreetmap.org/19/1/1.png", BaseMapSources.TileUrl("rues", "", 0, 19, 1, 1));
    }

    [Theory]
    [InlineData("rues", 1, 5, 0, 0)]
    [InlineData("clair", 2, 5, 0, 0)]
    [InlineData("clair", 0, 17, 0, 0)]
    [InlineData("clair", 0, 3, 8, 0)]
    [InlineData("clair", 0, 3, -1, 0)]
    public void Tiles_outside_the_map_are_refused(string key, int layer, int z, int x, int y) =>
        Assert.Null(BaseMapSources.TileUrl(key, "", layer, z, x, y));
}
