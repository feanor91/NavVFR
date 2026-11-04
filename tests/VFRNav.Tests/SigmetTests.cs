using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class SigmetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793768000);   // 2026-11-04 04:53 UTC

    // Même forme que la réponse réelle d'aviationweather.gov (isigmet, format=json).
    private static string Json(long from, long to) => $$"""
        [
          {"icaoId":"LFPW","firId":"LFMM","firName":"LFMM MARSEILLE","validTimeFrom":{{from}},"validTimeTo":{{to}},"seriesId":"T02","hazard":"TS","qualifier":"EMBD",
           "base":null,"top":35000,"geom":"AREA",
           "coords":[{"lon":7.75,"lat":41.75},{"lon":2.75,"lat":43},{"lon":3.5,"lat":43.75},{"lon":7.75,"lat":43},{"lon":7.75,"lat":41.75}],
           "rawSigmet":"WSFR34 LFPW 040457\nLFMM SIGMET T02 VALID 040500/040700 LFPW-"},
          {"icaoId":"SBBR","firId":"SBAZ","firName":"SBAZ AMAZONICA","validTimeFrom":{{from}},"validTimeTo":{{to}},"seriesId":"A1","hazard":"TURB","qualifier":"SEV",
           "base":15000,"top":28000,"geom":"AREAS",
           "coords":[[{"lon":-54,"lat":-31},{"lon":-53,"lat":-31},{"lon":-53,"lat":-32},{"lon":-54,"lat":-31}],
                     [{"lon":-50,"lat":-20},{"lon":-49,"lat":-20},{"lon":-49,"lat":-21},{"lon":-50,"lat":-20}]],
           "rawSigmet":"x"},
          {"icaoId":"XXXX","firId":"XXXX","firName":"degenerate","validTimeFrom":{{from}},"validTimeTo":{{to}},"seriesId":"D","hazard":"TS","qualifier":null,
           "base":null,"top":null,"geom":"AREA","coords":[{"lon":1,"lat":1},{"lon":2,"lat":2}],"rawSigmet":"y"}
        ]
        """;

    private static Waypoint Wp(double lat, double lon, string name = "") => new() { Name = name, Lat = lat, Lon = lon };

    [Fact]
    public void Parse_reads_single_and_multi_area_messages_and_drops_unusable_ones()
    {
        var all = SigmetParser.Parse(Json(1793768400, 1793775600));

        Assert.Equal(2, all.Count);                                   // la zone réduite à une ligne est ignorée
        var ts = all.Single(s => s.Hazard == "TS");
        Assert.Equal("LFMM", ts.FirId);
        Assert.Null(ts.BaseFt);
        Assert.Equal(35000, ts.TopFt);
        Assert.Single(ts.Areas);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793768400), ts.ValidFrom);
        Assert.Equal(2, all.Single(s => s.Hazard == "TURB").Areas.Count);
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    public void Parse_rejects_unexpected_responses(string json) =>
        Assert.Throws<InvalidDataException>(() => SigmetParser.Parse(json));

    [Fact]
    public void Descriptions_are_in_french()
    {
        var ts = SigmetParser.Parse(Json(1793768400, 1793775600)).Single(s => s.Hazard == "TS");
        Assert.Equal("Orages noyés dans les nuages", ts.Description);
        Assert.Equal("Givrage", Sigmet.HazardName("ICE"));
        Assert.Equal("CODE-INCONNU", Sigmet.HazardName("CODE-INCONNU"));
    }

    [Fact]
    public void Validity_is_checked_against_the_flight_period()
    {
        var s = SigmetParser.Parse(Json(1793768400, 1793775600)).First();   // 05:00 -> 07:00 UTC
        Assert.False(s.IsValidAt(Now));                                     // pas encore valable
        Assert.True(s.OverlapsPeriod(Now, Now.AddHours(1)));                // mais un vol d'une heure en rencontre le début
        Assert.False(s.OverlapsPeriod(Now, Now.AddMinutes(5)));      // le vol finit avant le début de validité
        Assert.False(s.OverlapsPeriod(Now.AddHours(5), Now.AddHours(6)));   // déjà expiré
    }

    [Fact]
    public void Route_through_the_area_is_reported_with_entry_exit_and_altitude_status()
    {
        var sigmets = SigmetParser.Parse(Json(1793768400, 1793775600));
        var route = new[] { Wp(42, 1.0, "LFAA"), Wp(42.8, 8.5, "LFBB") };   // traverse la zone des orages (41,75-43,75 N / 2,75-7,75 E)

        var low = SigmetAnalysis.AlongRoute(route, new AltitudeProfile(route, 5000), sigmets, Now, TimeSpan.FromHours(2));
        var high = SigmetAnalysis.AlongRoute(route, new AltitudeProfile(route, 39000), sigmets, Now, TimeSpan.FromHours(2));

        var hit = Assert.Single(low);
        Assert.Equal("T02", hit.Sigmet.SeriesId);
        Assert.Equal(VerticalStatus.Inside, hit.Status);                    // 5000 ft est sous le sommet à FL350
        Assert.True(hit.ExitNm > hit.EntryNm && hit.EntryNm > 0);
        Assert.Equal(VerticalStatus.Above, Assert.Single(high).Status);     // 39000 ft au-dessus
    }

    [Fact]
    public void Route_outside_the_area_or_outside_the_validity_period_is_not_reported()
    {
        var sigmets = SigmetParser.Parse(Json(1793768400, 1793775600));
        var north = new[] { Wp(50, 1, "A"), Wp(50, 8, "B") };
        var south = new[] { Wp(42, 1, "A"), Wp(42.8, 8.5, "B") };

        Assert.Empty(SigmetAnalysis.AlongRoute(north, new AltitudeProfile(north, 5000), sigmets, Now, TimeSpan.FromHours(2)));
        Assert.Empty(SigmetAnalysis.AlongRoute(south, new AltitudeProfile(south, 5000), sigmets, Now.AddHours(10), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Altitude_below_the_floor_is_reported_as_below()
    {
        var turb = SigmetParser.Parse(Json(1793768400, 1793775600)).Single(s => s.Hazard == "TURB");   // 15 000 à 28 000 ft
        var route = new[] { Wp(-30.9, -53.9, "A"), Wp(-31.7, -53.1, "B") };

        var found = SigmetAnalysis.AlongRoute(route, new AltitudeProfile(route, 5000), [turb], Now, TimeSpan.FromHours(2));

        Assert.Equal(VerticalStatus.Below, Assert.Single(found).Status);
    }

    [Fact]
    public void Contains_handles_concave_polygons_and_the_antimeridian()
    {
        // Polygone en « L » : le coin vide n'est pas dedans.
        (double, double)[] ell = [(0, 0), (0, 10), (5, 10), (5, 5), (10, 5), (10, 0), (0, 0)];
        Assert.True(SigmetAnalysis.Contains(ell, 2, 2));
        Assert.False(SigmetAnalysis.Contains(ell, 8, 8));

        // Zone à cheval sur l'antiméridien, avec des longitudes au-delà de 180°.
        (double, double)[] wrap = [(60, 170), (60, 190), (70, 190), (70, 170), (60, 170)];
        Assert.True(SigmetAnalysis.Contains(wrap, 65, 179));
        Assert.True(SigmetAnalysis.Contains(wrap, 65, -179));
        Assert.False(SigmetAnalysis.Contains(wrap, 65, 160));
    }

    [Fact]
    public async Task Service_keeps_the_response_for_ten_minutes()
    {
        var handler = new CountingHandler(Json(1793768400, 1793775600));
        var clock = new ManualClock(Now);
        var service = new SigmetService(new HttpClient(handler), clock);

        await service.GetAsync();
        await service.GetAsync();
        Assert.Equal(1, handler.Calls);

        clock.Advance(TimeSpan.FromMinutes(11));
        await service.GetAsync();
        Assert.Equal(2, handler.Calls);
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public class SigmetPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 6, 0, 0, TimeSpan.Zero);

    private static Sigmet Make(string hazard = "TS", double? baseFt = null, double? topFt = 35000, int fromMin = -60, int toMin = 96, string raw = "WSFR34 LFPW\nLFMM SIGMET T02") => new(
        "LFMM", "LFMM MARSEILLE", "T02", hazard, "EMBD", Now.AddMinutes(fromMin), Now.AddMinutes(toMin), baseFt, topFt,
        [[(41.75, 7.75), (43, 2.75), (43.75, 3.5), (43, 7.75), (41.75, 7.75)]], raw);

    [Fact]
    public void Every_hazard_has_a_valid_distinct_colour_and_unknown_ones_are_grey()
    {
        var colours = SigmetPresentation.Legend.Select(e => e.Hex).ToList();
        Assert.All(colours, h => Assert.Matches("^#[0-9A-F]{6}$", h));
        Assert.Equal(colours.Count, colours.Distinct().Count());
        Assert.Equal("#757575", SigmetPresentation.HazardColorHex("XYZ"));
        Assert.Equal(SigmetPresentation.HazardColorHex("DS"), SigmetPresentation.HazardColorHex("ss"));
    }

    [Theory]
    [InlineData(35000, "FL350")]
    [InlineData(0, "SFC")]
    [InlineData(9500, "FL095")]
    public void Level_text_is_a_flight_level_or_surface(double feet, string expected) =>
        Assert.Equal(expected, SigmetPresentation.LevelText(feet, "?"));

    [Fact]
    public void Missing_limits_use_the_given_wording()
    {
        Assert.Equal("illimité", SigmetPresentation.LevelText(null, "illimité"));
        Assert.Equal("SFC → FL350", SigmetPresentation.LevelRange(Make(baseFt: null, topFt: 35000)));
        Assert.Equal("FL150 → illimité", SigmetPresentation.LevelRange(Make(baseFt: 15000, topFt: null)));
    }

    [Fact]
    public void Map_label_shows_the_hazard_and_the_vertical_extent()
    {
        Assert.Equal("TS FL350", SigmetPresentation.LabelText(Make()));
        Assert.Equal("TURB FL150-FL280", SigmetPresentation.LabelText(Make("TURB", 15000, 28000)));
        Assert.Equal("VA", SigmetPresentation.LabelText(Make("VA", null, null)));
    }

    [Fact]
    public void Validity_text_gives_utc_times_and_the_time_left_or_to_wait()
    {
        Assert.Equal("05:00 → 07:36 UTC, reste 1 h 36", SigmetPresentation.ValidityText(Make(), Now));
        Assert.Equal("06:30 → 07:36 UTC, commence dans 30 min", SigmetPresentation.ValidityText(Make(fromMin: 30), Now));
        Assert.EndsWith("expiré", SigmetPresentation.ValidityText(Make(toMin: -5), Now));
        Assert.Contains("05/11", SigmetPresentation.ValidityText(Make(toMin: 24 * 60), Now));   // fin le lendemain : la date est précisée
    }

    [Fact]
    public void Raw_text_is_one_line_and_shortened()
    {
        Assert.Equal("WSFR34 LFPW LFMM SIGMET T02", SigmetPresentation.RawOneLine("WSFR34 LFPW\nLFMM SIGMET T02"));
        string text = SigmetPresentation.RawOneLine(new string('A', 500), 100);
        Assert.Equal(101, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Tip_text_has_the_phenomenon_as_title_then_fir_levels_validity_and_message()
    {
        var (title, lines) = SigmetPresentation.TipText(Make(), Now);

        Assert.Equal("Orages noyés dans les nuages", title);
        Assert.Contains("LFMM MARSEILLE · série T02", lines[0]);
        Assert.Equal("Niveaux : SFC → FL350", lines[1]);
        Assert.StartsWith("Validité : 05:00", lines[2]);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void Select_keeps_only_valid_messages_and_optionally_those_at_the_given_altitude()
    {
        var active = Make();
        var future = Make(fromMin: 30);
        var expired = Make(toMin: -1);
        var high = Make("TURB", 30000, 40000);
        var all = new[] { active, future, expired, high };

        var now = SigmetPresentation.Select(all, Now);
        Assert.Equal([active, high], now);

        Assert.Equal([active], SigmetPresentation.Select(all, Now, 5000));
        Assert.Equal([active, high], SigmetPresentation.Select(all, Now, 33000));
        Assert.Equal([high], SigmetPresentation.Select(all, Now, 38000));
    }

    [Fact]
    public void At_finds_the_messages_covering_a_point()
    {
        var s = Make();
        Assert.Single(SigmetPresentation.At([s], 42.5, 5));
        Assert.Empty(SigmetPresentation.At([s], 50, 5));
    }

    [Fact]
    public void DrawRing_closes_the_contour_and_keeps_longitudes_continuous_across_the_antimeridian()
    {
        IReadOnlyList<(double, double)> ring = [(60, 170), (60, 190), (70, 190), (70, 170)];

        var drawn = SigmetPresentation.DrawRing(ring);

        Assert.Equal(drawn[0], drawn[^1]);                                            // fermé
        Assert.InRange(drawn.Take(drawn.Count - 1).Average(p => p.Lon), -180, 180);                       // centre sur la carte
        for (int i = 1; i < drawn.Count; i++) Assert.True(Math.Abs(drawn[i].Lon - drawn[i - 1].Lon) <= 180);   // pas de ruban autour du monde
    }

    [Fact]
    public void DrawRing_shifts_a_zone_lying_entirely_beyond_180_degrees_back_onto_the_map()
    {
        IReadOnlyList<(double, double)> ring = [(65, 185), (65, 190), (68, 190), (68, 185)];
        var drawn = SigmetPresentation.DrawRing(ring);
        Assert.All(drawn, p => Assert.InRange(p.Lon, -175.1, -169.9));
    }

    [Fact]
    public void Label_point_lies_inside_the_drawn_area()
    {
        var (lat, lon) = SigmetPresentation.LabelPoint(Make().Areas[0]);
        Assert.InRange(lat, 41.75, 43.75);
        Assert.InRange(lon, 2.75, 7.75);
    }
}
