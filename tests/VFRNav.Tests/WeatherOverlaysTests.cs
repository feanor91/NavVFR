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
    public void LastFrames_returns_the_most_recent_images_oldest_first()
    {
        var maps = RadarMaps.Parse(Sample);

        Assert.Equal(["/v2/radar/b", "/v2/radar/c"], maps.LastFrames(2).Select(f => f.Path));
        Assert.Equal(3, maps.LastFrames(10).Count);                 // le service en fournit moins que demandé
        Assert.Equal(["/v2/radar/c"], maps.LastFrames(0).Select(f => f.Path));   // au moins une image
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
    public void Each_layer_kind_targets_its_own_eumetsat_product()
    {
        Assert.Equal("msg_fes:clm", SatelliteClouds.LayerFor(CloudLayerKind.Cover));
        Assert.Equal("mtg_fd:ir105_hrfi", SatelliteClouds.LayerFor(CloudLayerKind.Tops));
        Assert.Contains("layers=msg_fes%3Aclm", SatelliteClouds.GetMapUrl(SatelliteClouds.MaskLayer, 0, 0, 100, 100));
    }

    [Fact]
    public void The_two_layers_have_clearly_different_tints()
    {
        var (cr, cg, cb) = SatelliteClouds.Tint(CloudLayerKind.Cover);
        var (tr, tg, tb) = SatelliteClouds.Tint(CloudLayerKind.Tops);

        // Distance de couleur assez grande pour qu'on les distingue superposées sur une carte.
        double distance = Math.Sqrt(Math.Pow(cr - tr, 2) + Math.Pow(cg - tg, 2) + Math.Pow(cb - tb, 2));
        Assert.True(distance > 80, $"teintes trop proches : {distance:0}");
        Assert.NotEqual(SatelliteClouds.TintHex(CloudLayerKind.Cover), SatelliteClouds.TintHex(CloudLayerKind.Tops));
        Assert.Matches("^#[0-9A-F]{6}$", SatelliteClouds.TintHex(CloudLayerKind.Cover));
    }

    // ---------- couche « sommets froids » (infrarouge) ----------

    [Fact]
    public void Warm_ground_becomes_transparent_and_cold_cloud_tops_stay_visible()
    {
        Assert.Equal(0, SatelliteClouds.CloudAlpha(40));
        Assert.Equal(0, SatelliteClouds.CloudAlpha(105));
        Assert.InRange(SatelliteClouds.CloudAlpha(255), 0.8, 0.95);
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
    public void Tops_overlay_uses_brightness_for_alpha_and_keeps_transparent_pixels_transparent()
    {
        byte[] input =
        [
            20, 20, 20, 255,        // sol sombre, opaque
            255, 255, 255, 255,     // sommet de nuage, opaque
            255, 255, 255, 0,       // hors du disque du satellite : transparent
            180, 180, 180, 128,     // gris moyen, à moitié transparent
        ];
        var output = new byte[input.Length];

        SatelliteClouds.ToTopsOverlay(input, output);

        Assert.Equal(0, output[3]);
        Assert.InRange(output[7], 200, 235);
        Assert.Equal(0, output[11]);
        Assert.InRange(output[15], 1, output[7]);
        var (r, g, b) = SatelliteClouds.Tint(CloudLayerKind.Tops);
        for (int p = 0; p < 4; p++) { Assert.Equal(r, output[p * 4]); Assert.Equal(g, output[p * 4 + 1]); Assert.Equal(b, output[p * 4 + 2]); }
    }

    // ---------- couche « couverture » (masque de nuages) ----------

    private static readonly byte[] White = [255, 255, 255, 255], CleanLand = [0, 190, 0, 255], CleanSea = [0, 0, 255, 255], Nothing = [0, 0, 0, 0];

    private static byte[] Cover(byte[] pixel)
    {
        var output = new byte[4];
        SatelliteClouds.ToCoverOverlay(pixel, output);
        return output;
    }

    [Fact]
    public void Cover_overlay_tints_cloudy_points_and_leaves_clear_sky_transparent()
    {
        var (r, g, b) = SatelliteClouds.Tint(CloudLayerKind.Cover);
        var cloud = Cover(White);
        Assert.Equal([r, g, b], cloud[..3]);
        Assert.InRange(cloud[3], 200, 230);          // ~ 85 %

        Assert.Equal(0, Cover(CleanLand)[3]);        // terre dégagée
        Assert.Equal(0, Cover(CleanSea)[3]);         // mer dégagée
        Assert.Equal(0, Cover(Nothing)[3]);          // hors du disque du satellite
    }

    [Fact]
    public void Cover_overlay_sees_low_cloud_that_the_infrared_misses()
    {
        // Le cas de Paris par temps couvert : le sol chaud et le stratus ont la même température (infrarouge sombre).
        var infrared = new byte[] { 60, 60, 60, 255 };
        var tops = new byte[4];
        SatelliteClouds.ToTopsOverlay(infrared, tops);

        Assert.Equal(0, tops[3]);                    // l'infrarouge ne voit rien
        Assert.True(Cover(White)[3] > 150);          // le masque voit le nuage
    }

    [Theory]
    [InlineData(255, 255, 255, 255, true)]
    [InlineData(235, 240, 238, 255, true)]
    [InlineData(0, 190, 0, 255, false)]      // terre dégagée
    [InlineData(0, 0, 255, 255, false)]      // mer dégagée
    [InlineData(255, 255, 255, 0, false)]    // transparent : hors du disque du satellite
    public void Mask_colours_are_classified_as_cloud_or_clear(byte r, byte g, byte b, byte a, bool cloud) =>
        Assert.Equal(cloud, SatelliteClouds.IsCloudInMask(r, g, b, a));

    [Fact]
    public void ToOverlay_dispatches_to_the_right_conversion()
    {
        var input = new byte[] { 255, 255, 255, 255 };
        var cover = new byte[4];
        var tops = new byte[4];

        SatelliteClouds.ToOverlay(CloudLayerKind.Cover, input, cover);
        SatelliteClouds.ToOverlay(CloudLayerKind.Tops, input, tops);

        Assert.Equal(SatelliteClouds.Tint(CloudLayerKind.Cover).R, cover[0]);
        Assert.Equal(SatelliteClouds.Tint(CloudLayerKind.Tops).R, tops[0]);
    }

    [Fact]
    public void Conversions_reject_a_too_small_output_buffer()
    {
        Assert.Throws<ArgumentException>(() => SatelliteClouds.ToCoverOverlay(new byte[8], new byte[4]));
        Assert.Throws<ArgumentException>(() => SatelliteClouds.ToTopsOverlay(new byte[8], new byte[4]));
    }
}

public class RadarLegendTests
{
    [Fact]
    public void Rain_intensity_follows_the_marshall_palmer_relation()
    {
        Assert.InRange(RadarLegend.MmPerHour(20), 0.63, 0.67);    // ≈ 0,65 mm/h
        Assert.InRange(RadarLegend.MmPerHour(30), 2.6, 2.8);      // ≈ 2,7 mm/h
        Assert.InRange(RadarLegend.MmPerHour(40), 11, 12);        // ≈ 11,5 mm/h
        Assert.InRange(RadarLegend.MmPerHour(50), 47, 51);        // ≈ 49 mm/h
    }

    [Fact]
    public void Legend_steps_increase_in_reflectivity_and_in_rain_rate()
    {
        var rain = RadarLegend.Rain;
        Assert.True(rain.Count >= 8);
        for (int i = 1; i < rain.Count; i++)
        {
            Assert.True(rain[i].Dbz > rain[i - 1].Dbz);
            Assert.True(rain[i].MmPerHour > rain[i - 1].MmPerHour);
        }
    }

    [Fact]
    public void Colours_are_valid_hex_and_match_the_official_palette_for_a_few_known_values()
    {
        Assert.All(RadarLegend.Rain, e => Assert.Matches("^#[0-9A-F]{6}$", e.Hex));
        // Valeurs relevées dans la table officielle (colonne « Universal Blue », pluie).
        Assert.Equal("#00A3E0", RadarLegend.Rain.Single(e => e.Dbz == 20).Hex);
        Assert.Equal("#FFEE00", RadarLegend.Rain.Single(e => e.Dbz == 35).Hex);
        Assert.Equal("#C10000", RadarLegend.Rain.Single(e => e.Dbz == 50).Hex);
        Assert.Equal("#FFAAFF", RadarLegend.Rain.Single(e => e.Dbz == 55).Hex);
    }

    [Fact]
    public void Every_step_has_a_description_and_the_strongest_is_flagged_extreme()
    {
        Assert.All(RadarLegend.Rain, e => Assert.False(string.IsNullOrWhiteSpace(e.Intensity)));
        Assert.Equal("extrême", RadarLegend.Rain[^1].Intensity);
    }
}
