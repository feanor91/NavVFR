using VFRNav.Core.Models;

namespace VFRNav.Tests;

public class GlossaryTests
{
    [Fact]
    public void Every_entry_is_complete_and_belongs_to_a_known_category()
    {
        foreach (var e in Glossary.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Term), "sigle vide");
            Assert.False(string.IsNullOrWhiteSpace(e.Meaning), e.Term);
            Assert.True(e.Explanation.Length > 20, $"explication trop courte : {e.Term}");
            Assert.Contains(e.Category, Glossary.Categories);
        }
    }

    [Fact]
    public void No_term_is_defined_twice_in_the_same_category()
    {
        var duplicates = Glossary.Entries.GroupBy(e => (e.Category, Term: e.Term.ToLowerInvariant())).Where(g => g.Count() > 1).Select(g => g.Key.Term).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_category_has_entries()
    {
        foreach (var category in Glossary.Categories) Assert.Contains(Glossary.Entries, e => e.Category == category);
    }

    [Theory]
    [InlineData("TMA")]
    [InlineData("tma")]
    [InlineData("QNH")]
    [InlineData("CTR")]
    [InlineData("RMZ")]
    [InlineData("NOTAM")]
    [InlineData("AMSL")]
    [InlineData("CAVOK")]
    [InlineData("QFU")]
    [InlineData("SVFR")]
    public void The_acronyms_found_on_the_charts_are_in_the_glossary(string term)
    {
        Assert.NotEmpty(Glossary.Search(term));
    }

    [Fact]
    public void An_exact_acronym_comes_before_entries_that_only_mention_it()
    {
        Assert.Equal("TMA", Glossary.Search("tma")[0].Term);
        Assert.Equal("QNH", Glossary.Search("QNH")[0].Term);
    }

    [Fact]
    public void Search_ignores_accents_and_case_and_looks_in_the_explanation()
    {
        Assert.Contains(Glossary.Search("CLAIRANCE"), e => e.Term == "Clairance");
        Assert.Contains(Glossary.Search("region de controle"), e => e.Term == "TMA");
        Assert.Contains(Glossary.Search("hectopascal"), e => e.Term == "hPa");
        Assert.Empty(Glossary.Search("zzzzqqq"));
        Assert.Equal(Glossary.Entries.Count, Glossary.Search("  ").Count);
    }

    [Fact]
    public void Every_airspace_type_the_app_names_has_a_glossary_entry()
    {
        // Les abréviations qui apparaissent dans les infobulles de zones (Airspace.TypeName).
        foreach (string term in new[] { "CTR", "TMA", "CTA", "RMZ", "TMZ", "ATZ", "FIR", "TRA", "TSA", "ADIZ", "SIV" })
            Assert.Contains(Glossary.Entries, e => e.Term.Split(' ', '/').Contains(term));
    }
}
