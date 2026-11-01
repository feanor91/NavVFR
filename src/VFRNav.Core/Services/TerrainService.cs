using System.Globalization;
using System.Text.Json;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public sealed record TerrainSample(double DistanceNm, double Lat, double Lon, double ElevationFt, int LegIndex);

/// <param name="LegIndex">Tronçon concerné.</param>
/// <param name="MaxFt">Altitude du point le plus haut du tronçon.</param>
/// <param name="AtNm">Où il se trouve, en milles depuis le départ.</param>
/// <param name="ClearanceFt">Marge entre l'altitude de croisière et ce point (négative : le terrain est plus haut).</param>
public sealed record TerrainWarning(int LegIndex, double MaxFt, double AtNm, double ClearanceFt);

/// <summary>Relief sous la route, d'après un modèle numérique de terrain (pas de pylônes, antennes ni autres obstacles).</summary>
public sealed record TerrainProfile(IReadOnlyList<TerrainSample> Samples)
{
    public double MaxFt => Samples.Count == 0 ? 0 : Samples.Max(s => s.ElevationFt);
    public TerrainSample? Highest => Samples.Count == 0 ? null : Samples.MaxBy(s => s.ElevationFt);
    public double TotalNm => Samples.Count == 0 ? 0 : Samples[^1].DistanceNm;

    public double? LegMaxFt(int leg)
    {
        var inLeg = Samples.Where(s => s.LegIndex == leg).ToList();
        return inLeg.Count == 0 ? null : inLeg.Max(s => s.ElevationFt);
    }

    /// <summary>Comme ci-dessous, avec une altitude qui varie le long de la route : on retient l'endroit où la marge est la plus faible.</summary>
    public IReadOnlyList<TerrainWarning> Warnings(AltitudeProfile altitudes, double marginFt = 1000)
    {
        var result = new List<TerrainWarning>();
        foreach (var group in Samples.GroupBy(s => s.LegIndex).OrderBy(g => g.Key))
        {
            var worst = group.MinBy(s => altitudes.AtDistance(s.DistanceNm) - s.ElevationFt)!;
            double clearance = altitudes.AtDistance(worst.DistanceNm) - worst.ElevationFt;
            if (clearance < marginFt) result.Add(new TerrainWarning(group.Key, worst.ElevationFt, worst.DistanceNm, clearance));
        }
        return result;
    }

    /// <summary>Tronçons où la marge au-dessus du relief est inférieure à <paramref name="marginFt"/>.</summary>
    public IReadOnlyList<TerrainWarning> Warnings(double cruiseFt, double marginFt = 1000)
    {
        var result = new List<TerrainWarning>();
        foreach (var group in Samples.GroupBy(s => s.LegIndex).OrderBy(g => g.Key))
        {
            var top = group.MaxBy(s => s.ElevationFt)!;
            double clearance = cruiseFt - top.ElevationFt;
            if (clearance < marginFt) result.Add(new TerrainWarning(group.Key, top.ElevationFt, top.DistanceNm, clearance));
        }
        return result;
    }
}

/// <summary>
/// Altitudes du sol via Open-Meteo (modèle numérique de terrain d'environ 90 m, gratuit pour un usage non commercial).
/// Cent points par requête ; un point déjà demandé n'est pas redemandé.
/// </summary>
public sealed class TerrainService(HttpClient http)
{
    public const string Attribution = "Altitudes du sol : Open-Meteo.com (modèle numérique de terrain Copernicus, résolution d'environ 90 m).";
    private const int BatchSize = 100;
    private const double MetresToFeet = 3.28084;

    private readonly Dictionary<string, double> _cache = [];

    private static string Key(double lat, double lon) => string.Create(CultureInfo.InvariantCulture, $"{lat:0.0000},{lon:0.0000}");

    public async Task<TerrainProfile> ProfileAsync(IReadOnlyList<Waypoint> route, double stepNm = 1, CancellationToken ct = default)
    {
        var points = RouteGeometry.Sample(route, stepNm);
        var missing = points.Select(p => (p.Lat, p.Lon)).DistinctBy(p => Key(p.Lat, p.Lon)).Where(p => !_cache.ContainsKey(Key(p.Lat, p.Lon))).ToList();

        for (int i = 0; i < missing.Count; i += BatchSize)
        {
            var batch = missing.Skip(i).Take(BatchSize).ToList();
            var metres = await FetchAsync(batch, ct);
            for (int k = 0; k < batch.Count; k++) _cache[Key(batch[k].Lat, batch[k].Lon)] = metres[k] * MetresToFeet;
        }

        return new TerrainProfile(points.Select(p => new TerrainSample(p.DistanceNm, p.Lat, p.Lon, _cache[Key(p.Lat, p.Lon)], p.LegIndex)).ToList());
    }

    /// <summary>Altitude du sol, en pieds, de points quelconques (mêmes lots et même cache que le profil).</summary>
    public async Task<double[]> ElevationsFeetAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
    {
        var missing = points.DistinctBy(p => Key(p.Lat, p.Lon)).Where(p => !_cache.ContainsKey(Key(p.Lat, p.Lon))).ToList();
        for (int i = 0; i < missing.Count; i += BatchSize)
        {
            var batch = missing.Skip(i).Take(BatchSize).ToList();
            var metres = await FetchAsync(batch, ct);
            for (int k = 0; k < batch.Count; k++) _cache[Key(batch[k].Lat, batch[k].Lon)] = metres[k] * MetresToFeet;
        }
        return points.Select(p => _cache[Key(p.Lat, p.Lon)]).ToArray();
    }

    private async Task<double[]> FetchAsync(List<(double Lat, double Lon)> batch, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        string lats = string.Join(',', batch.Select(p => p.Lat.ToString("0.0000", inv)));
        string lons = string.Join(',', batch.Select(p => p.Lon.ToString("0.0000", inv)));
        string json = await http.GetStringAsync($"https://api.open-meteo.com/v1/elevation?latitude={lats}&longitude={lons}", ct);
        using var doc = JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("elevation").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0).ToArray();
        if (values.Length != batch.Count) throw new InvalidOperationException($"Open-Meteo a renvoyé {values.Length} altitudes pour {batch.Count} points.");
        return values;
    }
}
