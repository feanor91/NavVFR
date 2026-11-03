using VFRNav.Core.Ifr;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class IfrRouterTests
{
    // Réseau fictif : LFAA au nord, LFBB au sud. UN1 puis UN5 en ligne droite (cap 180) ; UN2 est un détour par l'est.
    //   LFAA - SIDWP - ALPHA - BRAVO - CHARL - DELTA - STARWP - LFBB
    internal static IfrNavData Network(Action<List<AirwaySegment>>? tweak = null)
    {
        List<IfrFix> fixes =
        [
            new("LFAA", 48.0, 2.0, FixKind.Airport),
            new("LFBB", 44.0, 2.0, FixKind.Airport),
            new("SIDWP", 47.8, 2.0), new("SIDEA", 47.8, 2.3),
            new("ALPHA", 47.5, 2.0), new("BRAVO", 46.0, 2.0), new("CHARL", 45.0, 2.0), new("DELTA", 44.5, 2.0),
            new("STARWP", 44.2, 2.0),
            new("ECHO", 46.0, 4.0), new("FOXTR", 45.0, 4.0),
            // Deux terrains sans procédure.
            new("LFCC", 40.0, 8.0, FixKind.Airport), new("LFDD", 40.0, 10.0, FixKind.Airport),
            new("GOLF", 40.1, 8.2), new("HOTEL", 40.1, 9.8),
            // Un terrain isolé.
            new("LFZZ", 30.0, 0.0, FixKind.Airport),
        ];

        List<AirwaySegment> segments =
        [
            new("UN1", "ALPHA", "BRAVO"), new("UN1", "BRAVO", "CHARL"),
            new("UN5", "CHARL", "DELTA"),
            new("UN2", "ALPHA", "ECHO"), new("UN2", "ECHO", "FOXTR"), new("UN2", "FOXTR", "DELTA"),
            new("UN9", "GOLF", "HOTEL"),
        ];
        tweak?.Invoke(segments);

        List<IfrProcedure> procedures =
        [
            new(ProcedureKind.Sid, "LFAA", "ALPH1A",
            [
                new ProcedureVariant(["01"], [new ProcedureLeg("SIDWP", MinAltFt: 3000), new ProcedureLeg("ALPHA")]),
                new ProcedureVariant(["19"], [new ProcedureLeg("SIDEA"), new ProcedureLeg("ALPHA")]),
            ]),
            new(ProcedureKind.Star, "LFBB", "DELT1B",
                [new ProcedureVariant([], [new ProcedureLeg("DELTA", MaxAltFt: 6000), new ProcedureLeg("STARWP")])]),
        ];

        return new IfrNavData(fixes, segments, procedures, "2611", new DateTime(2026, 10, 29));
    }

    [Fact]
    public void Plan_uses_sid_airways_and_star_in_order()
    {
        var result = IfrRouter.Plan(Network(), "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" });

        Assert.True(result.Success, result.Error);
        var route = result.Route!;
        Assert.Equal("ALPH1A", route.Sid);
        Assert.Equal("DELT1B", route.Star);
        Assert.Equal(
            ["LFAA", "SIDWP", "ALPHA", "BRAVO", "CHARL", "DELTA", "STARWP", "LFBB"],
            route.Points.Select(p => p.Ident));
    }

    [Fact]
    public void Description_is_a_flight_plan_route_with_consecutive_points_on_one_airway_merged()
    {
        var route = IfrRouter.Plan(Network(), "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;
        Assert.Equal("LFAA ALPH1A ALPHA UN1 CHARL UN5 DELTA DELT1B LFBB", route.Description);
    }

    [Fact]
    public void Points_carry_their_airway_and_procedure_constraints()
    {
        var route = IfrRouter.Plan(Network(), "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;

        Assert.Equal(3000, route.Points.Single(p => p.Ident == "SIDWP").MinAltFt);
        Assert.Equal(6000, route.Points.Single(p => p.Ident == "DELTA").MaxAltFt);
        Assert.Equal("UN1", route.Points.Single(p => p.Ident == "BRAVO").Airway);
        Assert.Equal("UN5", route.Points.Single(p => p.Ident == "DELTA").Airway);
        Assert.Equal(IfrLegSource.Sid, route.Points.Single(p => p.Ident == "SIDWP").Source);
        Assert.Equal(IfrLegSource.Star, route.Points.Single(p => p.Ident == "STARWP").Source);
    }

    [Fact]
    public void Distance_is_the_sum_of_the_legs()
    {
        var route = IfrRouter.Plan(Network(), "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;
        double sum = 0;
        for (int i = 1; i < route.Points.Count; i++)
            sum += NavMath.DistanceNm(route.Points[i - 1].Lat, route.Points[i - 1].Lon, route.Points[i].Lat, route.Points[i].Lon);

        Assert.Equal(sum, route.DistanceNm, 6);
        Assert.InRange(route.DistanceNm, 238, 242); // 4° de latitude = 240 NM en ligne droite
    }

    [Fact]
    public void Flight_level_rules_out_segments_that_do_not_cover_it()
    {
        // UN1 plafonnée au FL200 : à FL300 la route doit passer par le détour UN2.
        var data = Network(s => s[1] = new AirwaySegment("UN1", "BRAVO", "CHARL", UpperFl: 200));

        var low = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01", FlightLevel = 150 }).Route!;
        var high = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01", FlightLevel = 300, FreeRouteFromFl = null }).Route!;

        Assert.Contains(low.Points, p => p.Ident == "BRAVO");
        Assert.DoesNotContain(high.Points, p => p.Ident == "BRAVO");
        Assert.Contains(high.Points, p => p.Ident == "ECHO");
        Assert.Equal("LFAA ALPH1A ALPHA UN2 DELTA DELT1B LFBB", high.Description);
    }

    [Fact]
    public void One_way_segments_are_respected()
    {
        // UN5 à sens unique vers le nord : on ne peut plus l'emprunter de CHARL vers DELTA.
        var data = Network(s => s[2] = new AirwaySegment("UN5", "CHARL", "DELTA", AirwayDirection.Backward));

        var route = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;

        Assert.DoesNotContain(route.Points, p => p.Ident == "CHARL");
        Assert.Contains(route.Points, p => p.Ident == "ECHO");
    }

    [Fact]
    public void Runway_selects_the_sid_variant()
    {
        var data = Network();
        var rwy01 = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;
        var rwy19 = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "19" }).Route!;

        Assert.Contains(rwy01.Points, p => p.Ident == "SIDWP");
        Assert.Contains(rwy19.Points, p => p.Ident == "SIDEA");
        Assert.DoesNotContain(rwy19.Points, p => p.Ident == "SIDWP");
    }

    [Fact]
    public void Without_runway_the_shortest_variant_wins()
    {
        var route = IfrRouter.Plan(Network(), "LFAA", "LFBB").Route!;
        Assert.Contains(route.Points, p => p.Ident == "SIDWP");
    }

    [Fact]
    public void Unknown_runway_or_procedure_is_an_error_not_a_silent_fallback()
    {
        var data = Network();

        var runway = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "27" });
        var sid = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { Sid = "NOPE1A" });

        Assert.False(runway.Success);
        Assert.Contains("27", runway.Error);
        Assert.False(sid.Success);
        Assert.Contains("NOPE1A", sid.Error);
    }

    [Fact]
    public void Airport_without_procedures_joins_the_network_directly()
    {
        var route = IfrRouter.Plan(Network(), "LFCC", "LFDD").Route!;

        Assert.Null(route.Sid);
        Assert.Null(route.Star);
        Assert.Equal(["LFCC", "GOLF", "HOTEL", "LFDD"], route.Points.Select(p => p.Ident));
        Assert.Equal("LFCC GOLF UN9 HOTEL LFDD", route.Description);
        Assert.Equal(IfrLegSource.Direct, route.Points[1].Source);
    }

    [Fact]
    public void Direct_entry_points_must_be_usable_at_the_requested_level()
    {
        // GOLF est le point le plus proche de LFCC mais son airway est plafonné au FL100 : à FL300 il faut viser un autre point.
        var fixes = new IfrFix[]
        {
            new("LFCC", 40.0, 8.0, FixKind.Airport), new("LFDD", 40.0, 10.0, FixKind.Airport),
            new("GOLF", 40.05, 8.0), new("HOTEL", 40.05, 10.0), new("INDIA", 40.3, 8.3), new("JULIET", 40.3, 9.7),
        };
        var segments = new[]
        {
            new AirwaySegment("UL1", "GOLF", "HOTEL", UpperFl: 100),
            new AirwaySegment("UN2", "INDIA", "JULIET"),
        };
        var data = new IfrNavData(fixes, segments, []);

        var route = IfrRouter.Plan(data, "LFCC", "LFDD", new IfrPlanOptions { FlightLevel = 300, FreeRouteFromFl = null }).Route!;

        Assert.Equal(["LFCC", "INDIA", "JULIET", "LFDD"], route.Points.Select(p => p.Ident));
    }

    [Fact]
    public void Free_route_level_allows_direct_legs_between_nearby_points()
    {
        // Aucun airway : à FL300 (route libre) la route enchaîne des tronçons directs, en dessous elle échoue.
        var fixes = new IfrFix[]
        {
            new("LFCC", 40.0, 8.0, FixKind.Airport), new("LFDD", 40.0, 10.5, FixKind.Airport),
            new("P1", 40.1, 8.4), new("P2", 40.1, 9.0), new("P3", 40.1, 9.6), new("OTHER", 40.1, 9.0),
            new("AWY1", 40.2, 8.4), new("AWY2", 40.2, 8.5),
        };
        var data = new IfrNavData(fixes, [new AirwaySegment("UN9", "AWY1", "AWY2")], []);
        var opts = new IfrPlanOptions { AllowDirect = false };

        opts.FlightLevel = 150;
        Assert.False(IfrRouter.Plan(data, "LFCC", "LFDD", opts).Success);

        opts.FlightLevel = 300;
        var route = IfrRouter.Plan(data, "LFCC", "LFDD", opts).Route!;
        Assert.All(route.Points.Skip(1).Take(route.Points.Count - 2), p => Assert.Equal(IfrLegSource.Direct, p.Source));
        Assert.True(route.Points.Count >= 3);
        Assert.Equal("LFCC", route.Points[0].Ident);
        Assert.Equal("LFDD", route.Points[^1].Ident);

        opts.FreeRouteFromFl = null; // désactivé : plus de route libre
        Assert.False(IfrRouter.Plan(data, "LFCC", "LFDD", opts).Success);
    }

    [Fact]
    public void No_airway_link_falls_back_to_direct_or_fails_when_forbidden()
    {
        var data = Network();

        var direct = IfrRouter.Plan(data, "LFAA", "LFCC", new IfrPlanOptions { DepartureRunway = "01", DirectRadiusNm = 150 });
        var forbidden = IfrRouter.Plan(data, "LFAA", "LFCC", new IfrPlanOptions { DepartureRunway = "01", DirectRadiusNm = 150, AllowDirect = false });

        Assert.True(direct.Success, direct.Error);
        Assert.Equal("LFCC", direct.Route!.Points[^1].Ident);
        Assert.False(forbidden.Success);
        Assert.NotNull(forbidden.Error);
    }

    [Fact]
    public void Unknown_or_isolated_airports_report_a_clear_error()
    {
        var data = Network();
        Assert.Contains("LFXX", IfrRouter.Plan(data, "LFXX", "LFBB").Error);
        Assert.Contains("LFZZ", IfrRouter.Plan(data, "LFZZ", "LFBB").Error);
    }

    [Fact]
    public void Segments_with_unknown_endpoints_are_ignored()
    {
        var data = Network(s => s.Add(new AirwaySegment("UN7", "ALPHA", "GHOST")));
        Assert.Equal(7, data.Segments.Count);
        Assert.Empty(data.EdgesFrom("GHOST"));
    }

    [Fact]
    public void ToWaypoints_feeds_the_existing_navlog_types()
    {
        var route = IfrRouter.Plan(Network(), "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;
        var waypoints = route.ToWaypoints();

        Assert.Equal(route.Points.Count, waypoints.Count);
        Assert.Equal("LFAA", waypoints[0].Icao);
        Assert.Null(waypoints[1].Icao);
        Assert.Equal(3000, waypoints[1].AltitudeFt);
    }

    [Fact]
    public void Airac_cycle_expires_after_28_days()
    {
        var data = Network();
        Assert.False(data.IsExpired(new DateTime(2026, 11, 25)));
        Assert.True(data.IsExpired(new DateTime(2026, 11, 26)));
    }
}

public class IfrSampleDataTests
{
    [Theory]
    [InlineData("LFPG", "LFMN", "TST3N")]
    [InlineData("LFPG", "LFLL", "TST1L")]
    [InlineData("LFPG", "LFBO", "TST1T")]
    public void Demo_routes_between_the_test_airports_are_found(string dep, string arr, string star)
    {
        var result = IfrRouter.Plan(IfrSampleData.Create(), dep, arr);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Route!.Sid, new[] { "TST1A", "TST2B" }); // les deux SID mènent au réseau : la plus courte gagne
        Assert.Equal(star, result.Route.Star);
    }

    [Fact]
    public void Demo_airport_without_procedures_still_connects()
    {
        var route = IfrRouter.Plan(IfrSampleData.Create(), "LFBO", "LFPG").Route!;
        Assert.Null(route.Star);
        Assert.Equal("LFBO", route.Points[0].Ident);
        Assert.Equal("LFPG", route.Points[^1].Ident);
    }

    [Fact]
    public void Demo_data_is_labelled_as_test_data()
    {
        Assert.Equal("TEST", IfrSampleData.Create().AiracCycle);
    }

    [Fact]
    public void No_sid_or_star_hooks_the_airport_straight_onto_the_network()
    {
        var data = IfrRouterTests.Network();
        var auto = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { DepartureRunway = "01" }).Route!;
        var none = IfrRouter.Plan(data, "LFAA", "LFBB", new IfrPlanOptions { NoSid = true, NoStar = true }).Route!;

        Assert.Equal("ALPH1A", auto.Sid);
        Assert.Null(none.Sid);
        Assert.Null(none.Star);
        Assert.Equal("LFAA", none.Points[0].Ident);
        Assert.Equal("LFBB", none.Points[^1].Ident);
        Assert.DoesNotContain(none.Points, p => p.Source is IfrLegSource.Sid or IfrLegSource.Star);
    }

    [Fact]
    public void Free_route_only_links_points_of_regions_where_it_exists()
    {
        List<IfrFix> fixes =
        [
            new("LFCC", 40.0, 8.0, FixKind.Airport), new("LFDD", 40.0, 10.5, FixKind.Airport),
            new("P1", 40.1, 8.4), new("P2", 40.1, 9.0), new("P3", 40.1, 9.6),
            new("Q1/ZZ", 40.1, 8.4), new("Q2/ZZ", 40.1, 9.0), new("Q3/ZZ", 40.1, 9.6),
            new("AWY1", 40.2, 8.4), new("AWY2", 40.2, 8.5),
        ];
        var data = new IfrNavData(fixes, [new AirwaySegment("UN9", "AWY1", "AWY2")], []);
        var opts = new IfrPlanOptions { AllowDirect = false, FlightLevel = 300 };

        var inFrance = IfrRouter.Plan(data, "LFCC", "LFDD", opts).Route!;
        Assert.Contains(inFrance.Points, p => p.Ident is "P1" or "P2" or "P3");         // France (LF) : route libre permise

        opts.FreeRouteRegions = new HashSet<string> { "EG" };
        Assert.False(IfrRouter.Plan(data, "LFCC", "LFDD", opts).Success);          // hors de ces régions : pas de tronçon direct, rien ne relie les deux terrains

        opts.FreeRouteRegions = null;
        Assert.True(IfrRouter.Plan(data, "LFCC", "LFDD", opts).Success);
    }

    [Fact]
    public void Procedure_labels_show_where_a_sid_exits_and_a_star_enters()
    {
        var data = IfrRouterTests.Network();
        var sid = data.ProceduresFor("LFAA", ProcedureKind.Sid).Single();
        var star = data.ProceduresFor("LFBB", ProcedureKind.Star).Single();

        Assert.Equal("ALPH1A → ALPHA", ProcedureLabel.For(sid));
        Assert.Equal("DELT1B ← DELTA", ProcedureLabel.For(star));
        Assert.Equal("ALPH1A", ProcedureLabel.Ident("ALPH1A → ALPHA"));
        Assert.Equal("DELT1B", ProcedureLabel.Ident("DELT1B ← DELTA"));
        Assert.Equal("08L", ProcedureLabel.Ident("08L"));
    }
}
