using System.Text;
using VFRNav.Core.Ifr;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class IlsPlannerTests
{
    // ILS fictif : seuil à 49,00 N 2,60 E, axe 265° vrai (on arrive par l'est), zone à 100 ft, pente 3°, RDH 15,24 m (50 ft).
    private static IlsApproach Make(string runway = "26L", double bearing = 265, double? slope = 3, string category = "III") => new(
        "LFXX", runway, category, "DSU", 108.35, slope, 49.00, 2.60, bearing, bearing - 1, 100, 15.24);

    [Fact]
    public void Glide_path_altitude_adds_the_slope_to_the_field_and_the_reference_height()
    {
        // 100 ft + 50 ft + 10 NM x 318,4 ft/NM = 3 334 ft -> 3 300 ; 5 NM : 1 742 -> 1 700.
        Assert.Equal(3300, IlsPlanner.GlidePathAltitudeFt(Make(), 0, 10));
        Assert.Equal(1700, IlsPlanner.GlidePathAltitudeFt(Make(), 0, 5));
    }

    [Fact]
    public void A_missing_or_absurd_slope_falls_back_to_three_degrees()
    {
        int normal = IlsPlanner.GlidePathAltitudeFt(Make(), 0, 10);
        Assert.Equal(normal, IlsPlanner.GlidePathAltitudeFt(Make(slope: null), 0, 10));
        Assert.Equal(normal, IlsPlanner.GlidePathAltitudeFt(Make(slope: 0), 0, 10));
        Assert.Equal(normal, IlsPlanner.GlidePathAltitudeFt(Make(slope: 45), 0, 10));
    }

    [Fact]
    public void A_steeper_slope_gives_a_higher_altitude_at_the_same_distance()
    {
        Assert.True(IlsPlanner.GlidePathAltitudeFt(Make(slope: 4), 0, 10) > IlsPlanner.GlidePathAltitudeFt(Make(slope: 3), 0, 10));
    }

    [Fact]
    public void The_field_elevation_is_used_when_the_ils_has_none()
    {
        var withoutElevation = Make() with { ElevationFt = null };
        Assert.Equal(IlsPlanner.GlidePathAltitudeFt(Make(), 0, 10) + 900, IlsPlanner.GlidePathAltitudeFt(withoutElevation, 1000, 10));
    }

    [Fact]
    public void Final_points_lie_on_the_extended_centreline_at_the_right_distance()
    {
        var final = IlsPlanner.Build(Make(), 100);

        Assert.Equal(["ILS26L-10", "ILS26L-5"], final.Points.Select(p => p.Ident));
        foreach (var p in final.Points)
        {
            Assert.InRange(NavMath.DistanceNm(49.00, 2.60, p.Lat, p.Lon), p.DistanceNm - 0.05, p.DistanceNm + 0.05);
            // Du point vers le seuil, on suit l'axe de la piste : c'est bien la finale.
            double course = NavMath.TrueCourse(new Waypoint { Lat = p.Lat, Lon = p.Lon }, new Waypoint { Lat = 49.00, Lon = 2.60 });
            Assert.InRange(course, 264.5, 265.5);
            Assert.True(p.Lon > 2.60, "on arrive par l'est, sur une piste 26");
        }
        Assert.True(final.Points[0].AltitudeFt > final.Points[1].AltitudeFt, "l'altitude diminue vers le seuil");
    }

    [Fact]
    public void Summary_gives_the_frequency_course_slope_category_and_warns_it_is_not_the_published_procedure()
    {
        string summary = IlsPlanner.Build(Make(), 100).Summary;

        Assert.Contains("ILS 26L DSU 108,35 MHz", summary);
        Assert.Contains("QDM 264°", summary);
        Assert.Contains("pente 3,0°", summary);
        Assert.Contains("cat III", summary);
        Assert.Contains("interception à 10 NM", summary);
        Assert.Contains("pas la procédure publiée", summary);
    }

    [Fact]
    public void An_unrated_ils_does_not_show_a_category()
    {
        Assert.DoesNotContain("· cat", IlsPlanner.Build(Make(category: "NOCAT"), 100).Summary);
    }

    [Fact]
    public void The_runway_chosen_needs_the_smallest_turn_from_the_arrival_direction()
    {
        var east = Make("26L", 265);   // axe vers l'ouest
        var west = Make("08L", 85);    // axe vers l'est
        // Arrivée par l'est, en volant vers l'ouest : on prend la 26.
        Assert.Equal("26L", IlsPlanner.ChooseRunway([west, east], 270)!.Runway);
        Assert.Equal("08L", IlsPlanner.ChooseRunway([west, east], 80)!.Runway);
        Assert.Equal("08L", IlsPlanner.ChooseRunway([east, west], 355)!.Runway);   // ex æquo en écart (90° / 90°) : ordre alphabétique stable
        Assert.Null(IlsPlanner.ChooseRunway([], 0));
    }

    // ---------- insertion dans la route ----------

    private static IfrRoute RouteFrom(double lat, double lon, string star = "STAR1A") => new(
        [new IfrRoutePoint("ENRTE", lat, lon, IfrLegSource.Airway),
         new IfrRoutePoint("LFXX", 49.00, 2.55, IfrLegSource.Airport)],
        50, null, star, $"LFYY ENRTE {star} LFXX");

    private static IlsCatalog Catalog() => new([Make("26L", 265), Make("08R", 85) with { ThresholdLon = 2.50 }]);

    [Fact]
    public void The_final_is_inserted_just_before_the_arrival_airport_in_order_from_far_to_near()
    {
        var route = RouteFrom(49.0, 3.8);                                   // 50 NM à l'est : on arrive en volant vers l'ouest

        var (result, final, note) = IlsPlanner.Apply(route, Catalog(), null, 100);

        Assert.Null(note);
        Assert.Equal("26L", final!.Ils.Runway);                             // choisie : la plus alignée avec l'arrivée
        Assert.Equal(["ENRTE", "ILS26L-10", "ILS26L-5", "LFXX"], result.Points.Select(p => p.Ident));
        Assert.Equal(IfrLegSource.Ils, result.Points[1].Source);
        Assert.Equal(IfrLegSource.Airport, result.Points[^1].Source);
        Assert.Equal(final.Points[0].AltitudeFt, result.Points[1].MinAltFt);
        Assert.Equal(final.Points[0].AltitudeFt, result.Points[1].MaxAltFt);
    }

    [Fact]
    public void The_description_and_the_distance_follow_the_new_points()
    {
        var route = RouteFrom(49.0, 3.8);
        var (result, _, _) = IlsPlanner.Apply(route, Catalog(), null, 100);

        Assert.Equal("LFYY ENRTE STAR1A ILS26L LFXX", result.Description);
        double expected = 0;
        for (int i = 1; i < result.Points.Count; i++)
            expected += NavMath.DistanceNm(result.Points[i - 1].Lat, result.Points[i - 1].Lon, result.Points[i].Lat, result.Points[i].Lon);
        Assert.Equal(expected, result.DistanceNm, 6);
    }

    [Fact]
    public void A_requested_runway_is_respected_even_if_it_means_a_longer_route()
    {
        var route = RouteFrom(49.0, 3.8);
        var (result, final, _) = IlsPlanner.Apply(route, Catalog(), "08R", 100);

        Assert.Equal("08R", final!.Ils.Runway);
        Assert.Contains("ILS08R", result.Description);
    }

    [Fact]
    public void A_runway_without_ils_is_explained_and_the_route_is_left_alone()
    {
        var route = RouteFrom(49.0, 3.8);
        var (result, final, note) = IlsPlanner.Apply(route, Catalog(), "17", 100);

        Assert.Same(route, result);
        Assert.Null(final);
        Assert.Contains("La piste 17 de LFXX n'a pas d'ILS", note);
        Assert.Contains("08R, 26L", note);
    }

    [Fact]
    public void An_airport_without_any_ils_data_is_explained()
    {
        var route = RouteFrom(49.0, 3.8);
        var (result, final, note) = IlsPlanner.Apply(route, new IlsCatalog([]), null, 100);

        Assert.Same(route, result);
        Assert.Null(final);
        Assert.Contains("Pas d'ILS dans les données pour LFXX", note);
        Assert.Contains("France", note);
    }

    [Fact]
    public void Final_points_behind_the_last_route_point_are_not_added()
    {
        // Dernier point de la route à 6 NM du seuil : ni l'interception à 10 NM, ni la finale à 5 NM (qui ne gagnerait que 1 NM) ne sont ajoutées.
        var (lat, lon) = RouteGeometry.Destination(49.00, 2.60, 85, 6);
        var (closeRoute, closeFinal, closeNote) = IlsPlanner.Apply(RouteFrom(lat, lon), Catalog(), "26L", 100);
        Assert.Equal(2, closeRoute.Points.Count);
        Assert.NotNull(closeFinal);
        Assert.Contains("aucun point de finale ajouté", closeNote);

        // À 8 NM : seule la finale à 5 NM est utile.
        var (lat8, lon8) = RouteGeometry.Destination(49.00, 2.60, 85, 8);
        var (midRoute, _, midNote) = IlsPlanner.Apply(RouteFrom(lat8, lon8), Catalog(), "26L", 100);
        Assert.Null(midNote);
        Assert.Equal(["ENRTE", "ILS26L-5", "LFXX"], midRoute.Points.Select(p => p.Ident));
    }

    [Fact]
    public void A_route_that_does_not_end_at_an_airport_is_left_alone()
    {
        var route = new IfrRoute([new IfrRoutePoint("A", 49, 3, IfrLegSource.Airway), new IfrRoutePoint("B", 49, 3.5, IfrLegSource.Airway)], 20, null, null, "A B");
        var (result, final, note) = IlsPlanner.Apply(route, Catalog(), null, 0);
        Assert.Same(route, result);
        Assert.Null(final);
        Assert.Null(note);
    }

    [Fact]
    public void The_ils_points_become_waypoints_with_their_altitude()
    {
        var (result, _, _) = IlsPlanner.Apply(RouteFrom(49.0, 3.8), Catalog(), null, 100);
        var waypoints = result.ToWaypoints();

        var intercept = waypoints.Single(w => w.Name == "ILS26L-10");
        Assert.Equal(result.Points[1].MinAltFt, (int?)intercept.AltitudeFt);
        Assert.Null(intercept.Icao);
        Assert.Equal("LFXX", waypoints[^1].Icao);
    }
}

public class AixmIlsTests
{
    // Extrait fabriqué à la main au format de l'export du SIA : l'ILS précède volontairement la piste qui donne son seuil.
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <AIXM-Snapshot created="2026-09-09T15:26:41.163+02:00" origin="Sia-France" version="4.5" effective="2026-10-29T00:00:00.000+01:00">
          <Ils>
            <IlsUid mid="1"><RdnUid mid="2"><RwyUid mid="3"><AhpUid mid="4"><codeId>LFPG</codeId></AhpUid><txtDesig>08R/26L</txtDesig></RwyUid><txtDesig>26L</txtDesig></RdnUid></IlsUid>
            <codeCat>III</codeCat>
            <Ilz><codeId>DSU</codeId><valFreq>108.35</valFreq><uomFreq>MHZ</uomFreq><geoLat>485949.00N</geoLat><geoLong>0023609.00E</geoLong></Ilz>
            <Igp><valFreq>330.5</valFreq><valSlope>3</valSlope><valRdh>17.3</valRdh><uomRdh>M</uomRdh><valElev>315</valElev><uomDistVer>FT</uomDistVer></Igp>
          </Ils>
          <Ils>
            <IlsUid mid="5"><RdnUid mid="6"><RwyUid mid="7"><AhpUid mid="8"><codeId>LFPG</codeId></AhpUid><txtDesig>08R/26L</txtDesig></RwyUid><txtDesig>08R</txtDesig></RdnUid></IlsUid>
            <codeCat>NOCAT</codeCat>
            <Ilz><codeId>DSE</codeId><valFreq>108.55</valFreq></Ilz>
          </Ils>
          <Ils>
            <IlsUid mid="9"><RdnUid mid="10"><RwyUid mid="11"><AhpUid mid="12"><codeId>LFXX</codeId></AhpUid><txtDesig>10/28</txtDesig></RwyUid><txtDesig>28</txtDesig></RdnUid></IlsUid>
            <Ilz><codeId>ZZZ</codeId><valFreq>109.1</valFreq></Ilz>
          </Ils>
          <Ils>
            <IlsUid mid="13"><RdnUid mid="14"><RwyUid mid="15"><AhpUid mid="16"><codeId>NWWW</codeId></AhpUid><txtDesig>11/29</txtDesig></RwyUid><txtDesig>11</txtDesig></RdnUid></IlsUid>
            <Ilz><codeId>NOU</codeId><valFreq>110.3</valFreq></Ilz>
          </Ils>
          <Rdn><RdnUid mid="6"><RwyUid mid="7"><AhpUid mid="8"><codeId>LFPG</codeId></AhpUid><txtDesig>08R/26L</txtDesig></RwyUid><txtDesig>08R</txtDesig></RdnUid>
            <geoLat>485935.00N</geoLat><geoLong>0023924.00E</geoLong><valTrueBrg>85.4</valTrueBrg><valMagBrg>84.3</valMagBrg></Rdn>
          <Rdn><RdnUid mid="2"><RwyUid mid="3"><AhpUid mid="4"><codeId>LFPG</codeId></AhpUid><txtDesig>08R/26L</txtDesig></RwyUid><txtDesig>26L</txtDesig></RdnUid>
            <geoLat>485942.00N</geoLat><geoLong>0023609.00E</geoLong><valTrueBrg>265.4</valTrueBrg><valMagBrg>264.3</valMagBrg></Rdn>
          <Rdn><RdnUid mid="14"><RwyUid mid="15"><AhpUid mid="16"><codeId>NWWW</codeId></AhpUid><txtDesig>11/29</txtDesig></RwyUid><txtDesig>11</txtDesig></RdnUid>
            <geoLat>221000.00S</geoLat><geoLong>1663000.00E</geoLong><valTrueBrg>110</valTrueBrg></Rdn>
        </AIXM-Snapshot>
        """;

    private static AixmLoadResult Load(GeoArea? area = null) => AixmReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(Sample)), area);

    [Fact]
    public void Ils_are_read_with_their_localizer_slope_category_and_runway_threshold()
    {
        var ils = Load().Ils.Find("LFPG", "26L")!;

        Assert.Equal("DSU", ils.LocalizerIdent);
        Assert.Equal(108.35, ils.LocalizerMhz);
        Assert.Equal("III", ils.Category);
        Assert.Equal(3, ils.GlideSlopeDeg);
        Assert.Equal(17.3, ils.ReferenceHeightM);
        Assert.Equal(315, ils.ElevationFt);
        Assert.Equal(265.4, ils.TrueBearingDeg);
        Assert.Equal(264.3, ils.MagneticBearingDeg);
        Assert.Equal(48.995, ils.ThresholdLat, 3);    // 485942 N = 48° 59' 42"
        Assert.Equal(2.6025, ils.ThresholdLon, 4);    // 0023609 E
    }

    [Fact]
    public void An_ils_without_slope_data_is_still_usable()
    {
        var ils = Load().Ils.Find("LFPG", "08R")!;
        Assert.Null(ils.GlideSlopeDeg);
        Assert.Null(ils.ElevationFt);
        Assert.Equal("NOCAT", ils.Category);
    }

    [Fact]
    public void An_ils_whose_runway_threshold_is_unknown_is_ignored()
    {
        Assert.Null(Load().Ils.Find("LFXX", "28"));
    }

    [Fact]
    public void The_area_filter_drops_overseas_ils()
    {
        Assert.NotNull(Load().Ils.Find("NWWW", "11"));
        Assert.Null(Load(GeoArea.MetropolitanFrance).Ils.Find("NWWW", "11"));
        Assert.NotNull(Load(GeoArea.MetropolitanFrance).Ils.Find("LFPG", "26L"));
    }

    [Fact]
    public void The_catalog_lookup_ignores_case_and_spaces()
    {
        var catalog = Load().Ils;
        Assert.NotNull(catalog.Find(" lfpg ", "26l "));
        Assert.Equal(2, catalog.For("LFPG").Count);
    }
}
