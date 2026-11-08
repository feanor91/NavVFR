using System.Text;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class CountriesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfrnav-asp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Country_codes_are_read_from_a_free_text_without_duplicates_or_junk()
    {
        Assert.Equal(["FR", "GB", "BE"], RouteCountries.Parse(" fr, GB;be  fr x FRA 1 "));
        Assert.Empty(RouteCountries.Parse(null));
        Assert.Empty(RouteCountries.Parse("France"));
        Assert.Equal(["FR", "GB", "DE"], RouteCountries.Wanted(["FR", "GB"], ["gb", "DE"]));
    }

    [Fact]
    public void Europe_expands_to_the_european_countries_without_duplicates()
    {
        var all = RouteCountries.Parse("fr europe GB");
        Assert.Equal("FR", all[0]);
        Assert.Contains("DE", all);
        Assert.Contains("GB", all);
        Assert.Contains("NO", all);
        Assert.DoesNotContain("RU", all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(RouteCountries.Europe.Count, RouteCountries.Parse("EUROPE").Count);
        Assert.True(RouteCountries.Europe.All(c => c.Length == 2));
    }

    [Fact]
    public void The_airport_filter_countries_understand_europe_too()
    {
        Assert.Equal(["IT", "BE"], AirportFilter.ParseCountries("it be"));
        Assert.Equal(RouteCountries.Europe.Count, AirportFilter.ParseCountries("EUROPE").Count);
    }

    private static Airport Apt(string ident, double lat, double lon, string country) =>
        new(ident, ident, "small_airport", lat, lon, 0, Country: country);

    [Fact]
    public void Countries_along_a_route_are_those_of_the_nearest_airports_and_the_sea_adds_nothing()
    {
        var airports = new List<Airport>
        {
            Apt("LFPG", 49.0, 2.5, "FR"), Apt("LFQQ", 50.6, 3.1, "FR"), Apt("EGLL", 51.47, -0.46, "GB"), Apt("EBBR", 50.9, 4.5, "BE"),
        };
        var route = new List<Waypoint>
        {
            new() { Name = "A", Lat = 49.0, Lon = 2.5 },     // France
            new() { Name = "B", Lat = 51.47, Lon = -0.46 },  // Royaume-Uni
        };

        var countries = RouteCountries.Along(route, airports);
        Assert.Contains("FR", countries);
        Assert.Contains("GB", countries);
        Assert.DoesNotContain("BE", countries);   // loin de la route

        var atSea = new List<Waypoint> { new() { Name = "M", Lat = 45.0, Lon = -20.0 }, new() { Name = "N", Lat = 45.5, Lon = -20.0 } };
        Assert.Empty(RouteCountries.Along(atSea, airports));
        Assert.Empty(RouteCountries.Along([], airports));
    }

    [Fact]
    public void Navaids_can_be_read_for_several_countries_at_once()
    {
        const string csv =
            "\"id\",\"filename\",\"ident\",\"name\",\"type\",\"frequency_khz\",\"latitude_deg\",\"longitude_deg\",\"elevation_ft\",\"iso_country\"\n" +
            "1,\"a\",\"CGN\",\"Coligny\",\"VOR-DME\",113500,46.4,5.3,800,\"FR\"\n" +
            "4,\"d\",\"LON\",\"London\",\"VOR\",113600,51.5,-0.1,100,\"GB\"\n" +
            "5,\"e\",\"BRU\",\"Bruxelles\",\"VOR\",114600,50.9,4.5,100,\"BE\"\n";
        Assert.Equal(["CGN", "LON"], OurAirportsService.ParseNavaids(new StringReader(csv), ["FR", "gb"]).Select(n => n.Ident));
        Assert.Single(OurAirportsService.ParseNavaids(new StringReader(csv), "BE"));
    }

    private sealed class FakeOpenAip : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            const string json = """
                {"totalPages":1,"items":[{"name":"TMA TEST","icaoClass":3,"type":7,"lowerLimit":{"value":1500,"unit":1,"referenceDatum":1},
                 "upperLimit":{"value":6000,"unit":1,"referenceDatum":1},
                 "geometry":{"type":"Polygon","coordinates":[[[2,48],[3,48],[3,49],[2,49],[2,48]]]}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Airspaces_of_a_country_are_cached_on_disk_and_read_back_without_a_request()
    {
        var handler = new FakeOpenAip();
        var service = new OpenAipService(new HttpClient(handler));

        var first = await service.LoadCachedAsync("FR", "clé", _dir, TimeSpan.FromDays(7));
        Assert.Single(first);
        Assert.Equal(1, handler.Requests);
        Assert.True(File.Exists(Path.Combine(_dir, "FR.json.gz")));

        var second = await service.LoadCachedAsync("FR", "clé", _dir, TimeSpan.FromDays(7));
        Assert.Single(second);
        Assert.Equal("TMA TEST", second[0].Name);
        Assert.Equal(1, handler.Requests);                    // relu du disque

        await service.LoadCachedAsync("FR", "clé", _dir, TimeSpan.FromDays(7), refresh: true);
        Assert.Equal(2, handler.Requests);                    // rechargement demandé

        File.SetLastWriteTimeUtc(Path.Combine(_dir, "FR.json.gz"), DateTime.UtcNow.AddDays(-8));
        await service.LoadCachedAsync("FR", "clé", _dir, TimeSpan.FromDays(7));
        Assert.Equal(3, handler.Requests);                    // cache trop vieux
    }

    [Fact]
    public async Task A_damaged_cache_file_is_replaced_by_a_fresh_download()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "GB.json.gz"), [1, 2, 3, 4]);
        var handler = new FakeOpenAip();
        var zones = await new OpenAipService(new HttpClient(handler)).LoadCachedAsync("GB", "clé", _dir, TimeSpan.FromDays(7));
        Assert.Single(zones);
        Assert.Equal(1, handler.Requests);
    }
}
