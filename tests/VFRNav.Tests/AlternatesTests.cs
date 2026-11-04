using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class AlternatesTests
{
    private static readonly List<Airport> Airports =
    [
        new("LFMN", "Nice Côte d'Azur", "large_airport", 43.6584, 7.2159, 12),
        new("LFML", "Marseille Provence", "large_airport", 43.4393, 5.2214, 69),
        new("LFMD", "Cannes Mandelieu", "medium_airport", 43.5420, 6.9534, 13),
    ];

    private static readonly FlightParameters P = new(120, 40, 3000, 0, 0, 0);

    [Fact]
    public void Parse_reads_codes_separated_by_spaces_commas_or_slashes_without_duplicates()
    {
        Assert.Equal(["LFML", "LFMD", "LFMN"], Alternates.Parse(" lfml, LFMD/lfml ;LFMN "));
        Assert.Empty(Alternates.Parse(null));
        Assert.Empty(Alternates.Parse("  "));
    }

    [Fact]
    public void Alternates_are_measured_from_the_arrival_with_the_aircraft_speed_and_fuel()
    {
        var from = Airports[0].ToWaypoint();
        var result = Alternates.For(from, ["LFMD", "LFML", "LFXX"], Airports, P);

        Assert.Equal(3, result.Count);
        var cannes = result[0];
        Assert.True(cannes.Known);
        Assert.InRange(cannes.DistanceNm, 10, 20);                // Nice → Cannes : une quinzaine de NM
        Assert.InRange(cannes.TrueCourse, 230, 270);              // vers l'ouest-sud-ouest
        Assert.Equal(cannes.DistanceNm / 120 * 40, cannes.FuelL, 0.5);
        Assert.Contains("LFMD", cannes.Line);
        Assert.Contains("NM", cannes.Line);

        Assert.InRange(result[1].DistanceNm, 80, 100);            // Nice → Marseille
        Assert.False(result[2].Known);
        Assert.Contains("inconnu", result[2].Line);
    }
}
