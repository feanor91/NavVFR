using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class VerticalPlannerTests
{
    private static Aircraft A320() => new()
    {
        Category = AircraftCategory.Jet, CruiseKt = 447, ServiceCeilingFt = 39800, CruiseAltFt = 35000,
        V1Kt = 140, VrKt = 143, V2Kt = 147,
    };

    [Fact]
    public void A_short_route_cannot_reach_a_high_level_and_gets_the_highest_reachable_one()
    {
        var plan = VerticalPlanner.Compute(215, 392, 80, 35000, A320());   // LFPG → EGLL, route comprise : environ 215 NM

        Assert.True(plan.Limited);
        Assert.True(plan.CruiseFt < 35000);
        Assert.InRange(plan.CruiseFt, 20000, 33000);
        Assert.Equal(0, plan.CruiseFt % 1000);
        // La montée et la descente tiennent dans la distance.
        Assert.True(plan.TopOfClimbNm <= plan.TopOfDescentNm);
        Assert.True(plan.TopOfDescentNm <= plan.DistanceNm);
        Assert.True(plan.TopOfClimbNm + plan.TopOfDescentFromArrivalNm <= plan.DistanceNm - VerticalPlanner.TerminalAllowanceNm + 1);
    }

    [Fact]
    public void A_long_route_keeps_the_requested_level_and_places_toc_and_tod()
    {
        var plan = VerticalPlanner.Compute(900, 392, 12, 35000, A320());

        Assert.False(plan.Limited);
        Assert.Equal(35000, plan.CruiseFt);
        Assert.Equal((35000 - 392) / A320().ClimbFtPerNm, plan.TopOfClimbNm, 3);
        Assert.Equal(900 - (35000 - 12) / A320().DescentFtPerNm, plan.TopOfDescentNm, 3);
        Assert.True(plan.ClimbMinutes > 15 && plan.ClimbMinutes < 30);
        Assert.True(plan.DescentMinutes > 10 && plan.DescentMinutes < 25);
    }

    [Fact]
    public void The_service_ceiling_limits_the_level_even_on_a_long_route()
    {
        var low = A320();
        low.ServiceCeilingFt = 25000;
        var plan = VerticalPlanner.Compute(900, 0, 0, 35000, low);
        Assert.True(plan.Limited);
        Assert.True(plan.CruiseFt <= 25000);
    }

    [Theory]
    [InlineData(27400, 90, 27000)]    // vers l'est : milliers impairs
    [InlineData(27400, 270, 26000)]   // vers l'ouest : milliers pairs
    [InlineData(26200, 90, 25000)]
    [InlineData(26200, 270, 26000)]
    [InlineData(2400, 270, 2000)]     // sous 3 000 ft : pas de règle
    public void Levels_follow_the_semicircular_rule(double ft, double track, double expected) =>
        Assert.Equal(expected, VerticalPlanner.RoundDownToLevel(ft, track));

    [Fact]
    public void Without_a_track_levels_are_simply_rounded_down_to_the_thousand() =>
        Assert.Equal(27000, VerticalPlanner.RoundDownToLevel(27999, null));

    [Fact]
    public void A_longer_route_reaches_higher()
    {
        double Reach(double nm) => VerticalPlanner.MaxReachableFt(nm, 0, 0, A320());
        Assert.True(Reach(150) < Reach(250));
        Assert.True(Reach(250) < Reach(400));
    }

    [Fact]
    public void A_route_too_short_to_climb_stays_low_instead_of_returning_nonsense()
    {
        var plan = VerticalPlanner.Compute(12, 392, 80, 35000, A320());
        Assert.True(plan.CruiseFt < 6000);
        Assert.True(plan.TopOfClimbNm <= plan.TopOfDescentNm + 0.001);
    }

    [Fact]
    public void Level_text_uses_flight_levels_from_ten_thousand_feet()
    {
        Assert.Equal("FL260", VerticalPlanner.LevelText(26000));
        Assert.Equal("FL100", VerticalPlanner.LevelText(10000));
        Assert.Contains("5", VerticalPlanner.LevelText(5500));
        Assert.EndsWith("ft", VerticalPlanner.LevelText(5500));
    }

    [Fact]
    public void Takeoff_speeds_line_lists_the_known_speeds_and_is_null_without_any()
    {
        Assert.Contains("V1 140", VerticalPlanner.TakeoffSpeedsLine(A320())!);
        Assert.Contains("VR 143", VerticalPlanner.TakeoffSpeedsLine(A320())!);
        Assert.Contains("V2 147", VerticalPlanner.TakeoffSpeedsLine(A320())!);
        Assert.Null(VerticalPlanner.TakeoffSpeedsLine(new Aircraft()));
        Assert.DoesNotContain("V1", VerticalPlanner.TakeoffSpeedsLine(new Aircraft { VrKt = 55 })!);
    }

    [Fact]
    public void Summary_lines_say_whether_the_level_is_limited_and_give_toc_and_tod()
    {
        var limited = VerticalPlanner.Lines(VerticalPlanner.Compute(215, 392, 80, 35000, A320()), "LFPG", "EGLL", "Airbus A320neo");
        Assert.Equal(3, limited.Count);
        Assert.Contains("FL350", limited[0]);
        Assert.Contains("Airbus A320neo", limited[0]);
        Assert.Contains("TOC", limited[1]);
        Assert.Contains("LFPG", limited[1]);
        Assert.Contains("TOD", limited[2]);
        Assert.Contains("EGLL", limited[2]);

        var fine = VerticalPlanner.Lines(VerticalPlanner.Compute(900, 0, 0, 35000, A320()), "LFPG", "LFMN", "Airbus A320neo");
        Assert.DoesNotContain("Airbus", fine[0]);
    }

    [Fact]
    public void Aircraft_with_a_non_positive_takeoff_speed_is_rejected()
    {
        var a = new Aircraft { Name = "X", V1Kt = 0 };
        Assert.Contains(a.Validate(), e => e.Contains("V1"));
    }

    [Fact]
    public void Top_of_descent_moves_before_a_star_point_constrained_below_the_cruise_level()
    {
        // 300 NM ; croisière FL350 ; le planificateur placerait le TOD à environ 190 NM du départ.
        var plan = VerticalPlanner.Compute(300, 0, 0, 35000, A320());
        Assert.False(plan.Limited);

        // Entrée de STAR à 270 NM, contrainte à 24 000 ft : la descente doit commencer assez tôt pour y arriver à ce niveau.
        var adjusted = plan.WithTopOfDescentBefore([(0, null), (270, 24000), (290, 9000), (300, null)]);

        Assert.True(adjusted.TopOfDescentNm <= 270 - (35000 - 24000) / A320().DescentFtPerNm + 0.001);
        Assert.True(adjusted.TopOfDescentNm >= adjusted.TopOfClimbNm);
        Assert.Equal(plan.CruiseFt, adjusted.CruiseFt);
    }

    [Fact]
    public void Top_of_descent_is_unchanged_when_no_point_is_constrained_below_the_cruise_or_before_the_top_of_climb()
    {
        var plan = VerticalPlanner.Compute(900, 0, 0, 35000, A320());
        Assert.Equal(plan.TopOfDescentNm, plan.WithTopOfDescentBefore([(0, null), (450, null), (900, null)]).TopOfDescentNm);
        Assert.Equal(plan.TopOfDescentNm, plan.WithTopOfDescentBefore([(5, 3000), (60, 8000)]).TopOfDescentNm);   // contraintes de SID, avant la fin de la montée
        Assert.Equal(plan.TopOfDescentNm, plan.WithTopOfDescentBefore([(500, 34800)]).TopOfDescentNm);          // presque la croisière : sans effet
    }
}
