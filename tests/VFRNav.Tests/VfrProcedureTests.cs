using System.Text.Json;
using VFRNav.Core.StormAtc;

namespace VFRNav.Tests;

public class VfrProcedureTests
{
    private const string Toussus = """
    {
      "icao": "LFPN",
      "preferredQfu": 70, "preferredQfuReason": "noise abatement",
      "reportingPoints": [
        { "name": "S", "spoken": { "fr": "Sierra", "en": "Sierra" }, "latitude": 48.70972, "longitude": 2.11028,
          "location": "northern hill of the Chevreuse valley", "purpose": "compulsory arrival point" },
        { "name": "NH", "spoken": { "fr": "Novembre Hôtel" }, "latitude": 48.75917, "longitude": 2.1 }
      ],
      "circuit": { "heightFeetAmsl": 1500, "sideOfRunways": "north", "ulmCircuitsProhibited": true,
                   "landmark": { "description": "checkerboard", "use": "overfly on base" } },
      "withAts": {
        "arrivals": [ { "via": "S", "altitudeFeetAmsl": 1300, "instruction": "contact Info 5 minutes before S" },
                      { "from": ["SouthEast", "South"], "altitudeFeetAmsl": 1500 } ],
        "departures": [ { "runway": "25L", "climbToFeetAmsl": 1500, "note": "straight ahead" } ],
        "radio": { "arrival": "contact Sol once the runway is vacated" }
      },
      "withoutAts": { "arrivals": { "compulsoryPoint": "S" }, "particularTrainingProhibited": false, "runway26": "climb to 1500" },
      "notes": ["Give way to IFR aircraft.", "Closure of the flight plan is mandatory."]
    }
    """;

    private static VfrProcedures Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return VfrProcedureReader.Read(doc.RootElement)!;
    }

    [Fact]
    public void Reporting_points_are_read_with_their_french_spoken_name_and_position()
    {
        var p = Read(Toussus);
        Assert.Equal(["S", "NH"], p.ReportingPoints.Select(r => r.Name).ToArray());
        Assert.Equal("S (Sierra)", p.ReportingPoints[0].Label);
        Assert.Equal("NH (Novembre Hôtel)", p.ReportingPoints[1].Label);
        Assert.Equal(48.70972, p.ReportingPoints[0].Lat);
        Assert.Equal("compulsory arrival point", p.ReportingPoints[0].Purpose);
    }

    [Fact]
    public void Sections_have_french_titles_and_labels()
    {
        var p = Read(Toussus);
        Assert.Equal(["Piste préférentielle", "Circuit d'aérodrome", "Arrivées et départs, avec contrôle (ATS)", "Arrivées et départs, sans contrôle", "Notes du profil"],
            p.Sections.Select(s => s.Title).ToArray());

        var circuit = p.Sections.Single(s => s.Title.StartsWith("Circuit", StringComparison.Ordinal)).Lines;
        Assert.Contains("Hauteur du circuit (ft AMSL) : 1500", circuit);
        Assert.Contains("Côté du circuit : nord", circuit);              // valeur traduite
        Assert.Contains("Circuits ULM interdits : oui", circuit);        // booléen en français
        Assert.Contains("Repère visuel :", circuit);
        Assert.Contains("  Description : checkerboard", circuit);        // objet imbriqué décalé

        Assert.Contains("QFU 070 — noise abatement", p.Sections[0].Lines);
    }

    [Fact]
    public void List_items_are_flattened_on_one_line_with_translated_directions()
    {
        var ats = Read(Toussus).Sections.Single(s => s.Title.Contains("avec contrôle", StringComparison.Ordinal)).Lines;
        Assert.Contains(ats, l => l.Contains("Par le point : S") && l.Contains("Altitude (ft AMSL) : 1300") && l.Contains("Consigne : contact Info 5 minutes before S"));
        Assert.Contains(ats, l => l.Contains("Depuis : sud-est, sud"));
        Assert.Contains(ats, l => l.Contains("Piste : 25L") && l.Contains("Monter à (ft AMSL) : 1500"));
        Assert.Contains("Radio :", ats);
        Assert.Contains(ats, l => l.Trim().StartsWith("Arrivée : contact Sol", StringComparison.Ordinal));
        Assert.Contains(ats, l => l.Contains("contact Sol once the runway is vacated"));
    }

    [Fact]
    public void Unknown_keys_are_kept_with_a_readable_label_not_dropped()
    {
        var lines = Read(Toussus).Sections.Single(s => s.Title.Contains("sans contrôle", StringComparison.Ordinal)).Lines;
        Assert.Contains("Entraînement particulier interdit : non", lines);
        Assert.Contains("Runway 26 : climb to 1500", lines);
        Assert.Contains("  Point obligatoire : S", lines);
    }

    [Fact]
    public void Summary_lists_points_and_circuit_height()
    {
        var summary = Read(Toussus).Summary!;
        Assert.Contains("Points de report : S, NH", summary);
        Assert.Contains("Circuit à 1500 ft AMSL", summary);
    }

    [Fact]
    public void ToLines_starts_with_the_reporting_points()
    {
        var lines = Read(Toussus).ToLines();
        Assert.Equal("Points de report VFR", lines[0]);
        Assert.Contains("  • S (Sierra) : northern hill of the Chevreuse valley — compulsory arrival point", lines);
    }

    [Fact]
    public void A_profile_without_procedures_gives_null()
    {
        using var doc = JsonDocument.Parse("""{ "icao": "LFAB", "name": "Dieppe", "elevationFeet": 344 }""");
        Assert.Null(VfrProcedureReader.Read(doc.RootElement));
    }

    [Fact]
    public void Label_falls_back_to_the_key_split_into_words()
    {
        Assert.Equal("Hauteur du circuit (ft AMSL)", VfrProcedureReader.Label("heightFeetAmsl"));
        Assert.Equal("Some new key", VfrProcedureReader.Label("someNewKey"));
    }

    [Fact]
    public void Real_stormatc_profiles_expose_their_procedures_when_present()
    {
        var paths = StormAtcPaths.Locate();
        if (paths is null) return;
        var repo = StormAtcRepository.Load(paths);
        var lfpl = repo.Find("LFPL")!;
        Assert.NotNull(lfpl.Procedures);
        Assert.Contains(lfpl.Procedures!.ReportingPoints, r => r.Name == "E");
        Assert.NotNull(StormAtcRepository.DescribeProcedures(lfpl));
        // Profil relevé par script (StormATC 0.25) : des notes de la carte, sans circuit ni points de report forcément.
        var lfab = repo.Find("LFAB")!.Procedures;
        if (lfab is not null) Assert.Contains(lfab.Sections, s => s.Title == "Notes du profil");
    }
}
