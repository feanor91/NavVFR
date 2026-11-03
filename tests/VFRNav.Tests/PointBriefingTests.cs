using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class PointBriefingTests
{
    private static Waypoint Wp(string name, double lat, double lon, double? alt = null, string? icao = null, double elevation = 0) =>
        new() { Name = name, Lat = lat, Lon = lon, AltitudeFt = alt, Icao = icao, ElevationFt = elevation };

    private static Airspace Zone(string name, string cls, int type, double lowerFt, double upperFt, AirspaceKind kind = AirspaceKind.Tma, AirspaceDetails? details = null) =>
        new(name, cls, lowerFt == 0 ? "SFC" : $"{lowerFt:0}ft AMSL", $"{upperFt:0}ft AMSL",
            [new List<(double, double)> { (1, 48), (3, 48), (3, 50), (1, 50) }], kind, lowerFt, upperFt, type, details);

    private static readonly List<Waypoint> Route = [Wp("A", 49, 2), Wp("B", 49, 2.5), Wp("C", 49, 2.9)];

    [Fact]
    public void Altitude_profile_uses_the_entered_altitudes_and_interpolates_between_them()
    {
        var route = new List<Waypoint> { Wp("A", 49, 2, 1000), Wp("B", 49, 2.5), Wp("C", 49, 3, 3000) };
        var p = new AltitudeProfile(route, 2000);
        Assert.Equal(1000, p.AtWaypoint(0));
        Assert.Equal(2000, p.AtWaypoint(1));      // pas de valeur saisie : croisière
        Assert.Equal(3000, p.AtWaypoint(2));
        Assert.Equal(1500, p.AtDistance(p.DistanceAt(1) / 2), 1);
        Assert.Equal(3000, p.AtDistance(p.DistanceAt(2) + 50)); // au-delà de l'arrivée
        Assert.True(p.HasCustom);
        Assert.False(p.IsFlat);
        Assert.False(new AltitudeProfile(Route, 2500).HasCustom);
        Assert.True(new AltitudeProfile(Route, 2500).IsFlat);
    }

    [Fact]
    public void An_airport_without_entered_altitude_is_on_the_ground_and_a_typed_altitude_wins()
    {
        var route = new List<Waypoint> { Wp("LFPG", 49, 2, icao: "LFPG", elevation: 392), Wp("B", 49, 2.5), Wp("LFMN", 49, 3, icao: "LFMN", elevation: 12) };
        var p = new AltitudeProfile(route, 30000);
        Assert.Equal(392, p.AtWaypoint(0));
        Assert.Equal(12, p.AtWaypoint(2));
        // Le point intermédiaire monte depuis le sol à pente réaliste au lieu de sauter à la croisière.
        double expected = Math.Min(392 + AltitudeProfile.GradientFtPerNm * p.DistanceAt(1), 12 + AltitudeProfile.GradientFtPerNm * (p.DistanceAt(2) - p.DistanceAt(1)));
        Assert.Equal(expected, p.AtWaypoint(1), 3);
        Assert.InRange(p.AtWaypoint(1), 392, 29999);
        Assert.Equal(30000, new AltitudeProfile([Wp("A", 49, 2, 0), Wp("B", 49, 2, 30000)], 30000).AtWaypoint(1));

        route[0].AltitudeFt = 0;
        Assert.Equal(0, new AltitudeProfile(route, 30000).AtWaypoint(0));
    }

    [Fact]
    public void Crossing_status_follows_the_altitude_at_each_part_of_the_route()
    {
        var zone = Zone("TMA X", "A", 7, 2000, 3000);
        var samples = RouteGeometry.Sample(Route, 1);
        var flatLow = RouteAnalysis.AirspacesAlong(samples, [zone], new AltitudeProfile(Route, 1500));
        var flatIn = RouteAnalysis.AirspacesAlong(samples, [zone], new AltitudeProfile(Route, 2500));
        var climbing = RouteAnalysis.AirspacesAlong(samples, [zone], new AltitudeProfile([Wp("A", 49, 2, 1500), Wp("B", 49, 2.5), Wp("C", 49, 2.9, 2500)], 2000));
        Assert.Equal(VerticalStatus.Below, flatLow.Single().Status);
        Assert.Equal(VerticalStatus.Inside, flatIn.Single().Status);
        Assert.Equal(VerticalStatus.Inside, climbing.Single().Status); // la montée entre dans la zone
    }

    [Fact]
    public void Terrain_warning_uses_the_altitude_where_the_margin_is_smallest()
    {
        var terrain = new TerrainProfile([new(0, 49, 2, 500, 0), new(10, 49, 2.2, 2200, 0), new(20, 49, 2.5, 500, 0)]);
        var route = new List<Waypoint> { Wp("A", 49, 2, 3500), Wp("B", 49, 2.5, 1200) };
        var profile = new AltitudeProfile(route, 2500);
        var warnings = terrain.Warnings(profile, 1000);
        var w = Assert.Single(warnings);
        Assert.Equal(10, w.AtNm);
        Assert.True(w.ClearanceFt < 1000);
    }

    [Fact]
    public void Class_A_means_vfr_forbidden_and_asks_for_a_clearance_on_the_zone_frequency()
    {
        var zone = Zone("TMA PARIS 2", "A", 7, 1500, 2500, details: new AirspaceDetails(Frequencies: [new AirspaceFrequency("Paris Info", "124.350")]));
        var b = PointBriefings.For(Route, 1, 2000, [zone]);
        var z = Assert.Single(b.Zones);
        Assert.Equal(Severity.Forbidden, z.Severity);
        Assert.Contains("interdit", z.Verdict);
        Assert.Contains("Paris Info 124.350", z.Action);
        Assert.Equal(["Paris Info 124.350"], b.Frequencies);
        Assert.Equal(Severity.Forbidden, b.Worst);
    }

    [Theory]
    [InlineData("B", Severity.Clearance)]
    [InlineData("C", Severity.Clearance)]
    [InlineData("D", Severity.Clearance)]
    [InlineData("E", Severity.Free)]
    [InlineData("G", Severity.Free)]
    [InlineData("", Severity.Caution)]
    public void Controlled_zones_depend_on_their_class(string cls, Severity expected)
    {
        var zone = Zone("CTR X", cls, 4, 0, 3000, AirspaceKind.Ctr);
        Assert.Equal(expected, PointBriefings.For(Route, 1, 2000, [zone]).Zones.Single().Severity);
    }

    [Fact]
    public void Restricted_prohibited_rmz_and_fis_zones_get_their_own_advice()
    {
        var r = PointBriefings.Assess(Zone("R 1", "", 1, 0, 2500, AirspaceKind.Restricted))!;
        Assert.Equal(Severity.Caution, r.Severity);
        Assert.Contains("active", r.Action);
        Assert.Equal(Severity.Forbidden, PointBriefings.Assess(Zone("P 1", "", 3, 0, 2500, AirspaceKind.Restricted))!.Severity);
        var rmz = PointBriefings.Assess(Zone("RMZ L", "", 6, 0, 1500, AirspaceKind.Radio, new AirspaceDetails(Frequencies: [new AirspaceFrequency("Lognes Info", "120.000")])))!;
        Assert.Contains("Radio obligatoire", rmz.Verdict);
        Assert.Contains("Lognes Info 120.000", rmz.Action);
        var fis = PointBriefings.Assess(Zone("SIV PARIS SUD", "", 33, 0, 19500, AirspaceKind.Sector, new AirspaceDetails(Frequencies: [new AirspaceFrequency("Paris Info", "124.350")])))!;
        Assert.Equal(Severity.Info, fis.Severity);
        Assert.Null(PointBriefings.Assess(Zone("FIR", "", 10, 0, 19500, AirspaceKind.Other)));
    }

    [Fact]
    public void A_zone_just_above_the_point_is_mentioned_as_out_of_zone_with_the_margin()
    {
        var zone = Zone("TMA X", "A", 7, 2500, 3500);
        var route = new List<Waypoint> { Wp("A", 49, 2), Wp("B", 49, 2.5, 2000), Wp("C", 49, 2.9) };
        var z = PointBriefings.For(route, 1, 1800, [zone]).Zones.Single();
        Assert.Equal(VerticalStatus.Below, z.Status);
        Assert.Contains("sous le plancher", z.Verdict);
        Assert.Contains("500 ft", z.Action);
        Assert.Equal(Severity.Free, PointBriefings.For(route, 1, 1800, [zone]).Worst);
    }

    [Fact]
    public void Departure_and_arrival_aerodromes_use_the_circuit_altitude_unless_one_is_entered()
    {
        var route = new List<Waypoint> { Wp("LFPL", 49, 2, icao: "LFPL", elevation: 351), Wp("M", 49, 2.5), Wp("LFPN", 49, 2.9, 2000, "LFPN", 538) };
        Assert.Equal((1351.0, "circuit (terrain + 1 000 ft)"), PointBriefings.AltitudeFor(route, 0, 2500));
        Assert.Equal((2500.0, "croisière"), PointBriefings.AltitudeFor(route, 1, 2500));
        Assert.Equal((2000.0, "saisie"), PointBriefings.AltitudeFor(route, 2, 2500));
    }

    [Fact]
    public void Aerodrome_frequencies_come_first_and_zone_frequencies_are_not_repeated()
    {
        var zone = Zone("CTR X", "D", 4, 0, 3000, AirspaceKind.Ctr, new AirspaceDetails(Frequencies: [new AirspaceFrequency("Tour", "118.350")]));
        var b = PointBriefings.For(Route, 1, 2000, [zone, zone], ["Lognes Info 120.000"]);
        Assert.Equal(["Lognes Info 120.000", "Tour 118.350"], b.Frequencies);
    }

    [Fact]
    public void Entered_altitude_is_written_in_the_pln_and_shared_with_stormatc()
    {
        var route = new List<Waypoint> { Wp("A", 49, 2), Wp("B", 49, 2.5, 3456) };
        string pln = PlnWriter.Build(route, 2500);
        Assert.Contains("+003456.00", pln);
        Assert.Equal(3456, LiveFeed.State(route, null, null, 2500, DateTimeOffset.UtcNow).Route[1].AltitudeFt);
        Assert.Null(LiveFeed.State(route, null, null, 2500, DateTimeOffset.UtcNow).Route[0].AltitudeFt);
    }
}
