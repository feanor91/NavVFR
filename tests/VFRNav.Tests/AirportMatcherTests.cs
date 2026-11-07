using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class AirportMatcherTests
{
    private static Airport Apt(string ident, string name, double lat, double lon, string? sim = null) =>
        new(ident, name, "small_airport", lat, lon, 100, SimIdent: sim, City: "X", Country: "GB");

    private static readonly Airport[] Airports =
    [
        Apt("GB-0309", "Haydock Park Airstrip", 53.4783, -2.6222),
        Apt("EGLL", "London Heathrow Airport", 51.4706, -0.4619),
        Apt("GB-0001", "Autre piste", 53.4900, -2.6222),
    ];

    [Fact]
    public void An_msfs_code_unknown_to_ourairports_is_matched_to_the_airport_at_the_same_place()
    {
        var map = AirportMatcher.Match(Airports, [("EGGL", 53.478, -2.621)]);
        Assert.Equal("EGGL", map["GB-0309"]);
        Assert.Single(map);
    }

    [Fact]
    public void Codes_already_known_and_airports_too_far_away_are_not_matched()
    {
        var map = AirportMatcher.Match(Airports, [("EGLL", 51.4706, -0.4619), ("EGZZ", 53.60, -2.62), ("EGYY", 40, 10)]);
        Assert.Empty(map);
    }

    [Fact]
    public void Only_the_nearest_msfs_code_is_kept_for_an_airport()
    {
        var map = AirportMatcher.Match(Airports, [("EGAA", 53.4790, -2.6230), ("EGAB", 53.4783, -2.6222)]);
        Assert.Equal("EGAB", map["GB-0309"]);
    }

    [Fact]
    public void Search_finds_an_airport_by_its_msfs_code_as_well_as_by_its_name()
    {
        var withAlias = Airports.Select(a => a.Ident == "GB-0309" ? a with { SimIdent = "EGGL" } : a).ToList();
        var index = new AirportSearchIndex(withAlias);
        Assert.Equal("GB-0309", index.Find("EGGL")[0].Ident);
        Assert.Equal("GB-0309", index.Find("eggl")[0].Ident);
        Assert.Equal("GB-0309", index.Find("Haydock")[0].Ident);
        Assert.Equal("GB-0309", index.Find("EGG")[0].Ident);   // début du code MSFS
    }

    [Fact]
    public void A_name_after_a_hyphen_counts_as_a_word_so_paris_orly_is_found_by_orly_and_ranked_by_importance()
    {
        var airports = new List<Airport>
        {
            new("KE-0034", "Orly Airpark", "small_airport", 0, 36, 0, City: "Olooloitikosh", Country: "KE"),
            new("LFPO", "Aéroport Paris-Orly", "large_airport", 48.72, 2.38, 300, Iata: "ORY", City: "Paris (Orly, Val-de-Marne)", Country: "FR"),
        };
        var result = new AirportSearchIndex(airports).Find("Orly");
        Assert.Equal("LFPO", result[0].Ident);   // même rang de correspondance : le grand aéroport d'abord
    }
}
