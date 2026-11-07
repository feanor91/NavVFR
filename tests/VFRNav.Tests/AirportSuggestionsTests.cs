using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class AirportSuggestionsTests
{
    private static readonly string[] Known = ["EGLL", "EGLC", "EGGW", "EGKK", "LFPG", "LFPO", "LFMN", "EGL"];

    [Fact]
    public void A_mistyped_code_suggests_the_airport_one_letter_away()
    {
        Assert.Contains("EGLL", AirportSuggestions.Similar("EGGL", Known));   // lettre remplacée
        Assert.Contains("EGLL", AirportSuggestions.Similar("EGL", Known.Where(k => k.Length == 4)));   // lettre manquante
        Assert.Contains("EGLL", AirportSuggestions.Similar("EGLLL", Known));   // lettre en trop : ne diffère que d'une
        Assert.Contains("LFPG", AirportSuggestions.Similar("LFGP", Known));    // deux lettres inversées
    }

    [Fact]
    public void The_code_itself_far_codes_and_too_short_inputs_give_nothing()
    {
        Assert.DoesNotContain("EGLL", AirportSuggestions.Similar("EGLL", Known));
        Assert.Empty(AirportSuggestions.Similar("XXXX", Known));
        Assert.Empty(AirportSuggestions.Similar("EG", Known));
    }

    [Fact]
    public void Heavier_airports_come_first_so_a_big_airport_beats_a_grass_strip()
    {
        var counts = new Dictionary<string, int> { ["EGGD"] = 40, ["EGGP"] = 35, ["EGGW"] = 30, ["EGLL"] = 200 };
        var result = AirportSuggestions.Similar("EGGL", counts.Keys, max: 3, weight: k => counts[k]);
        Assert.Equal("EGLL", result[0]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Suggestions_are_sorted_limited_and_case_insensitive()
    {
        var result = AirportSuggestions.Similar("eggl", ["EGLL", "EGGW", "EGGK", "EGGD", "EGGP"], max: 3);
        Assert.Equal(3, result.Count);
        Assert.Equal(result.Order(StringComparer.Ordinal), result);
    }
}
