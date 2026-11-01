using System.Net;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class ObstacleTests
{
    private const string Sample = """
    {"version":0.6,"elements":[
      {"type":"node","id":1,"lat":48.05,"lon":2.5,"tags":{"man_made":"mast","height":"59.6","name":"Mât A"}},
      {"type":"node","id":2,"lat":48.03,"lon":2.3,"tags":{"man_made":"chimney"}},
      {"type":"node","id":3,"lat":48.4,"lon":2.5,"tags":{"man_made":"mast","height":"80"}},
      {"type":"node","id":4,"lat":48.02,"lon":2.7,"tags":{"man_made":"tower","highway":"speed_camera","maxspeed":"110"}},
      {"type":"node","id":5,"lat":48.01,"lon":2.4,"tags":{"power":"generator","generator:source":"wind","height":"120 m"}},
      {"type":"node","id":6,"lat":48.0,"lon":2.6,"tags":{"man_made":"mast","disused":"yes","height":"40"}},
      {"type":"way","id":7,"nodes":[1,2]}
    ]}
    """;

    private static Waypoint Wp(string name, double lat, double lon) => new() { Name = name, Lat = lat, Lon = lon };
    private static readonly Waypoint A = Wp("A", 48.0, 2.0);
    private static readonly Waypoint B = Wp("B", 48.0, 3.0);

    [Fact]
    public void Parse_keeps_masts_chimneys_and_wind_turbines_and_drops_cameras_and_disused()
    {
        var list = ObstacleService.Parse(Sample);
        Assert.Equal([1L, 2L, 3L, 5L], list.Select(o => o.Id).ToArray());
        Assert.Equal(ObstacleKind.WindTurbine, list.Single(o => o.Id == 5).Kind);
        Assert.Equal(59.6, list.Single(o => o.Id == 1).HeightM);
        Assert.Null(list.Single(o => o.Id == 2).HeightM);
        Assert.Equal("Mât A", list.Single(o => o.Id == 1).Name);
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("45.5", 45.5)]
    [InlineData("45,5", 45.5)]
    [InlineData("120 m", 120)]
    [InlineData("100 ft", 30.48)]
    [InlineData("100'", 30.48)]
    public void Height_formats(string text, double expected) => Assert.Equal(expected, ObstacleService.ParseHeightMetres(text)!.Value, 2);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("grand")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Unusable_heights_are_unknown_not_guessed(string? text) => Assert.Null(ObstacleService.ParseHeightMetres(text));

    [Fact]
    public void Near_keeps_only_the_corridor_and_orders_along_the_route()
    {
        var all = ObstacleService.Parse(Sample);
        var near = ObstacleService.Near([A, B], all, corridorNm: 3);
        Assert.Equal([2L, 5L, 1L], near.Select(n => n.Obstacle.Id).ToArray()); // 2 (lon 2.3), 5 (2.4), 1 (2.5) ; l'id 3 est à 24 NM
        Assert.All(near, n => Assert.InRange(n.OffsetNm, 0, 3));
        Assert.True(near[0].AlongNm < near[1].AlongNm && near[1].AlongNm < near[2].AlongNm);
    }

    private static RouteObstacle Site(long id, double lat, double lon, double? height, double along = 5) =>
        new(new Obstacle(id, ObstacleKind.Mast, $"m{id}", lat, lon, height), along, 0.5, null, null);

    [Fact]
    public void Merge_keeps_the_tallest_of_antennas_on_the_same_site()
    {
        var merged = ObstacleService.Merge(
        [
            Site(1, 48.0000, 2.0000, 20, along: 4),
            Site(2, 48.0003, 2.0002, 45, along: 4.1),   // même pylône, plus haut
            Site(3, 48.0002, 2.0001, null, along: 4.1), // même pylône, sans hauteur : écarté
            Site(4, 48.0500, 2.0500, 30, along: 9)      // autre site
        ], ObstacleService.MergeRadiusNm);

        Assert.Equal([2L, 4L], merged.Select(m => m.Obstacle.Id).ToArray());
        Assert.True(merged[0].AlongNm < merged[1].AlongNm);
    }

    [Fact]
    public void Merge_keeps_an_unknown_height_obstacle_when_alone()
    {
        var merged = ObstacleService.Merge([Site(1, 48.0, 2.0, null), Site(2, 48.1, 2.1, 30)], ObstacleService.MergeRadiusNm);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Margin_is_cruise_minus_top_and_unknown_when_height_is_unknown()
    {
        var known = new RouteObstacle(new Obstacle(1, ObstacleKind.Mast, "", 48, 2, 50), 5, 0.5, 400, 564);
        Assert.Equal(1936, known.MarginFt(2500));
        var unknown = new RouteObstacle(new Obstacle(2, ObstacleKind.Mast, "", 48, 2, null), 5, 0.5, 400, null);
        Assert.Null(unknown.MarginFt(2500));
    }

    [Fact]
    public void Boxes_cover_the_route_with_the_corridor_and_split_long_legs()
    {
        var boxes = ObstacleService.BuildBoxes([A, Wp("far", 48.0, 4.0)], corridorNm: 2);
        Assert.True(boxes.Count >= 2); // environ 80 NM : coupé par pas de 30 NM
        Assert.True(boxes.Min(b => b.South) < 48.0 && boxes.Max(b => b.North) > 48.0);
        Assert.True(boxes.Min(b => b.West) < 2.0 && boxes.Max(b => b.East) > 4.0);
    }

    [Fact]
    public void Query_asks_for_masts_towers_chimneys_and_wind_turbines_in_the_box()
    {
        string q = ObstacleService.BuildQuery([(48.0, 2.0, 48.5, 2.5), (49.0, 3.0, 49.5, 3.5)]);
        Assert.Contains("mast|tower|chimney|communications_tower", q);
        Assert.Contains("generator:source", q);
        Assert.Contains("(48.0000,2.0000,48.5000,2.5000)", q);
        Assert.Contains("(49.0000,3.0000,49.5000,3.5000)", q);
        Assert.StartsWith("[out:json]", q);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int OverpassCalls { get; private set; }
        public int ElevationCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("open-meteo"))
            {
                ElevationCalls++;
                int n = request.RequestUri.Query.Split("latitude=")[1].Split('&')[0].Split(',').Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"elevation\":[" + string.Join(",", Enumerable.Repeat("100", n)) + "]}") };
            }
            OverpassCalls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("data=", await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sample) };
        }
    }

    [Fact]
    public async Task FindAsync_adds_ground_elevation_and_top_altitude()
    {
        var handler = new FakeHandler();
        var client = new HttpClient(handler);
        var found = await new ObstacleService(client).FindAsync([A, B], new TerrainService(client), corridorNm: 3);

        Assert.Equal([2L, 5L, 1L], found.Select(f => f.Obstacle.Id).Distinct().ToArray());
        var mast = found.First(f => f.Obstacle.Id == 1);
        Assert.Equal(328.084, mast.GroundFt!.Value, 2);                       // 100 m de sol
        Assert.Equal(328.084 + 59.6 * 3.28084, mast.TopFt!.Value, 2);          // + hauteur du mât
        Assert.Null(found.First(f => f.Obstacle.Id == 2).TopFt);              // cheminée sans hauteur
        Assert.True(handler.ElevationCalls >= 1);
        Assert.Equal(1, handler.OverpassCalls); // une seule requête pour toute la route
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") });
    }

    [Fact]
    public async Task Overpass_errors_are_reported_clearly()
    {
        var client = new HttpClient(new FailingHandler());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new ObstacleService(client).FindAsync([A, B], new TerrainService(client)));
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task Too_long_a_route_is_refused_before_any_request()
    {
        var client = new HttpClient(new FailingHandler());
        var route = new[] { A, Wp("very far", 48.0, 35.0) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ObstacleService(client).FindAsync(route, new TerrainService(client)));
    }
}
