using System.Globalization;
using System.Text.Json;
using VFRNav.Core.Services;

namespace VFRNav.Core.Aerodromes;

/// <summary>
/// Lit la base d'aérodromes de MSFS Rhumb : <c>vfr-data/aerodromes-france.csv</c> (liste nationale),
/// <c>vfr-data/vfr-profiles/*.json</c> (position, altitude, fréquences, pistes, procédures VFR) et les cartes VAC du pilote
/// (<c>AD-2.&lt;OACI&gt;.pdf</c>, © SIA : lues sur place, jamais copiées).
/// </summary>
public sealed class AerodromeRepository
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Dictionary<string, AerodromeInfo> _byIcao;

    private AerodromeRepository(AerodromePaths paths, Dictionary<string, AerodromeInfo> byIcao, string? cycleNotice, int profileCount)
    {
        Paths = paths;
        _byIcao = byIcao;
        CycleNotice = cycleNotice;
        ProfileCount = profileCount;
    }

    public AerodromePaths Paths { get; }
    public string? CycleNotice { get; }
    public int ProfileCount { get; }
    public int ChartCount => _byIcao.Values.Count(a => a.HasChart);
    public IReadOnlyCollection<AerodromeInfo> All => _byIcao.Values;

    public AerodromeInfo? Find(string icao) =>
        _byIcao.TryGetValue(icao.Trim().ToUpperInvariant(), out var a) ? a : null;

    /// <summary>Cherche par code OACI exact, sinon par morceau de nom ou de commune.</summary>
    public AerodromeInfo? Search(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (Find(text) is { } exact) return exact;
        return _byIcao.Values
            .Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                        (a.Commune?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(a => a.HasChart).ThenBy(a => a.Icao, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static AerodromeRepository Load(AerodromePaths paths)
    {
        var profiles = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var owners = new List<JsonDocument>();
        // Les profils du pilote sont lus en dernier : ils remplacent ceux livrés avec l'application.
        foreach (string dir in new[] { paths.BundledProfilesDir, paths.UserProfilesDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("icao", out var icao) && icao.GetString() is { Length: > 0 } code)
                    {
                        owners.Add(doc);
                        profiles[code.ToUpperInvariant()] = doc.RootElement;
                    }
                    else doc.Dispose();
                }
                catch (Exception ex) when (ex is JsonException or IOException) { /* profil invalide : ignoré */ }
            }
        }

        var result = new Dictionary<string, AerodromeInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(paths.AerodromesCsv))
            {
                using var reader = new StreamReader(paths.AerodromesCsv);
                bool header = true;
                foreach (var row in CsvParser.Parse(reader, ';'))
                {
                    if (header) { header = false; continue; }
                    if (row.Length < 2 || string.IsNullOrWhiteSpace(row[0])) continue;
                    string icao = row[0].Trim().ToUpperInvariant();
                    profiles.TryGetValue(icao, out var profile);
                    result[icao] = Build(paths, icao, row[1].Trim(), At(row, 2), At(row, 3),
                        string.Equals(At(row, 4), "oui", StringComparison.OrdinalIgnoreCase), profile);
                }
            }
            // Profils sans ligne dans le CSV.
            foreach (var (icao, profile) in profiles)
                if (!result.ContainsKey(icao))
                    result[icao] = Build(paths, icao, "", null, null, false, profile);
        }
        finally
        {
            foreach (var d in owners) d.Dispose();
        }

        string? notice = null;
        try
        {
            string file = Path.Combine(paths.ChartsDir, "_cycle.txt");
            if (File.Exists(file)) notice = File.ReadAllText(file).Trim();
        }
        catch (IOException) { /* facultatif */ }

        return new AerodromeRepository(paths, result, notice, profiles.Count);
    }

    private static string? At(string[] row, int i) => i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

    private static AerodromeInfo Build(AerodromePaths paths, string icao, string csvName, string? commune, string? remark, bool vac, JsonElement profile)
    {
        bool hasProfile = profile.ValueKind == JsonValueKind.Object;
        string name = csvName.Length > 0 ? csvName : hasProfile ? Str(profile, "name") ?? icao : icao;
        string chart = Path.Combine(paths.ChartsDir, $"AD-2.{icao}.pdf");

        double? lat = null, lon = null, elev = null, variation = null;
        string? service = null;
        var freqs = new List<AerodromeFrequency>();
        var runways = new List<AerodromeRunway>();
        if (hasProfile)
        {
            if (profile.TryGetProperty("referencePosition", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                lat = Num(pos, "latitude");
                lon = Num(pos, "longitude");
            }
            elev = Num(profile, "elevationFeet");
            variation = Num(profile, "magneticVariationDegrees");
            if (profile.TryGetProperty("service", out var s) && s.ValueKind == JsonValueKind.Object) service = Str(s, "kind");
            if (profile.TryGetProperty("frequencies", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var entry in f.EnumerateObject())
                {
                    string label = ServiceLabel(entry.Name);
                    if (entry.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in entry.Value.EnumerateArray()) AddFrequency(freqs, label, item);
                    else AddFrequency(freqs, label, entry.Value);
                }
            if (profile.TryGetProperty("runways", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var rw in r.EnumerateArray())
                    if (Str(rw, "id") is { } id)
                    {
                        var qfu = rw.TryGetProperty("qfu", out var q) && q.ValueKind == JsonValueKind.Array
                            ? q.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList()
                            : [];
                        runways.Add(new AerodromeRunway(id, qfu, Str(rw, "surface"), (int?)Num(rw, "lengthMetres"), (int?)Num(rw, "widthMetres")));
                    }
        }
        return new AerodromeInfo(icao, name, commune, remark, vac, lat, lon, elev, variation, service,
            freqs, runways, File.Exists(chart) ? chart : null, hasProfile ? VfrProcedureReader.Read(profile) : null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static void AddFrequency(List<AerodromeFrequency> into, string service, JsonElement value)
    {
        string remark = "";
        var number = value;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("mhz", out var mhz))
        {
            number = mhz;
            remark = Str(value, "remark") ?? "";
        }
        if (number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out var m)) into.Add(new AerodromeFrequency(service, m, remark));
    }

    private static string ServiceLabel(string key) => key switch
    {
        "atis" => "ATIS",
        "tower" or "twr" => "TWR",
        "ground" or "gnd" => "SOL",
        "approach" or "app" => "APP",
        "airAir" => "A/A",
        "afis" => "AFIS",
        _ => key
    };

    /// <summary>Procédures VFR (points de report, circuit, arrivées et départs) d'un aérodrome, ou null si le profil n'en a pas.</summary>
    public static string? DescribeProcedures(AerodromeInfo a)
    {
        if (a.Procedures is not { Any: true } p) return null;
        return string.Join(Environment.NewLine, [.. p.ToLines(), VfrProcedures.Source]);
    }

    /// <summary>Texte de présentation d'un aérodrome pour le panneau d'information.</summary>
    public static string Describe(AerodromeInfo a)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string> { $"{a.Icao} — {a.Name}" };
        if (a.Commune is not null) lines.Add($"Commune : {a.Commune}");
        if (a.Remark is not null) lines.Add($"Remarque : {a.Remark}");
        var tech = new List<string>();
        if (a.ElevationFt is { } e) tech.Add($"Élévation {e.ToString("0", inv)} ft");
        if (a.MagneticVariation is { } v) tech.Add($"Déclinaison {v.ToString("0.#", inv)}° E");
        if (a.ServiceKind is not null) tech.Add($"Service : {a.ServiceKind}");
        if (tech.Count > 0) lines.Add(string.Join("  ·  ", tech));
        if (a.Frequencies.Count > 0)
            lines.Add("Fréquences : " + string.Join("   ", a.Frequencies.Select(f => $"{f.Service} {f.Mhz.ToString("0.000", inv)}")));
        foreach (var r in a.Runways)
        {
            string qfu = r.Qfu.Count > 0 ? " (QFU " + string.Join("/", r.Qfu.Select(q => q.ToString("000", inv))) + ")" : "";
            string size = r.LengthMetres is { } l ? $" {l}×{r.WidthMetres?.ToString(inv) ?? "?"} m" : "";
            lines.Add($"Piste {r.Id}{qfu}{size}{(r.Surface is not null ? " " + r.Surface : "")}");
        }
        lines.Add(a.HasChart ? "Carte VAC : disponible" : a.VacListed ? "Carte VAC : publiée, absente du dossier" : "Carte VAC : non publiée");
        return string.Join(Environment.NewLine, lines);
    }
}
