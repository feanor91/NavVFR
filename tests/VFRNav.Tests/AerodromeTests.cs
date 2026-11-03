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

public class VacChartUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfrnav-vac-" + Guid.NewGuid().ToString("N"));

    public VacChartUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeSia(Dictionary<string, byte[]> charts, bool online = true) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(request.Method + " " + url);
            string icao = url[^8..^4];
            if (request.Method == HttpMethod.Head) return Task.FromResult(new HttpResponseMessage(online ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound));
            return Task.FromResult(charts.TryGetValue(icao, out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static byte[] Pdf(string text) => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 " + text);

    [Fact]
    public void Cycles_are_28_days_apart_and_urls_follow_the_siA_layout()
    {
        Assert.Equal(new DateTime(2026, 10, 1), VacChartUpdater.CycleFor(new DateTime(2026, 10, 1)));
        Assert.Equal(new DateTime(2026, 10, 1), VacChartUpdater.CycleFor(new DateTime(2026, 10, 28)));
        Assert.Equal(new DateTime(2026, 10, 29), VacChartUpdater.CycleFor(new DateTime(2026, 10, 29)));
        Assert.Equal(new DateTime(2026, 9, 3), VacChartUpdater.CycleFor(new DateTime(2026, 9, 30)));
        Assert.Equal("https://www.sia.aviation-civile.gouv.fr/media/dvd/eAIP_29_OCT_2026/Atlas-VAC/PDF_AIPparSSection/VAC/AD/AD-2.LFPL.pdf",
            VacChartUpdater.ChartUrl(new DateTime(2026, 10, 29), "LFPL"));
    }

    [Fact]
    public void Wanted_lists_the_charts_of_the_folder_plus_the_requested_missing_ones()
    {
        File.WriteAllBytes(Path.Combine(_dir, "AD-2.LFPL.pdf"), Pdf("a"));
        File.WriteAllBytes(Path.Combine(_dir, "AD-2.LFAB.pdf"), Pdf("b"));
        File.WriteAllBytes(Path.Combine(_dir, "notes.pdf"), Pdf("c"));
        Assert.Equal(["LFAB", "LFPL"], VacChartUpdater.Wanted(_dir, null));
        Assert.Equal(["LFAB", "LFMN", "LFPL"], VacChartUpdater.Wanted(_dir, ["LFMN", "LF3126", "EGLL"]));
    }

    [Fact]
    public async Task Update_replaces_only_changed_charts_adds_new_ones_and_writes_the_cycle()
    {
        File.WriteAllBytes(Path.Combine(_dir, "AD-2.LFPL.pdf"), Pdf("old"));
        File.WriteAllBytes(Path.Combine(_dir, "AD-2.LFAB.pdf"), Pdf("same"));
        var sia = new FakeSia(new() { ["LFPL"] = Pdf("new"), ["LFAB"] = Pdf("same"), ["LFMN"] = Pdf("nice"), ["LFXX"] = [1, 2, 3, 4, 5] });
        var updater = new VacChartUpdater(new HttpClient(sia), TimeSpan.Zero);

        var result = await updater.UpdateAsync(_dir, ["LFAB", "LFMN", "LFPL", "LFXX", "LFZZ"], new DateTime(2026, 10, 1));

        Assert.Equal((1, 1, 1, 2), (result.Changed, result.Added, result.Same, result.Absent)); // LFXX n'est pas un PDF, LFZZ n'existe pas
        Assert.Equal(Pdf("new"), File.ReadAllBytes(Path.Combine(_dir, "AD-2.LFPL.pdf")));
        Assert.Equal(Pdf("nice"), File.ReadAllBytes(Path.Combine(_dir, "AD-2.LFMN.pdf")));
        Assert.False(File.Exists(Path.Combine(_dir, "AD-2.LFXX.pdf")));
        Assert.Contains("2026-10-01", File.ReadAllText(Path.Combine(_dir, "_cycle.txt")));
        Assert.Contains("1 modifiée(s), 1 nouvelle(s)", result.Summary);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        File.WriteAllBytes(Path.Combine(_dir, "AD-2.LFPL.pdf"), Pdf("old"));
        var updater = new VacChartUpdater(new HttpClient(new FakeSia(new() { ["LFPL"] = Pdf("new"), ["LFMN"] = Pdf("n") })), TimeSpan.Zero);

        var result = await updater.UpdateAsync(_dir, ["LFMN", "LFPL"], new DateTime(2026, 10, 1), dryRun: true);

        Assert.Equal((1, 1), (result.Changed, result.Added));
        Assert.Equal(Pdf("old"), File.ReadAllBytes(Path.Combine(_dir, "AD-2.LFPL.pdf")));
        Assert.False(File.Exists(Path.Combine(_dir, "AD-2.LFMN.pdf")));
        Assert.False(File.Exists(Path.Combine(_dir, "_cycle.txt")));
    }

    [Fact]
    public async Task A_cycle_that_is_not_online_stops_before_downloading_anything()
    {
        var sia = new FakeSia([], online: false);
        var updater = new VacChartUpdater(new HttpClient(sia), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.UpdateAsync(_dir, ["LFPL"], new DateTime(2026, 10, 1)));
        Assert.DoesNotContain(sia.Requests, r => r.StartsWith("GET"));
    }

    [Fact]
    public async Task Cancelling_before_the_start_downloads_nothing_and_writes_no_cycle_note()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sia = new FakeSia(new() { ["LFAB"] = Pdf("a") });
        var result = await new VacChartUpdater(new HttpClient(sia), TimeSpan.Zero).UpdateAsync(_dir, ["LFAB"], new DateTime(2026, 10, 1), false, null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Contains("arrêté", result.Summary);
        Assert.False(File.Exists(Path.Combine(_dir, "_cycle.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "AD-2.LFAB.pdf")));
    }
}
