using VFRNav.Core.Models;
using VFRNav.Core.Aerodromes;

namespace VFRNav.Tests;

public class AerodromeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfrnav-test-" + Guid.NewGuid().ToString("N"));

    public AerodromeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "vfr-profiles"));
        Directory.CreateDirectory(Path.Combine(_root, "Cartes VAC"));
        File.WriteAllText(Path.Combine(_root, "data", "aerodromes-france.csv"),
            "﻿OACI;Nom;Commune;Remarque;VAC\n" +
            "LFPL;Aérodrome de Lognes - Émerainville;Émerainville;;oui\n" +
            "LFAB;Aéroport de Dieppe - Saint-Aubin;Saint-Aubin-sur-Scie;;oui\n" +
            "LF3126;Aérodrome de Sabonnères;Sabonnères;aérodrome privé;non\n");
        File.WriteAllText(Path.Combine(_root, "data", "vfr-profiles", "LFPL.json"), """
            {
              "$comment": ["commentaire"],
              "icao": "LFPL", "name": "Lognes",
              "referencePosition": { "latitude": 48.82194, "longitude": 2.62278 },
              "elevationFeet": 359, "magneticVariationDegrees": 2.0,
              "service": { "kind": "TowerAndGround" },
              "frequencies": { "tower": 118.605, "ground": { "mhz": 122.13, "remark": "sol" }, "approach": [123.1, 124.2] },
              "runways": [ { "id": "08/26", "qfu": [80, 260], "surface": "paved", "lengthMetres": 700, "widthMetres": 20 } ]
            }
            """);
        File.WriteAllBytes(Path.Combine(_root, "Cartes VAC", "AD-2.LFPL.pdf"), [1]);
    }

    private AerodromeRepository Load() => AerodromeRepository.Load(new AerodromePaths(Path.Combine(_root, "data"), Path.Combine(_root, "user-profiles"), Path.Combine(_root, "Cartes VAC")));

    [Fact]
    public void Loads_csv_profile_and_chart()
    {
        var repo = Load();
        Assert.Equal(3, repo.All.Count);
        var lfpl = repo.Find("lfpl")!;
        Assert.Equal("Aérodrome de Lognes - Émerainville", lfpl.Name);
        Assert.Equal(359, lfpl.ElevationFt);
        Assert.Equal(2.0, lfpl.MagneticVariation);
        Assert.True(lfpl.HasChart);
        Assert.Equal(4, lfpl.Frequencies.Count);
        Assert.Contains(lfpl.Frequencies, f => f.Service == "SOL" && f.Mhz == 122.13m && f.Remark == "sol");
        Assert.Equal([80, 260], lfpl.Runways.Single().Qfu);
    }

    [Fact]
    public void Aerodrome_without_profile_has_no_position_or_chart()
    {
        var lfab = Load().Find("LFAB")!;
        Assert.False(lfab.HasPosition);
        Assert.False(lfab.HasChart);
        Assert.True(lfab.VacListed);
        Assert.False(Load().Find("LF3126")!.VacListed);
    }

    [Fact]
    public void User_profile_overrides_bundled_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "user-profiles"));
        File.WriteAllText(Path.Combine(_root, "user-profiles", "LFPL.json"),
            """{ "icao": "LFPL", "elevationFeet": 400 }""");
        Assert.Equal(400, Load().Find("LFPL")!.ElevationFt);
    }

    [Fact]
    public void Search_by_name_prefers_aerodromes_with_chart()
    {
        var repo = Load();
        Assert.Equal("LFPL", repo.Search("lognes")!.Icao);
        Assert.Equal("LFAB", repo.Search("Dieppe")!.Icao);
        Assert.Null(repo.Search("inconnu"));
    }

    [Fact]
    public void Merge_overlays_aerodrome_data_on_ourairports()
    {
        var baseList = new List<Airport> { new("LFPL", "Lognes", "small_airport", 48.8, 2.6, 300), new("LFXX", "Autre", "small_airport", 1, 1, 10) };
        var merged = AirportCatalog.Merge(baseList, Load());
        var lfpl = merged.Single(a => a.Ident == "LFPL");
        Assert.Equal(359, lfpl.ElevationFt);
        Assert.True(lfpl.HasVac);
        Assert.False(merged.Single(a => a.Ident == "LFXX").HasVac);
    }

    [Fact]
    public void Describe_mentions_chart_availability()
    {
        var text = AerodromeRepository.Describe(Load().Find("LFPL")!);
        Assert.Contains("Carte VAC : disponible", text);
        Assert.Contains("TWR 118.605", text);
    }

    [Fact]
    public void Charts_folder_is_the_configured_one_or_its_Cartes_VAC_subfolder()
    {
        Assert.Equal(Path.Combine(_root, "Cartes VAC"), AerodromePaths.ResolveChartsDir(_root));   // ancien réglage : racine contenant « Cartes VAC »
        Assert.Equal(Path.Combine(_root, "data"), AerodromePaths.ResolveChartsDir(Path.Combine(_root, "data")));
        Assert.EndsWith(Path.Combine("VFRNav", "Cartes VAC"), AerodromePaths.ResolveChartsDir(""));
    }

    [Fact]
    public void Real_bundled_repo_loads_when_present()
    {
        var paths = AerodromePaths.Locate();
        Assert.NotNull(paths); // les données livrées sont dans le dépôt (vfr-data)
        var repo = AerodromeRepository.Load(paths);
        Assert.True(repo.All.Count > 300);
        Assert.NotNull(repo.Find("LFPL")?.Lat);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
