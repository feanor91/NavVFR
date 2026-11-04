using System.Net;
using System.Text.Json;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class WorldAirportsTests
{
    private const string AirportsCsv =
        "id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,continent,iso_country,iso_region,municipality,scheduled_service,gps_code,iata_code\n" +
        "1,LFPG,large_airport,Paris Charles de Gaulle,49.0097,2.5479,392,EU,FR,FR-J,Paris,yes,LFPG,CDG\n" +
        "2,KJFK,large_airport,John F Kennedy Intl,40.6398,-73.7789,13,NA,US,US-NY,New York,yes,KJFK,JFK\n" +
        "3,LFPL,small_airport,Lognes,48.8,2.63,300,EU,FR,FR-J,Lognes,no,LFPL,\n" +
        "4,LFHP,heliport,Heli,45,3,500,EU,FR,FR-V,X,no,,\n" +
        "5,XX-0001,closed,Closed,45,3,500,EU,FR,FR-V,X,no,,\n" +
        "6,EGLL,large_airport,\"London, Heathrow\",51.47,-0.4543,83,EU,GB,GB-ENG,London,yes,EGLL,LHR\n";

    private const string RunwaysCsv =
        "id,airport_ref,airport_ident,length_ft,width_ft,surface,lighted,closed\n" +
        "1,1,LFPG,13829,148,ASP,1,0\n" +
        "2,1,LFPG,9000,148,ASP,1,1\n" +
        "3,3,LFPL,2600,80,GRS,0,0\n" +
        "4,3,LFPL,2000,60,CON,0,0\n" +
        "5,2,KJFK,14511,200,ASPH-G,1,0\n";

    private static Airport Apt(string ident, double lat, double lon, string type = "small_airport", bool sched = false, int? paved = null, int? longest = null) =>
        new(ident, ident, type, lat, lon, 0, Scheduled: sched, LongestRunwayFt: longest ?? paved, LongestPavedRunwayFt: paved);

    [Fact]
    public void ParseWorld_keeps_airplane_fields()
    {
        var list = OurAirportsService.ParseWorld(new StringReader(AirportsCsv));
        Assert.Equal(["LFPG", "KJFK", "LFPL", "EGLL"], list.Select(a => a.Ident));
        var cdg = list[0];
        Assert.Equal("CDG", cdg.Iata);
        Assert.Equal("FR", cdg.Country);
        Assert.Equal("EU", cdg.Continent);
        Assert.True(cdg.Scheduled);
        Assert.Null(list[2].Iata);
        Assert.Equal("London, Heathrow", list[3].Name);
    }

    [Fact]
    public void ParseRunways_ignores_closed_and_tracks_paved()
    {
        var map = OurAirportsService.ParseRunways(new StringReader(RunwaysCsv));
        Assert.Equal((13829, 13829), map["LFPG"]);
        Assert.Equal((2600, 2000), map["LFPL"]);
        Assert.Equal((14511, 14511), map["KJFK"]);
    }

    [Theory]
    [InlineData("ASP", true)]
    [InlineData("CON", true)]
    [InlineData("ASPH-G", true)]
    [InlineData("GRS", false)]
    [InlineData("", false)]
    public void IsPaved(string surface, bool expected) => Assert.Equal(expected, OurAirportsService.IsPaved(surface));

    [Fact]
    public void WithRunways_fills_known_airports_only()
    {
        var list = OurAirportsService.ParseWorld(new StringReader(AirportsCsv));
        var merged = OurAirportsService.WithRunways(list, OurAirportsService.ParseRunways(new StringReader(RunwaysCsv)));
        Assert.Equal(13829, merged.First(a => a.Ident == "LFPG").LongestRunwayFt);
        Assert.Null(merged.First(a => a.Ident == "EGLL").LongestRunwayFt);
    }

    [Fact]
    public void Filter_runway_length_and_paved()
    {
        var grass = Apt("A", 0, 0, longest: 4000);
        var paved = Apt("B", 0, 0, paved: 3500);
        var unknown = new Airport("C", "C", "small_airport", 0, 0, 0);
        Assert.True(new AirportFilter(MinRunwayFt: 3000).Matches(grass));
        Assert.False(new AirportFilter(MinRunwayFt: 3000, PavedOnly: true).Matches(grass));
        Assert.True(new AirportFilter(MinRunwayFt: 3000, PavedOnly: true).Matches(paved));
        Assert.False(new AirportFilter(MinRunwayFt: 1).Matches(unknown));
        Assert.True(new AirportFilter().Matches(unknown));
    }

    [Fact]
    public void Filter_country_continent_scheduled_iata()
    {
        var cdg = new Airport("LFPG", "CDG", "large_airport", 49, 2, 0, Iata: "CDG", Country: "FR", Continent: "EU", Scheduled: true);
        var small = new Airport("LFPL", "L", "small_airport", 48, 2, 0, Country: "FR", Continent: "EU");
        Assert.True(new AirportFilter(Countries: ["FR"]).Matches(cdg));
        Assert.False(new AirportFilter(Countries: ["GB"]).Matches(cdg));
        Assert.False(new AirportFilter(Continent: "NA").Matches(cdg));
        Assert.False(new AirportFilter(Countries: ["FR"], Continent: "NA").Matches(cdg));
        Assert.True(new AirportFilter(ScheduledOnly: true, IataOnly: true).Matches(cdg));
        Assert.False(new AirportFilter(ScheduledOnly: true).Matches(small));
        Assert.False(new AirportFilter(IataOnly: true).Matches(small));
    }

    [Fact]
    public void ParseCountries_normalizes()
    {
        Assert.Equal(["FR", "GB", "DE"], AirportFilter.ParseCountries("fr, gb ; de fr xyz 1a"));
        Assert.Empty(AirportFilter.ParseCountries(null));
    }

    [Fact]
    public void Old_json_settings_still_load()
    {
        var f = JsonSerializer.Deserialize<AirportFilter>("{\"WithCode\":true,\"WithoutCode\":false,\"VacOnly\":true,\"Large\":true,\"Medium\":false,\"Small\":true}");
        Assert.NotNull(f);
        Assert.False(f!.WithoutCode);
        Assert.Equal(0, f.MinRunwayFt);
        Assert.Null(f.Countries);
    }

    private static readonly GeoBounds World = new(-80, -180, 80, 180);

    [Fact]
    public void Select_keeps_best_per_cell_and_ranks()
    {
        var big = Apt("BIG", 48.0, 2.0, "large_airport", sched: true);
        var tiny = Apt("TINY", 48.001, 2.001);
        var far = Apt("FAR", 10, 10);
        var sel = AirportDisplay.Select([tiny, big, far], World, cellMeters: 50_000);
        Assert.Equal(["BIG", "FAR"], sel.Shown.Select(a => a.Ident).OrderBy(x => x));
        Assert.Equal(3, sel.InView);
        Assert.Equal(1, sel.Hidden);
    }

    [Fact]
    public void Select_cap_prefers_important_airports()
    {
        var list = new List<Airport>
        {
            Apt("S1", 0, 0), Apt("S2", 5, 5), Apt("L", 10, 10, "large_airport", sched: true), Apt("M", 15, 15, "medium_airport"),
        };
        var sel = AirportDisplay.Select(list, World, 0, cap: 2);
        Assert.Equal(["L", "M"], sel.Shown.Select(a => a.Ident));
        Assert.Equal(2, sel.Hidden);
    }

    [Fact]
    public void Select_pinned_always_shown_and_not_hidden()
    {
        var pin = Apt("PIN", 1, 1);
        var others = Enumerable.Range(0, 5).Select(i => Apt("O" + i, 20 + i, 20 + i, "medium_airport")).ToList();
        var sel = AirportDisplay.Select(others, World, 0, cap: 3, pinned: [pin]);
        Assert.Contains(sel.Shown, a => a.Ident == "PIN");
        Assert.Equal(3, sel.Shown.Count);
        Assert.Equal(6, sel.InView);
        Assert.Equal(3, sel.Hidden);
    }

    [Fact]
    public void Select_culls_outside_view()
    {
        var sel = AirportDisplay.Select([Apt("IN", 48, 2), Apt("OUT", -30, 100)], new GeoBounds(40, -5, 55, 10), 0);
        Assert.Equal(["IN"], sel.Shown.Select(a => a.Ident));
        Assert.Equal(1, sel.InView);
    }

    [Fact]
    public void NearRoute_keeps_airports_along_the_route()
    {
        var route = new List<Waypoint> { new() { Lat = 48, Lon = 0 }, new() { Lat = 48, Lon = 4 } };
        var onRoute = Apt("ON", 48.05, 2);
        var off = Apt("OFF", 50, 2);
        var near = AirportDisplay.NearRoute([onRoute, off], route, 20);
        Assert.Equal(["ON"], near.Select(a => a.Ident));
    }

    [Fact]
    public void NearRoute_without_route_keeps_everything()
    {
        Assert.Equal(2, AirportDisplay.NearRoute([Apt("A", 0, 0), Apt("B", 1, 1)], [], 20).Count);
    }

    [Fact]
    public void Search_by_code_iata_name_and_city_with_accents()
    {
        var index = new AirportSearchIndex(
        [
            new Airport("LFPG", "Paris Charles de Gaulle", "large_airport", 49, 2, 0, Iata: "CDG", City: "Roissy", Scheduled: true),
            new Airport("LFPL", "Lognes Émerainville", "small_airport", 48.8, 2.6, 0),
            new Airport("EGLL", "London Heathrow", "large_airport", 51, 0, 0, Iata: "LHR", City: "London"),
        ]);
        Assert.Equal("LFPG", index.Find("lfpg")[0].Ident);
        Assert.Equal("LFPG", index.Find("cdg")[0].Ident);
        Assert.Equal("EGLL", index.Find("heathrow")[0].Ident);
        Assert.Equal("LFPL", index.Find("emerainville")[0].Ident);
        Assert.Equal("LFPG", index.Find("roissy")[0].Ident);
        Assert.Empty(index.Find(""));
        Assert.Empty(index.Find("zzzz"));
    }

    // ---- cache ----

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request.RequestUri.ToString()));
        }
    }

    private static HttpResponseMessage Ok(string csv) => new(HttpStatusCode.OK) { Content = new StringContent(csv) };

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "navvfr-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_downloads_then_serves_fresh_cache_without_network()
    {
        string dir = TempDir();
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = new Handler(u => Ok(u.EndsWith("runways.csv") ? RunwaysCsv : AirportsCsv));
            var store = new AirportDataStore(new HttpClient(handler), dir, clock);

            var first = await store.LoadAsync();
            Assert.False(first.FromCache);
            Assert.Equal(4, first.Airports.Count);
            Assert.Equal(13829, first.Airports.First(a => a.Ident == "LFPG").LongestRunwayFt);
            Assert.Equal(2, handler.Requests.Count);

            clock.Now = clock.Now.AddDays(3);
            var second = await store.LoadAsync();
            Assert.True(second.FromCache);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Store_refreshes_stale_cache_and_falls_back_offline()
    {
        string dir = TempDir();
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            bool online = true;
            var handler = new Handler(u => online ? Ok(u.EndsWith("runways.csv") ? RunwaysCsv : AirportsCsv) : throw new HttpRequestException("hors ligne"));
            var store = new AirportDataStore(new HttpClient(handler), dir, clock);
            await store.LoadAsync();

            clock.Now = clock.Now.AddDays(10);
            online = false;
            var offline = await store.LoadAsync();
            Assert.True(offline.FromCache);
            Assert.Equal(4, offline.Airports.Count);
            Assert.Contains("hors ligne", offline.Note);

            online = true;
            int before = handler.Requests.Count;
            var refreshed = await store.LoadAsync();
            Assert.False(refreshed.FromCache);
            Assert.True(handler.Requests.Count > before);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Store_without_cache_and_offline_throws()
    {
        string dir = TempDir();
        try
        {
            var store = new AirportDataStore(new HttpClient(new Handler(_ => throw new HttpRequestException("hors ligne"))), dir);
            await Assert.ThrowsAsync<HttpRequestException>(() => store.LoadAsync());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Store_survives_runway_download_failure()
    {
        string dir = TempDir();
        try
        {
            var handler = new Handler(u => u.EndsWith("runways.csv") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok(AirportsCsv));
            var data = await new AirportDataStore(new HttpClient(handler), dir).LoadAsync();
            Assert.Equal(4, data.Airports.Count);
            Assert.All(data.Airports, a => Assert.Null(a.LongestRunwayFt));
            Assert.NotNull(data.Note);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
