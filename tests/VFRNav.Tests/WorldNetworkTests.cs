using VFRNav.Core.Ifr;

namespace VFRNav.Tests;

public sealed class WorldNetworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfrnav-net-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SimPoint Pt(string ident, string region, double lat, double lon, SimPointKind kind = SimPointKind.Waypoint) => new(kind, ident, region, lat, lon, 0, 0);

    private static SimRouteLink Link(string airway, (string Id, string Region, double Lat, double Lon, double AltM)? prev, (string Id, string Region, double Lat, double Lon, double AltM)? next, int type = 2,
        char prevType = 'W', char nextType = 'W') =>
        new(airway, type, prev?.Id ?? "", prev?.Region ?? "", prev?.Lat ?? 0, prev?.Lon ?? 0, prev?.AltM ?? 0,
            next?.Id ?? "", next?.Region ?? "", next?.Lat ?? 0, next?.Lon ?? 0, next?.AltM ?? 0, prevType, nextType);

    // ---------- clés ----------

    [Theory]
    [InlineData("ABCDE", "LF", "ABCDE")]
    [InlineData("ABCDE", "", "ABCDE")]
    [InlineData("ABCDE", "eg", "ABCDE/EG")]
    [InlineData(" ABCDE ", " EG ", "ABCDE/EG")]
    public void A_key_is_the_bare_name_in_France_and_name_slash_region_elsewhere(string ident, string region, string key)
    {
        Assert.Equal(key, FixKey.Make(ident, region));
        Assert.Equal("ABCDE", FixKey.Display(key));
        Assert.Equal(region.Trim().ToUpperInvariant() is "" ? "LF" : region.Trim().ToUpperInvariant(), FixKey.Region(key));
    }

    [Fact]
    public void Procedure_points_outside_France_carry_their_region_in_the_key_and_keep_their_kind()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "UK1A" };
        sid.Common.Add(new FacilityLeg(15, "ABCDE", 51.5, -0.2, 0, 0, 0, 0, "EG", 'W'));
        sid.Common.Add(new FacilityLeg(15, "LON", 51.4, -0.4, 0, 0, 0, 0, "EG", 'V'));
        sid.Common.Add(new FacilityLeg(15, "MEDWY", 48.7, 2.4, 0, 0, 0, 0, "LF", 'W'));
        var import = SimProcedures.ToIfr("EGLL", [sid]);

        Assert.Equal(["ABCDE/EG", "LON/EG", "MEDWY"], import.Procedures.Single().Variants.Single().Legs.Select(l => l.Fix));
        Assert.Equal(FixKind.Vor, import.Fixes.Single(f => f.Ident == "LON/EG").Kind);
        Assert.Equal(FixKind.Waypoint, import.Fixes.Single(f => f.Ident == "MEDWY").Kind);
    }

    // ---------- construction ----------

    [Fact]
    public void A_link_gives_two_segments_and_the_neighbours_to_explore_but_never_France()
    {
        var b = new WorldNetworkBuilder();
        b.Seed([Pt("MID", "EG", 51, 0)]);
        var next = b.AddLinks(Pt("MID", "EG", 51, 0), [Link("UL9", ("PRV", "EG", 51, -1, 5944), ("NXT", "EG", 51, 1, 5944))]);
        Assert.Equal(["NXT", "PRV"], next.Select(p => p.Ident).Order());
        Assert.Equal(3, b.FixCount);
        Assert.Equal(2, b.SegmentCount);

        // Un voisin français est enregistré (pour relier la route au SIA) mais n'est pas exploré.
        var border = b.AddLinks(Pt("NXT", "EG", 51, 1), [Link("UL9", ("MID", "EG", 51, 0, 5944), ("FRA", "LF", 50, 2, 5944))]);
        Assert.Empty(border);
        Assert.Contains(b.Build("t", DateTimeOffset.Now).Fixes, f => f.Ident == "FRA");
    }

    [Fact]
    public void A_segment_seen_from_both_ends_is_kept_once_with_its_levels()
    {
        var b = new WorldNetworkBuilder();
        b.AddLinks(Pt("A", "EG", 51, 0), [Link("UL9", null, ("B", "EG", 51, 1, 5944))]);                  // 19 500 ft
        b.AddLinks(Pt("B", "EG", 51, 1), [Link("UL9", ("A", "EG", 51, 0, 5944), null)]);                  // le même tronçon, vu de B
        var net = b.Build("t", DateTimeOffset.Now);
        var s = Assert.Single(net.Segments);
        Assert.Equal("UL9", s.Airway);
        Assert.Equal(195, s.LowerFl);
        Assert.Null(s.UpperFl);                                                                           // route haute : pas de plafond
        Assert.Equal(AirwayDirection.Both, s.Direction);
    }

    [Fact]
    public void A_low_route_is_limited_to_FL180_and_the_level_comes_from_the_minimum_altitude()
    {
        var b = new WorldNetworkBuilder();
        b.AddLinks(Pt("A", "EG", 51, 0), [Link("V12", null, ("B", "EG", 51, 1, 1981), 1)]);              // 6 500 ft
        var s = Assert.Single(b.Build("t", DateTimeOffset.Now).Segments);
        Assert.Equal(65, s.LowerFl);
        Assert.Equal(180, s.UpperFl);
        Assert.True(s.AllowsFlightLevel(100));
        Assert.False(s.AllowsFlightLevel(240));
        Assert.Null(WorldNetworkBuilder.Fl(0));
    }

    [Fact]
    public void An_empty_region_on_a_neighbour_means_the_region_of_the_point_that_names_it()
    {
        var b = new WorldNetworkBuilder();
        var next = b.AddLinks(Pt("A", "EG", 51, 0), [Link("UL9", null, ("B", "", 51, 1, 5944))]);
        Assert.Equal("EG", next.Single().Region);
        Assert.Contains(b.Build("t", DateTimeOffset.Now).Fixes, f => f.Ident == "B/EG");
    }

    [Fact]
    public void The_same_name_in_two_regions_gives_two_distinct_points()
    {
        var b = new WorldNetworkBuilder();
        b.AddLinks(Pt("ABCDE", "EG", 51, 0), [Link("UL9", null, ("N1", "EG", 51, 1, 0))]);
        b.AddLinks(Pt("ABCDE", "ED", 50, 8), [Link("UL10", null, ("N2", "ED", 50, 9, 0))]);
        var idents = b.Build("t", DateTimeOffset.Now).Fixes.Select(f => f.Ident).ToList();
        Assert.Contains("ABCDE/EG", idents);
        Assert.Contains("ABCDE/ED", idents);
    }

    [Fact]
    public void A_point_already_seen_is_never_asked_for_again()
    {
        var b = new WorldNetworkBuilder();
        var seeds = b.Seed([Pt("A", "EG", 51, 0), Pt("A", "EG", 51, 0)]);
        Assert.Single(seeds);
        var next = b.AddLinks(Pt("B", "EG", 51, 1), [Link("UL9", ("A", "EG", 51, 0, 0), null)]);
        Assert.Empty(next);                                                                               // A est une graine : déjà vue
    }

    // ---------- fichier ----------

    [Fact]
    public void The_network_file_is_read_back_identically()
    {
        var b = new WorldNetworkBuilder();
        b.AddAirport("EGLL", 51.47, -0.45);
        b.AddLinks(Pt("MID", "EG", 51, 0, SimPointKind.Vor), [Link("UL9", ("PRV", "EG", 51, -1, 5944), ("NXT", "EG", 51, 1, 1981), 3, 'N', 'W')]);
        var written = b.Build("MSFS 12.2", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_dir, "net.bin.gz");
        WorldNetworkStore.Write(path, written);

        var back = WorldNetworkStore.Read(path, out var problem)!;
        Assert.Null(problem);
        Assert.Equal("MSFS 12.2", back.SimVersion);
        Assert.Equal(written.ReadAt, back.ReadAt);
        Assert.Equal(written.Fixes.Count, back.Fixes.Count);
        Assert.Equal(FixKind.Airport, back.Fixes.Single(f => f.Ident == "EGLL").Kind);
        Assert.Equal(FixKind.Vor, back.Fixes.Single(f => f.Ident == "MID/EG").Kind);
        Assert.Equal(FixKind.Ndb, back.Fixes.Single(f => f.Ident == "PRV/EG").Kind);
        Assert.Equal(-0.45, back.Fixes.Single(f => f.Ident == "EGLL").Lon);
        Assert.Equal(1, back.Airports);
        Assert.Equal(written.Segments.Count, back.Segments.Count);
        Assert.All(back.Segments, s => Assert.Equal("UL9", s.Airway));
        Assert.Equal(written.Segments.Select(s => (s.From, s.To, s.LowerFl, s.UpperFl)).Order(), back.Segments.Select(s => (s.From, s.To, s.LowerFl, s.UpperFl)).Order());
    }

    [Theory]
    [InlineData("pas un fichier")]
    public void A_damaged_network_file_is_refused_with_a_reason(string text)
    {
        string path = Path.Combine(_dir, "bad.bin.gz");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, text);
        Assert.Null(WorldNetworkStore.Read(path, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.Null(WorldNetworkStore.Read(Path.Combine(_dir, "absent.bin.gz"), out var none));
        Assert.Null(none);
    }

    // ---------- fusion avec le SIA ----------

    [Fact]
    public void A_border_point_of_MSFS_that_the_SIA_already_has_keeps_the_SIA_key()
    {
        var sia = new IfrNavData([new IfrFix("BORDR", 50.0, 3.0), new IfrFix("INFRA", 49.5, 2.5)], [new AirwaySegment("UL9", "INFRA", "BORDR")], [], "2611");
        var world = new WorldNetwork("t", DateTimeOffset.Now,
            [new IfrFix("BORDR/EB", 50.001, 3.001), new IfrFix("FAR/EB", 51.0, 4.0), new IfrFix("OTHER/EB", 10, 10)],
            [new AirwaySegment("UL9", "BORDR/EB", "FAR/EB")]);

        var merged = WorldNetworkMerge.Apply(sia, world);
        Assert.Null(merged.Find("BORDR/EB"));                                  // même point : pas de doublon
        Assert.NotNull(merged.Find("FAR/EB"));
        Assert.Contains(merged.EdgesFrom("BORDR"), e => e.To == "FAR/EB");     // la route continue au-delà de la frontière
        Assert.Contains(merged.EdgesFrom("BORDR"), e => e.To == "INFRA");
        Assert.Equal("2611", merged.AiracCycle);
    }

    [Fact]
    public void A_same_name_far_away_is_a_different_point_and_does_not_join_the_networks()
    {
        var sia = new IfrNavData([new IfrFix("ABCDE", 48, 2), new IfrFix("NEAR", 48.5, 2.5)], [new AirwaySegment("UL9", "ABCDE", "NEAR")], [], "2611");
        var world = new WorldNetwork("t", DateTimeOffset.Now, [new IfrFix("ABCDE/EG", 51, 0), new IfrFix("X/EG", 51.5, 0.5)], [new AirwaySegment("UL1", "ABCDE/EG", "X/EG")]);
        var merged = WorldNetworkMerge.Apply(sia, world);
        Assert.NotNull(merged.Find("ABCDE"));
        Assert.NotNull(merged.Find("ABCDE/EG"));
        Assert.DoesNotContain(merged.EdgesFrom("ABCDE"), e => e.To == "X/EG");
        Assert.Contains(merged.EdgesFrom("ABCDE/EG"), e => e.To == "X/EG");
    }

    [Fact]
    public void A_route_can_be_planned_between_two_airports_outside_France_with_a_displayed_name_without_region()
    {
        var airports = new IfrFix[] { new("EGLL", 51.47, -0.45, FixKind.Airport), new("EDDF", 50.03, 8.57, FixKind.Airport) };
        var world = new WorldNetwork("t", DateTimeOffset.Now,
            [.. airports, new("LON/EG", 51.5, 0.0, FixKind.Vor), new("ABCDE/EB", 50.8, 4.0), new("ZZZZZ/ED", 50.0, 7.0)],
            [new AirwaySegment("UL9", "LON/EG", "ABCDE/EB", AirwayDirection.Both, 195), new AirwaySegment("UL9", "ABCDE/EB", "ZZZZZ/ED", AirwayDirection.Both, 195)]);
        var data = WorldNetworkMerge.Apply(new IfrNavData([], [], [], "2611"), world);

        var result = IfrRouter.Plan(data, "EGLL", "EDDF", new IfrPlanOptions { FlightLevel = 300 });
        Assert.True(result.Success, result.Error);
        Assert.Contains("UL9", result.Route!.Description);
        Assert.DoesNotContain("/", result.Route.Description);                  // « LON », pas « LON/EG »
        Assert.All(result.Route.ToWaypoints(), w => Assert.DoesNotContain('/', w.Name));
        Assert.Equal("EGLL", result.Route.ToWaypoints()[0].Icao);
    }
}
