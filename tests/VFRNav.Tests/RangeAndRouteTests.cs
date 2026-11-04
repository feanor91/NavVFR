using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class FuelCheckTests
{
    // 90 L à 30 L/h : 3 h, réserve VFR de 30 min = 15 L, réserve IFR de 45 min = 22,5 L.
    private static Aircraft Make() => new()
    {
        Name = "Test", Vfr = true, Ifr = true, CruiseKt = 100, MaxKt = 120, BurnLph = 30, FuelCapacityL = 90, CruiseAltFt = 3000,
    };

    private static NavLog Log(double fuelL) => new([], 250, 150, fuelL);   // 250 NM en 2 h 30 : 100 kt de vitesse sol

    [Fact]
    public void A_route_that_fits_with_the_reserve_raises_no_alert()
    {
        var r = FuelCheck.Evaluate(Make(), FlightMode.Vfr, Log(70));
        Assert.Equal(FuelStatus.Enough, r.Status);
        Assert.Equal(0, r.ShortfallL);
        Assert.Null(FuelCheck.Message(Make(), FlightMode.Vfr, Log(70)));
    }

    [Fact]
    public void A_route_that_fits_in_the_tank_but_not_with_the_reserve_eats_the_reserve()
    {
        var r = FuelCheck.Evaluate(Make(), FlightMode.Vfr, Log(80));   // utilisable 75 L : il manque 5 L

        Assert.Equal(FuelStatus.EatsReserve, r.Status);
        Assert.Equal(5, r.ShortfallL, 6);
        Assert.Equal(10, r.ShortfallMinutes, 6);                       // 5 L à 30 L/h
        Assert.Equal(16.67, r.ShortfallNm, 2);                         // 10 min à 100 kt
        string message = FuelCheck.Message(Make(), FlightMode.Vfr, Log(80))!;
        Assert.Contains("entame la réserve de 30 min", message);
        Assert.Contains("il manque 5 L", message);
    }

    [Fact]
    public void A_route_beyond_the_tank_is_insufficient()
    {
        var r = FuelCheck.Evaluate(Make(), FlightMode.Vfr, Log(95));
        Assert.Equal(FuelStatus.Insufficient, r.Status);
        Assert.Equal(5, r.ShortfallL, 6);                              // au-delà du réservoir
        Assert.Contains("escale", FuelCheck.Message(Make(), FlightMode.Vfr, Log(95))!);
    }

    [Fact]
    public void The_ifr_reserve_is_larger_so_the_same_route_can_pass_in_vfr_but_not_in_ifr()
    {
        var log = Log(70);   // utilisable : 75 L en VFR, 67,5 L en IFR
        Assert.Equal(FuelStatus.Enough, FuelCheck.Evaluate(Make(), FlightMode.Vfr, log).Status);
        Assert.Equal(FuelStatus.EatsReserve, FuelCheck.Evaluate(Make(), FlightMode.Ifr, log).Status);
    }

    [Fact]
    public void An_explicit_reserve_replaces_the_default()
    {
        var a = Make();
        a.ReserveMinutes = 0;
        Assert.Equal(FuelStatus.Enough, FuelCheck.Evaluate(a, FlightMode.Ifr, Log(88)).Status);   // plus de réserve : tout le réservoir sert
    }

    [Fact]
    public void The_a320_route_from_the_screenshot_eats_about_20_minutes_of_the_reserve()
    {
        var catalog = AircraftCatalog.Load(Path.Combine(Path.GetTempPath(), "vfrnav-absent-" + Guid.NewGuid().ToString("N"), "a.json"));
        var a320 = catalog.Find(AircraftCatalog.A320NeoId)!;
        // 2 172,3 + 1 333,9 = 3 506 NM à 447 kt : 7 h 50, soit 22 550 L environ.
        double minutes = 3506.2 / 447 * 60;
        var log = new NavLog([], 3506.2, minutes, minutes / 60 * a320.BurnLph);

        var r = FuelCheck.Evaluate(a320, FlightMode.Ifr, log);

        Assert.Equal(FuelStatus.EatsReserve, r.Status);
        Assert.InRange(r.ShortfallMinutes, 18, 23);
        Assert.InRange(r.ShortfallNm, 140, 160);      // la route dépasse la portée utile de 150 NM
    }

    [Fact]
    public void Missing_ground_speed_falls_back_to_the_cruise_speed()
    {
        var r = FuelCheck.Evaluate(Make(), FlightMode.Vfr, new NavLog([], 0, 0, 80));
        Assert.Equal(10 / 60.0 * 100, r.ShortfallNm, 6);
    }
}

public class RangeShadingTests
{
    private const double Lyon = 45.70, LyonLon = 4.95;

    [Theory]
    [InlineData(45.7, 3691, 'N')]
    [InlineData(45.7, 3356, 'N')]
    [InlineData(45.7, 600, null)]
    [InlineData(-60.0, 3000, 'S')]
    [InlineData(0.0, 3691, null)]
    public void The_pole_enclosed_by_a_circle_is_detected(double lat, double radius, char? expected) =>
        Assert.Equal(expected, RangeShading.EnclosedPole(lat, radius));

    [Fact]
    public void No_radius_encloses_no_pole() => Assert.Null(RangeShading.EnclosedPole(80, 0));

    [Fact]
    public void Boundary_spans_the_whole_map_with_matching_edges_and_the_expected_southern_point()
    {
        var boundary = RangeShading.Boundary(Lyon, LyonLon, 3691);

        Assert.Equal(-180, boundary[0].Lon);
        Assert.Equal(180, boundary[^1].Lon);
        Assert.Equal(boundary[0].Lat, boundary[^1].Lat, 6);                            // même latitude aux deux bords
        for (int i = 1; i < boundary.Count; i++) Assert.True(boundary[i].Lon >= boundary[i - 1].Lon);
        Assert.All(boundary, p => Assert.InRange(p.Lat, -85, 85));

        var south = boundary.MinBy(p => Math.Abs(p.Lon - LyonLon));
        Assert.InRange(south.Lat, -17, -14.5);                                         // ≈ 16° S, au sud de Lyon
    }

    [Fact]
    public void Outside_ring_matches_the_real_distance_everywhere()
    {
        const double radius = 3691;
        var ring = RangeShading.OutsideRing(Lyon, LyonLon, radius);
        Assert.Equal(ring[0], ring[^1]);                                               // fermé

        int checkedPoints = 0;
        for (double lat = -80; lat <= 80; lat += 20)
            for (double lon = -170; lon <= 170; lon += 40)
            {
                double d = NavMath.DistanceNm(Lyon, LyonLon, lat, lon);
                if (Math.Abs(d - radius) < 0.04 * radius) continue;                    // trop près de la frontière pour trancher
                Assert.Equal(d > radius, RangeShading.Contains(ring, lat, lon));
                checkedPoints++;
            }
        Assert.True(checkedPoints > 40);
    }

    [Fact]
    public void Band_ring_is_exactly_the_reserve_between_the_two_circles()
    {
        const double inner = 3356, outer = 3691;
        var band = RangeShading.BandRing(Lyon, LyonLon, inner, outer);

        int inBand = 0;
        for (double lat = -80; lat <= 80; lat += 10)
            for (double lon = -175; lon <= 175; lon += 25)
            {
                double d = NavMath.DistanceNm(Lyon, LyonLon, lat, lon);
                if (Math.Abs(d - inner) < 0.03 * inner || Math.Abs(d - outer) < 0.03 * outer) continue;
                bool expected = d > inner && d <= outer;
                Assert.Equal(expected, RangeShading.Contains(band, lat, lon));
                if (expected) inBand++;
            }
        Assert.True(inBand > 0, "la grille doit tomber au moins une fois dans la bande");
    }

    [Fact]
    public void Shading_is_refused_for_circles_that_do_not_enclose_a_pole_or_a_different_one()
    {
        Assert.Throws<ArgumentException>(() => RangeShading.OutsideRing(Lyon, LyonLon, 600));
        Assert.Throws<ArgumentException>(() => RangeShading.BandRing(Lyon, LyonLon, 600, 3691));
    }

    [Fact]
    public void A_southern_origin_encloses_the_south_pole_and_shades_the_north()
    {
        var ring = RangeShading.OutsideRing(-50, 20, 3500);                            // (90-50)*60 = 2400 < 3500 : pôle sud
        Assert.True(RangeShading.Contains(ring, 60, 20));                              // loin au nord : hors de portée
        Assert.False(RangeShading.Contains(ring, -80, 100));                           // près du pôle sud : à portée
    }
}

public class GreatCircleLinesTests
{
    private static Waypoint Wp(double lat, double lon) => new() { Name = "", Lat = lat, Lon = lon };

    [Fact]
    public void A_short_leg_stays_a_straight_segment()
    {
        var lines = RouteGeometry.GreatCircleLines([Wp(48, 2), Wp(48.2, 2.2)]);
        var line = Assert.Single(lines);
        Assert.Equal(2, line.Count);
        Assert.Equal((48.0, 2.0), line[0]);
        Assert.Equal((48.2, 2.2), line[^1]);
    }

    [Fact]
    public void A_long_leg_follows_the_great_circle_and_bows_towards_the_pole()
    {
        var paris = Wp(49.0, 2.5);
        var newYork = Wp(40.6, -73.8);

        var line = Assert.Single(RouteGeometry.GreatCircleLines([paris, newYork]));

        Assert.Equal((49.0, 2.5), line[0]);
        Assert.Equal((40.6, -73.8), line[^1]);
        double distance = NavMath.DistanceNm(paris, newYork);
        Assert.InRange(line.Count, distance / 30, distance / 30 + 3);                  // un point tous les 30 NM environ
        double straightMidLat = (49.0 + 40.6) / 2;
        Assert.True(line.Max(p => p.Lat) > straightMidLat + 3, "le grand cercle remonte vers le nord");
        for (int i = 1; i < line.Count; i++) Assert.True(NavMath.DistanceNm(line[i - 1].Lat, line[i - 1].Lon, line[i].Lat, line[i].Lon) < 35);
    }

    [Fact]
    public void The_line_is_cut_at_the_antimeridian_with_points_on_both_edges()
    {
        var lines = RouteGeometry.GreatCircleLines([Wp(35, 170), Wp(35, -170)]);

        Assert.Equal(2, lines.Count);
        Assert.Equal(180, lines[0][^1].Lon);
        Assert.Equal(-180, lines[1][0].Lon);
        Assert.Equal(lines[0][^1].Lat, lines[1][0].Lat, 6);                            // même latitude des deux côtés
        Assert.All(lines.SelectMany(l => l), p => Assert.InRange(p.Lon, -180, 180));
    }

    [Fact]
    public void Latitudes_are_kept_within_the_map_limits()
    {
        var line = Assert.Single(RouteGeometry.GreatCircleLines([Wp(84.9, 0), Wp(84.9, 90)], maxLat: 85));
        Assert.All(line, p => Assert.InRange(p.Lat, -85, 85));
    }

    [Fact]
    public void A_multi_leg_route_is_one_continuous_line_without_a_duplicated_junction()
    {
        var a = Wp(48, 2); var b = Wp(45, 10); var c = Wp(40, 20);
        var line = Assert.Single(RouteGeometry.GreatCircleLines([a, b, c]));

        int expected = (int)Math.Ceiling(NavMath.DistanceNm(a, b) / 30) + (int)Math.Ceiling(NavMath.DistanceNm(b, c) / 30) + 1;
        Assert.Equal(expected, line.Count);
        Assert.Contains((45.0, 10.0), line);
    }

    [Fact]
    public void A_route_with_fewer_than_two_points_has_no_line()
    {
        Assert.Empty(RouteGeometry.GreatCircleLines([]));
        Assert.Empty(RouteGeometry.GreatCircleLines([Wp(1, 1)]));
    }
}
