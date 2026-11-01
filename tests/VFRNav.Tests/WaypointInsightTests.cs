using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class WaypointInsightTests
{
    private static Airspace Box(string name, string cls, double lowerFt, double upperFt, AirspaceKind kind = AirspaceKind.Tma) =>
        new(name, cls, "x", "y", [new List<(double, double)> { (2, 48), (3, 48), (3, 49), (2, 49), (2, 48) }], kind, lowerFt, upperFt);

    private static Waypoint Wp(string name, double lat, double lon) => new() { Name = name, Lat = lat, Lon = lon };

    [Fact]
    public void Notes_are_kept_in_the_route_file_and_old_files_still_load()
    {
        var route = new List<Waypoint> { new() { Name = "WP1", Lat = 48, Lon = 2, Notes = "Passer à la verticale de l'étang, 2 500 ft" } };
        var back = RouteFile.Deserialize(RouteFile.Serialize(route));
        Assert.Equal("Passer à la verticale de l'étang, 2 500 ft", back[0].Notes);
        Assert.True(back[0].HasNotes);
        Assert.Equal("📝", back[0].NoteMark);

        var old = RouteFile.Deserialize("""[{"Name":"A","Lat":48.0,"Lon":2.0}]""");
        Assert.Null(old[0].Notes);
        Assert.False(old[0].HasNotes);
        Assert.Equal("", old[0].NoteMark);
        Assert.DoesNotContain("HasNotes", RouteFile.Serialize(route)); // les propriétés calculées ne sont pas enregistrées
    }

    [Fact]
    public void Blank_notes_do_not_count()
    {
        Assert.False(new Waypoint { Notes = "   " }.HasNotes);
    }

    [Fact]
    public void ZonesAt_lists_zones_containing_the_point_inside_first_and_ignores_high_ones()
    {
        var inside = Box("DANS", "D", 0, 6500);
        var below = Box("PLANCHER", "E", 3000, 6500);          // plancher 500 ft au-dessus de la croisière
        var tooHigh = Box("HAUTE", "C", 9000, 12000);
        var elsewhere = new Airspace("AILLEURS", "D", "x", "y", [new List<(double, double)> { (10, 50), (11, 50), (11, 51), (10, 51), (10, 50) }], AirspaceKind.Tma, 0, 5000);

        var found = WaypointInsight.ZonesAt(48.5, 2.5, [below, tooHigh, elsewhere, inside], altitudeFt: 2500);

        Assert.Equal(["DANS", "PLANCHER"], found.Select(z => z.Zone.Name).ToArray());
        Assert.Equal(VerticalStatus.Inside, found[0].Status);
        Assert.Equal(VerticalStatus.Below, found[1].Status);
    }

    [Fact]
    public void Nearest_navaids_and_airports_are_sorted_with_distance_and_bearing()
    {
        Navaid N(string id, double lat, double lon) => new(id, id, NavaidKind.Vor, "VOR", 113500, lat, lon, 0);
        var navs = WaypointInsight.NearestNavaids(48.0, 2.0, [N("LOIN", 49.5, 2.0), N("NORD", 48.1, 2.0), N("EST", 48.0, 2.2)], count: 2);
        Assert.Equal(["NORD", "EST"], navs.Select(n => n.Navaid.Ident).ToArray()); // du plus proche au plus lointain
        var north = navs.Single(n => n.Navaid.Ident == "NORD");
        Assert.Equal(6, north.DistanceNm, 0);
        Assert.Equal(0, north.BearingTrue, 0);
        Assert.DoesNotContain(navs, n => n.Navaid.Ident == "LOIN"); // plus de 40 NM

        var apts = WaypointInsight.NearestAirports(48.0, 2.0, [new Airport("A", "a", "small_airport", 48.0, 2.1, 0), new Airport("B", "b", "small_airport", 48.0, 2.05, 0)], count: 1);
        Assert.Equal("B", Assert.Single(apts).Airport.Ident);
        Assert.Equal("2,0 NM au 090°", WaypointInsight.Relative(apts[0].DistanceNm, apts[0].BearingTrue));
    }

    [Fact]
    public void RouteCheck_flags_class_A_restricted_zones_and_low_terrain()
    {
        var route = new[] { Wp("A", 48, 2), Wp("B", 48.5, 2.5), Wp("C", 49, 3) };
        var crossings = new List<AirspaceCrossing>
        {
            new(Box("TMA PARIS 3", "A", 2000, 3500), 3, 20, VerticalStatus.Inside),
            new(Box("LF-R324", "", 0, 3000, AirspaceKind.Restricted), 1, 8, VerticalStatus.Inside),
            new(Box("CTR X", "D", 0, 4000, AirspaceKind.Ctr), 5, 9, VerticalStatus.Inside),
            new(Box("HAUT", "E", 6000, 9000), 5, 9, VerticalStatus.Below)
        };
        var terrain = new TerrainProfile([new(0, 48, 2, 500, 0), new(10, 48.2, 2.2, 2300, 0), new(30, 49, 3, 400, 1)]);

        var lines = WaypointInsight.RouteCheck(route, 2500, crossings, terrain);

        Assert.Contains(lines, l => l.Contains("Classe A") && l.Contains("TMA PARIS 3"));
        Assert.Contains(lines, l => l.Contains("réglementée") && l.Contains("LF-R324"));
        Assert.Contains(lines, l => l.StartsWith("1 autre(s) zone(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("A → B") && l.Contains("2300"));
        Assert.DoesNotContain(lines, l => l.StartsWith("✔", StringComparison.Ordinal));
    }

    [Fact]
    public void RouteCheck_does_not_claim_all_clear_when_zones_are_not_loaded()
    {
        var route = new[] { Wp("A", 48, 2), Wp("B", 49, 3) };
        var lines = WaypointInsight.RouteCheck(route, 3500, [], null, zonesLoaded: false);
        Assert.Single(lines);
        Assert.Contains("non chargées", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("✔", StringComparison.Ordinal));
    }

    [Fact]
    public void RouteCheck_reports_all_clear_and_stays_empty_for_a_single_point()
    {
        var route = new[] { Wp("A", 48, 2), Wp("B", 49, 3) };
        var terrain = new TerrainProfile([new(0, 48, 2, 300, 0), new(60, 49, 3, 500, 0)]);
        var ok = WaypointInsight.RouteCheck(route, 3500, [], terrain);
        Assert.Equal(2, ok.Count);
        Assert.All(ok, l => Assert.StartsWith("✔", l));
        Assert.Empty(WaypointInsight.RouteCheck([Wp("A", 48, 2)], 3500, [], terrain));
    }
}
