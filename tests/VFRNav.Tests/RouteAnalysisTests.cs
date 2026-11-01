using System.Net;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class RouteAnalysisTests
{
    private static Waypoint Wp(string name, double lat, double lon) => new() { Name = name, Lat = lat, Lon = lon };

    private static readonly Waypoint A = Wp("A", 48.0, 2.0);
    private static readonly Waypoint B = Wp("B", 48.0, 3.0);   // environ 40 NM vers l'est
    private static readonly Waypoint C = Wp("C", 48.5, 3.0);   // environ 30 NM vers le nord

    [Fact]
    public void Sample_covers_the_route_with_cumulative_distances_and_leg_numbers()
    {
        var pts = RouteGeometry.Sample([A, B, C], stepNm: 5);
        Assert.Equal(0, pts[0].DistanceNm);
        Assert.Equal(0, pts[0].LegIndex);
        Assert.Equal(1, pts[^1].LegIndex);
        double total = NavMath.DistanceNm(A, B) + NavMath.DistanceNm(B, C);
        Assert.Equal(total, pts[^1].DistanceNm, 3);
        Assert.Equal(pts.Count, pts.Select(p => p.DistanceNm).Distinct().Count()); // aucun doublon à la jonction
        Assert.True(pts.Zip(pts.Skip(1)).All(z => z.Second.DistanceNm > z.First.DistanceNm));
    }

    [Fact]
    public void Sample_widens_the_step_to_respect_the_point_limit()
    {
        var pts = RouteGeometry.Sample([A, Wp("far", 48.0, 20.0)], stepNm: 1, maxPoints: 50);
        Assert.InRange(pts.Count, 2, 52);
    }

    [Fact]
    public void Interpolate_midpoint_is_between_the_ends()
    {
        var (lat, lon) = RouteGeometry.Interpolate(48, 2, 48, 3, 0.5);
        Assert.Equal(2.5, lon, 2);
        Assert.InRange(lat, 48.0, 48.01);
    }

    [Fact]
    public void DistanceToSegment_is_perpendicular_inside_and_to_the_end_outside()
    {
        var (d, t) = RouteGeometry.DistanceToSegment(48.1, 2.5, A, B); // 6 NM au nord du milieu
        Assert.Equal(6, d, 0);
        Assert.Equal(0.5, t, 1);
        var (dEnd, tEnd) = RouteGeometry.DistanceToSegment(48.0, 4.0, A, B);
        Assert.Equal(1, tEnd, 6);
        Assert.Equal(NavMath.DistanceNm(B, Wp("p", 48.0, 4.0)), dEnd, 0);
    }

    private static Airspace Box(string name, double lat0, double lon0, double lat1, double lon1, double lowerFt, double upperFt) =>
        new(name, "D", "x", "y", [new List<(double, double)> { (lon0, lat0), (lon1, lat0), (lon1, lat1), (lon0, lat1), (lon0, lat0) }],
            AirspaceKind.Tma, lowerFt, upperFt);

    [Fact]
    public void Airspaces_crossed_report_entry_exit_and_vertical_status()
    {
        var samples = RouteGeometry.Sample([A, B], 1);
        var inside = Box("DANS", 47.9, 2.4, 48.1, 2.6, 0, 6500);          // route à 3000 ft : dedans
        var above = Box("AU-DESSUS", 47.9, 2.7, 48.1, 2.9, 0, 2000);     // route passe au-dessus du plafond
        var elsewhere = Box("AILLEURS", 50, 10, 51, 11, 0, 5000);
        var tooHigh = Box("HAUTE", 47.9, 2.2, 48.1, 2.3, 9000, 12000);    // plancher bien au-dessus de la croisière

        var found = RouteAnalysis.AirspacesAlong(samples, [elsewhere, tooHigh, above, inside], cruiseFt: 3000);

        Assert.Equal(["DANS", "AU-DESSUS"], found.Select(c => c.Zone.Name).ToArray());
        Assert.Equal(VerticalStatus.Inside, found[0].Status);
        Assert.Equal(VerticalStatus.Above, found[1].Status);
        Assert.True(found[0].EntryNm < found[0].ExitNm);
        Assert.True(found[0].ExitNm < found[1].EntryNm);
    }

    [Fact]
    public void Navaids_near_the_route_are_listed_in_route_order_within_the_limit()
    {
        Navaid N(string id, double lat, double lon) => new(id, id, NavaidKind.Vor, "VOR", 113500, lat, lon, 0);
        var found = RouteAnalysis.NavaidsAlong([A, B], [N("FAR", 49.5, 2.5), N("LATE", 48.05, 2.8), N("EARLY", 48.05, 2.2)], 15);
        Assert.Equal(["EARLY", "LATE"], found.Select(f => f.Navaid.Ident).ToArray());
        Assert.InRange(found[0].OffsetNm, 2.5, 3.5);
        Assert.True(found[0].AlongNm < found[1].AlongNm);
    }

    [Fact]
    public void Terrain_warnings_flag_legs_with_too_little_clearance()
    {
        var profile = new TerrainProfile(
        [
            new(0, 48, 2, 500, 0), new(10, 48, 2.2, 2800, 0), new(20, 48, 2.4, 600, 0),
            new(30, 48, 2.6, 400, 1), new(40, 48, 2.8, 900, 1)
        ]);
        Assert.Equal(2800, profile.MaxFt);
        Assert.Equal(10, profile.Highest!.DistanceNm);
        Assert.Equal(900, profile.LegMaxFt(1));
        var warnings = profile.Warnings(cruiseFt: 3500, marginFt: 1000);
        var w = Assert.Single(warnings);
        Assert.Equal(0, w.LegIndex);
        Assert.Equal(700, w.ClearanceFt);
        Assert.Contains(profile.Warnings(2000), x => x.ClearanceFt < 0);
    }

    private sealed class FakeElevationHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            int n = request.RequestUri!.Query.Split("latitude=")[1].Split('&')[0].Split(',').Length;
            BatchSizes.Add(n);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"elevation\":[" + string.Join(",", Enumerable.Repeat("100.0", n)) + "]}")
            });
        }
    }

    [Fact]
    public async Task Terrain_service_batches_by_100_converts_metres_to_feet_and_caches()
    {
        var handler = new FakeElevationHandler();
        var service = new TerrainService(new HttpClient(handler));
        var route = new[] { A, Wp("far", 48.0, 5.0) }; // plus de 100 points au pas de 1 NM

        var profile = await service.ProfileAsync(route);
        Assert.True(handler.BatchSizes.Count >= 2);
        Assert.All(handler.BatchSizes, n => Assert.True(n <= 100));
        Assert.All(profile.Samples, s => Assert.Equal(328.084, s.ElevationFt, 2));

        int calls = handler.Calls;
        await service.ProfileAsync(route);
        Assert.Equal(calls, handler.Calls); // tout vient du cache
    }
}
