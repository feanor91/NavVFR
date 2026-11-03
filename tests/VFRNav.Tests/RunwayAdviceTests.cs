using VFRNav.Core.Ifr;

namespace VFRNav.Tests;

public class RunwayAdviceTests
{
    [Theory]
    [InlineData("26L", 260)]
    [InlineData("08R", 80)]
    [InlineData("8", 80)]
    [InlineData("36", 360)]
    [InlineData("27C", 270)]
    [InlineData("01", 10)]
    public void A_runway_name_gives_its_magnetic_heading(string runway, int heading) => Assert.Equal(heading, RunwayAdvice.HeadingOf(runway));

    [Theory]
    [InlineData("")]
    [InlineData("RW26")]
    [InlineData("37")]
    [InlineData("00")]
    [InlineData("26X")]
    public void Something_that_is_not_a_runway_has_no_heading(string text) => Assert.Null(RunwayAdvice.HeadingOf(text));

    [Fact]
    public void The_runway_most_into_the_wind_comes_first_with_its_components()
    {
        var ranking = RunwayAdvice.Rank(["08R", "26L", "26R"], new WindInfo(240, 15));
        Assert.Equal(["26L", "26R", "08R"], ranking.Select(r => r.Runway));       // parallèles : l'ordre donné est gardé
        var best = ranking[0];
        Assert.Equal(15 * Math.Cos(20 * Math.PI / 180), best.HeadwindKt, 1);       // vent à 20° de l'axe
        Assert.Equal(15 * Math.Sin(20 * Math.PI / 180), best.CrosswindKt, 1);
        Assert.True(ranking[^1].HeadwindKt < 0);                                    // la piste opposée a du vent arrière
        Assert.Contains("vent arrière", ranking[^1].Describe());
        Assert.Contains("vent de face", best.Describe());
    }

    [Fact]
    public void A_light_or_variable_wind_does_not_choose_a_runway()
    {
        Assert.Empty(RunwayAdvice.Rank(["26L", "08R"], new WindInfo(240, 2)));
        Assert.Empty(RunwayAdvice.Rank(["26L", "08R"], new WindInfo(null, 12)));
        Assert.False(new WindInfo(null, 12).Decides);
        Assert.True(new WindInfo(240, 3).Decides);
    }

    [Fact]
    public void The_true_wind_is_turned_into_magnetic_with_the_variation()
    {
        // Vent vrai du 275° et variation 10° Est : vent magnétique 265°. Entre les pistes 26 (260°) et 28 (280°), c'est la 26 qui gagne.
        Assert.Equal("26", RunwayAdvice.Rank(["28", "26"], new WindInfo(275, 12), variationEastDeg: 10)[0].Runway);
        // Sans variation, le même vent vrai est plus proche de la 28.
        Assert.Equal("28", RunwayAdvice.Rank(["28", "26"], new WindInfo(275, 12), variationEastDeg: 0)[0].Runway);
    }

    [Fact]
    public void Names_that_are_not_runways_are_ignored_by_the_ranking()
    {
        Assert.Equal(["26L"], RunwayAdvice.Rank(["RWY", "26L"], new WindInfo(260, 10)).Select(r => r.Runway));
    }

    [Fact]
    public void Procedures_are_filtered_by_runway_and_those_without_runway_serve_all()
    {
        IfrProcedure P(string ident, params string[] runways) =>
            new(ProcedureKind.Sid, "LFPG", ident, [new ProcedureVariant(runways, [new ProcedureLeg("ABCDE")])]);
        var all = new[] { P("A1", "26L", "26R"), P("B1", "08L"), P("C1") };

        Assert.Equal(["A1", "C1"], RunwayAdvice.ForRunway(all, "26L").Select(p => p.Ident));
        Assert.Equal(["B1", "C1"], RunwayAdvice.ForRunway(all, "08L").Select(p => p.Ident));
        Assert.Equal(3, RunwayAdvice.ForRunway(all, null).Count());
        Assert.Equal(["08L", "26L", "26R"], RunwayAdvice.RunwaysOf(all));
    }

    [Theory]
    [InlineData("METAR LFPG 031000Z 24012KT 9999 FEW040 12/08 Q1020 NOSIG", 240, 12, null)]
    [InlineData("LFPG 031000Z 24012G25KT 9999 FEW040 12/08 Q1020", 240, 12, 25.0)]
    [InlineData("SPECI LFPG 031000Z 360005KT CAVOK 12/08 Q1020", 360, 5, null)]
    [InlineData("METAR UUEE 031000Z 24005MPS 9999 FEW040 12/08 Q1020", 240, 9.7, null)]
    public void The_wind_is_read_from_a_raw_metar(string raw, int direction, double speed, double? gust)
    {
        string icao = raw.Split(' ').First(t => t.Length == 4 && char.IsLetter(t[0]) && t != "SPECI" && t != "METAR");
        var wind = RunwayAdvice.ParseMetar(raw, icao)!;
        Assert.Equal(direction, wind.DirectionTrueDeg);
        Assert.Equal(speed, wind.SpeedKt, 1);
        Assert.Equal(gust, wind.GustKt);
    }

    [Fact]
    public void A_variable_wind_is_read_but_decides_nothing_and_the_right_airport_is_picked()
    {
        const string raw = "METAR EGLL 031020Z 24015KT 9999 FEW040 12/08 Q1020\nMETAR LFPG 031000Z VRB03KT 9999 12/08 Q1020";
        var vrb = RunwayAdvice.ParseMetar(raw, "LFPG")!;
        Assert.Null(vrb.DirectionTrueDeg);
        Assert.False(vrb.Decides);
        Assert.Equal(240, RunwayAdvice.ParseMetar(raw, "EGLL")!.DirectionTrueDeg);
        Assert.Null(RunwayAdvice.ParseMetar(raw, "LFMN"));                           // terrain absent
        Assert.Null(RunwayAdvice.ParseMetar("METAR LFPG 031000Z AUTO 9999 12/08", "LFPG")); // pas de vent dans le message
        Assert.Null(RunwayAdvice.ParseMetar("", "LFPG"));
    }
}
