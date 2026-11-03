using VFRNav.Core.Ifr;

namespace VFRNav.Tests;

public class SimProceduresTests
{
    private static FacilityLeg Leg(string fix, double lat = 49, double lon = 2, int desc = 0, double alt1 = 0, double alt2 = 0, double speed = 0, int type = 15) =>
        new(type, fix, lat, lon, desc, alt1, alt2, speed);

    private static FacilityTransition Runway(string name, params FacilityLeg[] legs)
    {
        var t = new FacilityTransition { Runway = name, Name = name };
        t.Legs.AddRange(legs);
        return t;
    }

    private static FacilityTransition Enroute(string name, params FacilityLeg[] legs)
    {
        var t = new FacilityTransition { Name = name };
        t.Legs.AddRange(legs);
        return t;
    }

    [Fact]
    public void Runway_names_have_two_digits_and_a_letter()
    {
        Assert.Equal("26L", SimProcedures.RunwayName(26, 1));
        Assert.Equal("08R", SimProcedures.RunwayName(8, 2));
        Assert.Equal("09", SimProcedures.RunwayName(9, 0));
        Assert.Equal("27C", SimProcedures.RunwayName(27, 3));
        Assert.Equal("", SimProcedures.RunwayName(0, 0));
    }

    [Fact]
    public void A_sid_gives_one_variant_per_runway_and_exit_in_the_order_runway_common_exit()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "AMB1A" };
        sid.Common.Add(Leg("COM01"));
        sid.RunwayTransitions.Add(Runway("26L", Leg("RW26L"), Leg("R26A")));
        sid.RunwayTransitions.Add(Runway("08R", Leg("RW08R")));
        sid.EnrouteTransitions.Add(Enroute("AMBOP", Leg("EXIT1"), Leg("AMBOP")));
        sid.EnrouteTransitions.Add(Enroute("TUSKY", Leg("TUSKY")));

        var p = Assert.Single(SimProcedures.ToIfr("LFPG", [sid]).Procedures);
        Assert.Equal((ProcedureKind.Sid, "LFPG", "AMB1A"), (p.Kind, p.Airport, p.Ident));
        Assert.Equal(4, p.Variants.Count); // 2 pistes × 2 sorties
        var first = p.Variants[0];
        Assert.Equal(["26L"], first.Runways);
        Assert.Equal(["RW26L", "R26A", "COM01", "EXIT1", "AMBOP"], first.Legs.Select(l => l.Fix));
        Assert.Equal("TUSKY", p.Variants.Single(v => v.Runways.SequenceEqual(["08R"]) && v.Legs[^1].Fix == "TUSKY").Legs[^1].Fix);
    }

    [Fact]
    public void A_star_goes_from_the_entry_to_the_runway()
    {
        var star = new FacilityProcedure { Kind = ProcedureKind.Star, Name = "NIZ1R" };
        star.EnrouteTransitions.Add(Enroute("MEDIL", Leg("MEDIL"), Leg("LAT01")));
        star.Common.Add(Leg("COM02"));
        star.RunwayTransitions.Add(Runway("04R", Leg("IAF04"), Leg("FIN04")));

        var v = Assert.Single(Assert.Single(SimProcedures.ToIfr("LFMN", [star]).Procedures).Variants);
        Assert.Equal(["04R"], v.Runways);
        Assert.Equal(["MEDIL", "LAT01", "COM02", "IAF04", "FIN04"], v.Legs.Select(l => l.Fix));
    }

    [Fact]
    public void A_procedure_without_transitions_uses_its_common_points_for_every_runway()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "SIMPLE" };
        sid.Common.AddRange([Leg("A1"), Leg("B2")]);
        var v = Assert.Single(Assert.Single(SimProcedures.ToIfr("LFXX", [sid]).Procedures).Variants);
        Assert.Empty(v.Runways);          // vide : toutes les pistes
        Assert.True(v.AppliesTo("12"));
    }

    [Fact]
    public void A_joining_point_that_ends_one_part_and_starts_the_next_appears_once()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "JOIN1A" };
        sid.RunwayTransitions.Add(Runway("26L", Leg("RW26L"), Leg("MID01")));
        sid.Common.AddRange([Leg("MID01"), Leg("END02")]);
        var v = Assert.Single(Assert.Single(SimProcedures.ToIfr("LFPG", [sid]).Procedures).Variants);
        Assert.Equal(["RW26L", "MID01", "END02"], v.Legs.Select(l => l.Fix));
    }

    [Fact]
    public void Legs_without_a_named_fix_or_position_are_skipped()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "VA1A" };
        sid.RunwayTransitions.Add(Runway("26L", Leg("", type: 2), Leg("NOPOS", lat: 0, lon: 0), Leg("GOOD1")));
        var v = Assert.Single(Assert.Single(SimProcedures.ToIfr("LFPG", [sid]).Procedures).Variants);
        Assert.Equal(["GOOD1"], v.Legs.Select(l => l.Fix));
    }

    [Theory]
    [InlineData(1, 1524.0, 0.0, 5000, 5000)]     // « à » 5 000 ft
    [InlineData(2, 1524.0, 0.0, 5000, null)]     // « au moins »
    [InlineData(3, 1524.0, 0.0, null, 5000)]     // « au plus »
    [InlineData(0, 1524.0, 0.0, null, null)]     // aucune contrainte
    public void Altitude_constraints_are_converted_from_metres_to_feet(int desc, double alt1, double alt2, int? min, int? max)
    {
        var star = new FacilityProcedure { Kind = ProcedureKind.Star, Name = "ALT1A" };
        star.Common.Add(Leg("FIX01", desc: desc, alt1: alt1, alt2: alt2));
        var leg = Assert.Single(Assert.Single(Assert.Single(SimProcedures.ToIfr("LFMN", [star]).Procedures).Variants).Legs);
        Assert.Equal(min, leg.MinAltFt);
        Assert.Equal(max, leg.MaxAltFt);
    }

    [Fact]
    public void A_between_constraint_uses_both_altitudes_and_the_speed_limit_is_kept()
    {
        var star = new FacilityProcedure { Kind = ProcedureKind.Star, Name = "BTW1A" };
        star.Common.Add(Leg("FIX01", desc: 4, alt1: 3048, alt2: 1524, speed: 220.4));
        var leg = Assert.Single(Assert.Single(Assert.Single(SimProcedures.ToIfr("LFMN", [star]).Procedures).Variants).Legs);
        Assert.Equal(5000, leg.MinAltFt);
        Assert.Equal(10000, leg.MaxAltFt);
        Assert.Equal(220, leg.SpeedKt);
    }

    [Fact]
    public void The_fixes_used_by_the_procedures_are_returned_with_their_coordinates()
    {
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "COORD1" };
        sid.Common.Add(Leg("ABCDE", lat: 48.5, lon: 2.25));
        var import = SimProcedures.ToIfr("LFPG", [sid]);
        var fix = Assert.Single(import.Fixes);
        Assert.Equal(("ABCDE", 48.5, 2.25), (fix.Ident, fix.Lat, fix.Lon));
    }

    [Fact]
    public void Merging_replaces_the_airport_procedures_and_keeps_the_existing_fix_positions()
    {
        var existing = new IfrNavData(
            [new IfrFix("ABCDE", 10, 10), new IfrFix("LFPG", 49, 2.5, FixKind.Airport)], [],
            [new IfrProcedure(ProcedureKind.Sid, "LFPG", "OLD1A", [new ProcedureVariant([], [new ProcedureLeg("ABCDE")])]),
             new IfrProcedure(ProcedureKind.Sid, "LFPO", "KEEP1A", [new ProcedureVariant([], [new ProcedureLeg("ABCDE")])])],
            "2611");
        var sid = new FacilityProcedure { Kind = ProcedureKind.Sid, Name = "NEW1A" };
        sid.Common.AddRange([Leg("ABCDE", lat: 20, lon: 20), Leg("FGHIJ", lat: 48, lon: 3)]);

        var merged = SimProcedures.Merge(existing, "LFPG", SimProcedures.ToIfr("LFPG", [sid]));

        Assert.Equal(["NEW1A"], merged.ProceduresFor("LFPG", ProcedureKind.Sid).Select(p => p.Ident));
        Assert.Equal(["KEEP1A"], merged.ProceduresFor("LFPO", ProcedureKind.Sid).Select(p => p.Ident));
        Assert.Equal(10, merged.Find("ABCDE")!.Lat);   // la position de la base est conservée
        Assert.Equal(48, merged.Find("FGHIJ")!.Lat);   // le point manquant est ajouté
        Assert.Equal("2611", merged.AiracCycle);
    }
}
