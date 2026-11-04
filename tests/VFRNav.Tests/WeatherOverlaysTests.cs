using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class RadarMapsTests
{
    // Même forme que la réponse réelle de RainViewer, valeurs raccourcies.
    private const string Sample = """
        {"version":"2.0","generated":1793772922,"host":"https://tilecache.rainviewer.com",
         "radar":{"past":[
           {"time":1793772000,"path":"/v2/radar/b"},
           {"time":1793771400,"path":"/v2/radar/a"},
           {"time":1793772600,"path":"/v2/radar/c"}],
           "nowcast":[]},
         "satellite":{"infrared":[]}}
        """;

    [Fact]
    public void Parse_sorts_frames_by_time_and_exposes_the_latest()
    {
        var maps = RadarMaps.Parse(Sample);

        Assert.Equal(3, maps.Frames.Count);
        Assert.Equal(["/v2/radar/a", "/v2/radar/b", "/v2/radar/c"], maps.Frames.Select(f => f.Path));
        Assert.Equal("/v2/radar/c", maps.Latest.Path);
        Assert.Equal("https://tilecache.rainviewer.com", maps.Host);
    }

    [Fact]
    public void Tile_url_template_keeps_the_zxy_placeholders_for_the_tile_source()
    {
        var maps = RadarMaps.Parse(Sample);
        Assert.Equal("https://tilecache.rainviewer.com/v2/radar/c/256/{z}/{x}/{y}/2/1_1.png", maps.TileUrlTemplate(maps.Latest));
    }

    [Fact]
    public void Age_is_measured_from_the_latest_frame()
    {
        var maps = RadarMaps.Parse(Sample);
        Assert.Equal(TimeSpan.FromMinutes(7), maps.Age(maps.Latest.Time.AddMinutes(7)));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("""{"host":"https://x","radar":{"past":[]}}""")]
    [InlineData("""{"host":"http://non-securise","radar":{"past":[{"time":1,"path":"/p"}]}}""")]
    [InlineData("""{"host":"https://x","radar":{"past":[{"time":1,"path":""}]}}""")]
    public void Unusable_responses_are_reported_as_invalid_data(string json) =>
        Assert.Throws<InvalidDataException>(() => RadarMaps.Parse(json));
}

public class SatelliteCloudsTests
{
    [Fact]
    public void GetMap_url_is_a_transparent_png_in_web_mercator_with_invariant_numbers()
    {
        string url = SatelliteClouds.GetMapUrl(-1000000.5, 5000000, 0, 6000000.25, 256);

        Assert.StartsWith("https://view.eumetsat.int/geoserver/ows?", url);
        Assert.Contains("request=GetMap", url);
        Assert.Contains("layers=mtg_fd%3Air105_hrfi", url);
        Assert.Contains("transparent=true", url);
        Assert.Contains("crs=EPSG:3857", url);
        Assert.Contains("bbox=-1000000.5,5000000,0,6000000.25", url);   // point décimal, jamais de virgule
        Assert.Contains("width=256&height=256", url);
        Assert.DoesNotContain("time=", url);                            // pas de date : l'image la plus récente
    }

    [Fact]
    public void Warm_ground_becomes_transparent_and_cold_cloud_tops_stay_visible()
    {
        Assert.Equal(0, SatelliteClouds.CloudAlpha(40));
        Assert.Equal(0, SatelliteClouds.CloudAlpha(105));
        Assert.InRange(SatelliteClouds.CloudAlpha(255), 0.7, 0.85);
    }

    [Fact]
    public void Cloud_alpha_never_decreases_when_the_pixel_gets_brighter()
    {
        double previous = -1;
        for (int l = 0; l <= 255; l++)
        {
            double a = SatelliteClouds.CloudAlpha(l);
            Assert.InRange(a, 0, 1);
            Assert.True(a >= previous);
            previous = a;
        }
    }

    [Fact]
    public void Overlay_conversion_uses_brightness_for_alpha_and_keeps_transparent_pixels_transparent()
    {
        byte[] input =
        [
            20, 20, 20, 255,        // sol sombre, opaque
            255, 255, 255, 255,     // sommet de nuage, opaque
            255, 255, 255, 0,       // hors du disque du satellite : transparent
            180, 180, 180, 128,     // gris moyen, à moitié transparent
        ];
        var output = new byte[input.Length];

        SatelliteClouds.ToCloudOverlay(input, output);

        Assert.Equal(0, output[3]);
        Assert.InRange(output[7], 180, 215);
        Assert.Equal(0, output[11]);
        Assert.InRange(output[15], 1, output[7]);
        // Toute l'image a la même teinte : seule l'opacité varie.
        for (int p = 0; p < 4; p++) Assert.Equal(output[0], output[p * 4]);
    }

    [Fact]
    public void Overlay_conversion_rejects_a_too_small_output_buffer() =>
        Assert.Throws<ArgumentException>(() => SatelliteClouds.ToCloudOverlay(new byte[8], new byte[4]));
}
