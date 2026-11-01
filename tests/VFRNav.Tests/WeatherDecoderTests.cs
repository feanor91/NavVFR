using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class WeatherDecoderTests
{
    private static string Text(DecodedReport r) => string.Join("\n", r.Lines);

    [Fact]
    public void Metar_basic_fields_are_translated()
    {
        var r = WeatherDecoder.DecodeMetar("METAR LFPO 311030Z AUTO 27010G22KT 240V300 9999 -RA SCT025 BKN040 12/08 Q1013 NOSIG");
        var t = Text(r);
        Assert.Equal("LFPO", r.Station);
        Assert.Contains("le 31 à 10:30 UTC", r.Header);
        Assert.Contains("Observation automatique", t);
        Assert.Contains("Vent du 270° (ouest), 10 kt (19 km/h), rafales à 22 kt", t);
        Assert.Contains("entre 240° et 300°", t);
        Assert.Contains("Visibilité : 10 km ou plus", t);
        Assert.Contains("Pluie faible", t);
        Assert.Contains("Nuages épars (3 à 4/8) à 2500 ft", t);
        Assert.Contains("Nuages fragmentés (5 à 7/8) à 4000 ft", t);
        Assert.Contains("Température 12 °C, point de rosée 8 °C", t);
        Assert.Contains("QNH 1013 hPa", t);
        Assert.Contains("pas de changement significatif", t);
        Assert.DoesNotContain("Non décodé", t);
    }

    [Fact]
    public void Metar_cavok_calm_and_negative_temperatures()
    {
        var r = WeatherDecoder.DecodeMetar("LFPG 011200Z 00000KT CAVOK M02/M04 Q1030");
        var t = Text(r);
        Assert.Contains("Vent : calme", t);
        Assert.Contains("CAVOK", t);
        Assert.Contains("Température -2 °C, point de rosée -4 °C", t);
        Assert.Equal("VFR", r.Category);
    }

    [Theory]
    [InlineData("LFXX 011200Z 18005KT 9999 BKN040 10/05 Q1015", "VFR")]
    [InlineData("LFXX 011200Z 18005KT 9999 BKN025 10/05 Q1015", "MVFR")]
    [InlineData("LFXX 011200Z 18005KT 3000 BKN040 10/05 Q1015", "IFR")]
    [InlineData("LFXX 011200Z 18005KT 0800 FG VV001 10/10 Q1015", "LIFR")]
    public void Metar_flight_category(string raw, string expected) =>
        Assert.Equal(expected, WeatherDecoder.DecodeMetar(raw).Category);

    [Theory]
    [InlineData("+TSRA", "Orage avec pluie forte")]
    [InlineData("-SHRA", "Averses de pluie faible")]
    [InlineData("FZFG", "Brouillard givrant")]
    [InlineData("BR", "Brume")]
    [InlineData("VCSH", "Averses à proximité")]
    public void Weather_phenomena(string code, string expectedStart)
    {
        var t = Text(WeatherDecoder.DecodeMetar($"LFXX 011200Z 18005KT 5000 {code} 10/05 Q1015"));
        Assert.Contains(expectedStart, t);
    }

    [Fact]
    public void Unknown_groups_are_reported_not_guessed()
    {
        var t = Text(WeatherDecoder.DecodeMetar("LFPO 311030Z 27010KT 9999 ZZZ123 12/08 Q1013"));
        Assert.Contains("Non décodé : ZZZ123", t);
    }

    [Fact]
    public void Metar_trend_becmg()
    {
        var t = Text(WeatherDecoder.DecodeMetar("LFPO 311030Z 27010KT 9999 SCT030 12/08 Q1013 BECMG 4000 RA RMK blabla"));
        Assert.Contains("évolution durable", t);
        Assert.Contains("Visibilité : 4 km", t);
        Assert.DoesNotContain("blabla", t);
    }

    [Fact]
    public void Taf_periods_are_split_and_labelled()
    {
        var r = WeatherDecoder.DecodeTaf(
            "TAF LFPG 311100Z 3112/0112 27010KT 9999 SCT030 TX15/3114Z TN08/0105Z BECMG 3118/3120 20005KT TEMPO 3114/3118 4000 RA PROB30 0104/0108 1500 FG FM010900 25012KT CAVOK");
        var t = Text(r);
        Assert.Equal("LFPG", r.Station);
        Assert.Contains("Valable du 31 à 12h au 01 à 12h UTC", t);
        Assert.Contains("Prévision de base :", t);
        Assert.Contains("Évolution progressive du 31 à 18h au 31 à 20h UTC :", t);
        Assert.Contains("Variations temporaires du 31 à 14h au 31 à 18h UTC :", t);
        Assert.Contains("Probabilité 30 %", t);
        Assert.Contains("À partir du 01 à 09:00 UTC :", t);
        Assert.Contains("Température maximale 15 °C le 31 à 14h UTC", t);
        Assert.Contains("Température minimale 8 °C le 01 à 05h UTC", t);
        Assert.DoesNotContain("Non décodé", t);
    }

    [Fact]
    public void Tafs_split_multi_line_text()
    {
        var list = WeatherDecoder.DecodeTafs("TAF LFPG 311100Z 3112/0112 27010KT 9999 SCT030\n  BECMG 3118/3120 20005KT\nTAF LFPO 311100Z 3112/0112 CAVOK");
        Assert.Equal(2, list.Count);
        Assert.Equal("LFPG", list[0].Station);
        Assert.Equal("LFPO", list[1].Station);
        Assert.Contains("Évolution progressive", Text(list[0]));
    }

    [Fact]
    public void Metars_split_one_per_line()
    {
        var list = WeatherDecoder.DecodeMetars("METAR LFPG 311030Z 27010KT CAVOK 12/08 Q1013\nMETAR LFPO 311030Z 00000KT CAVOK 12/08 Q1013\n");
        Assert.Equal(["LFPG", "LFPO"], list.Select(r => r.Station).ToArray());
    }
}
