using System.Text.Json;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Weather;

/// <summary>
/// SIGMET : message d'information sur un phénomène dangereux en route (orages, turbulence, givrage, cendres volcaniques...),
/// valable dans une zone et une tranche d'altitude, pendant quelques heures.
/// </summary>
/// <param name="BaseFt">Plancher en pieds ; null = jusqu'au sol.</param>
/// <param name="TopFt">Plafond en pieds ; null = non limité.</param>
/// <param name="Areas">Un ou plusieurs polygones (certains messages décrivent plusieurs zones), en latitude et longitude.</param>
public sealed record Sigmet(
    string FirId, string FirName, string SeriesId, string Hazard, string? Qualifier,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, double? BaseFt, double? TopFt,
    IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> Areas, string? RawText)
{
    public bool IsValidAt(DateTimeOffset time) => time >= ValidFrom && time < ValidTo;

    /// <summary>Valide à un moment de l'intervalle [from, to] (la durée du vol).</summary>
    public bool OverlapsPeriod(DateTimeOffset from, DateTimeOffset to) => ValidFrom < to && ValidTo > from;

    /// <summary>Phénomène en clair, ex. « Orages noyés ».</summary>
    public string Description => HazardName(Hazard) + (Qualifier is { Length: > 0 } q ? " " + QualifierName(q) : "");

    public static string HazardName(string code) => code.ToUpperInvariant() switch
    {
        "TS" => Loc.Get("Sigmet_Orages"),
        "TURB" => Loc.Get("Sigmet_Turbulence"),
        "ICE" => Loc.Get("Sigmet_Givrage"),
        "MTW" => Loc.Get("Sigmet_Ondes_orographiques"),
        "VA" => Loc.Get("Sigmet_Cendres_volcaniques"),
        "TC" => Loc.Get("Sigmet_Cyclone_tropical"),
        "DS" => Loc.Get("Sigmet_Tempete_de_poussiere"),
        "SS" => Loc.Get("Sigmet_Tempete_de_sable"),
        "GR" => Loc.Get("Common_Hail"),
        "FZRA" => Loc.Get("Sigmet_Pluie_verglacante"),
        "RDOACT_CLD" => Loc.Get("Sigmet_Nuage_radioactif"),
        var other => other,
    };

    public static string QualifierName(string code) => code.ToUpperInvariant() switch
    {
        "EMBD" => Loc.Get("Sigmet_noyes_dans_les_nuages"),
        "OBSC" => Loc.Get("Sigmet_masques"),
        "FRQ" => Loc.Get("Sigmet_frequents"),
        "SQL" => Loc.Get("Sigmet_en_ligne_de_grains"),
        "SEV" => Loc.Get("Sigmet_severe"),
        "ISOL" => Loc.Get("Sigmet_isoles"),
        "OCNL" => Loc.Get("Sigmet_occasionnels"),
        var other => other.ToLowerInvariant(),
    };
}

/// <summary>Un SIGMET qui traverse la route.</summary>
/// <param name="EntryNm">Distance depuis le départ au premier point de la route dans la zone.</param>
/// <param name="ExitNm">Distance au dernier point de la route dans la zone.</param>
/// <param name="Status">Dans la tranche d'altitude du SIGMET à l'altitude prévue, au-dessus de son plafond ou en dessous de son plancher.</param>
public sealed record SigmetOnRoute(Sigmet Sigmet, double EntryNm, double ExitNm, VerticalStatus Status);

/// <summary>SIGMET vérifiés pour une route : ceux qui la traversent, et combien étaient en vigueur au total.</summary>
public sealed record SigmetReport(IReadOnlyList<SigmetOnRoute> OnRoute, int ActiveCount, DateTimeOffset At);

public static class SigmetParser
{
    /// <summary>Lit la réponse <c>format=json</c> de aviationweather.gov/api/data/isigmet. Les messages inutilisables (zone absente ou réduite à une ligne) sont ignorés.</summary>
    public static IReadOnlyList<Sigmet> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException(Loc.Get("Sigmet_Reponse_inattendue_une_liste_de"));

            var result = new List<Sigmet>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var areas = ReadAreas(e);
                if (areas.Count == 0) continue;
                if (!TryTime(e, "validTimeFrom", out var from) || !TryTime(e, "validTimeTo", out var to)) continue;

                result.Add(new Sigmet(
                    Text(e, "firId") ?? Text(e, "icaoId") ?? "", Text(e, "firName") ?? "", Text(e, "seriesId") ?? "",
                    Text(e, "hazard") ?? "", Text(e, "qualifier"), from, to, Number(e, "base"), Number(e, "top"), areas, Text(e, "rawSigmet")));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.Get("Sigmet_Reponse_des_SIGMET_illisible") + ex.Message, ex);
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool TryTime(JsonElement e, string name, out DateTimeOffset time)
    {
        time = default;
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return false;
        time = DateTimeOffset.FromUnixTimeSeconds(v.GetInt64());
        return true;
    }

    /// <summary>« coords » est une liste de points (une zone) ou une liste de listes de points (plusieurs zones).</summary>
    private static List<IReadOnlyList<(double, double)>> ReadAreas(JsonElement e)
    {
        var areas = new List<IReadOnlyList<(double, double)>>();
        if (!e.TryGetProperty("coords", out var coords) || coords.ValueKind != JsonValueKind.Array) return areas;

        foreach (var item in coords.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                var ring = Ring(item);
                if (ring.Count >= 3) areas.Add(ring);
            }
            else
            {
                // Zone unique : les éléments sont directement des points.
                var ring = Ring(coords);
                if (ring.Count >= 3) areas.Add(ring);
                break;
            }
        }
        return areas;
    }

    private static List<(double, double)> Ring(JsonElement points)
    {
        var ring = new List<(double, double)>();
        foreach (var p in points.EnumerateArray())
            if (p.TryGetProperty("lat", out var lat) && p.TryGetProperty("lon", out var lon) && lat.ValueKind == JsonValueKind.Number && lon.ValueKind == JsonValueKind.Number)
                ring.Add((lat.GetDouble(), lon.GetDouble()));
        return ring;
    }
}

public static class SigmetAnalysis
{
    /// <summary>
    /// SIGMET en vigueur pendant le vol qui traversent la route. L'heure de départ n'étant pas connue, le vol est supposé commencer
    /// maintenant et durer <paramref name="flightDuration"/>. Trié par distance d'entrée.
    /// </summary>
    public static IReadOnlyList<SigmetOnRoute> AlongRoute(
        IReadOnlyList<Waypoint> route, AltitudeProfile altitudes, IEnumerable<Sigmet> sigmets, DateTimeOffset now, TimeSpan flightDuration)
    {
        var result = new List<SigmetOnRoute>();
        if (route.Count < 2) return result;
        var samples = RouteGeometry.Sample(route, 1);
        var until = now + flightDuration;

        foreach (var s in sigmets)
        {
            if (!s.OverlapsPeriod(now, until)) continue;

            var inside = samples.Where(p => s.Areas.Any(area => Contains(area, p.Lat, p.Lon))).ToList();
            if (inside.Count == 0) continue;

            double floor = s.BaseFt ?? 0, ceiling = s.TopFt ?? double.MaxValue;
            var altitudesInside = inside.Select(p => altitudes.AtDistance(p.DistanceNm)).ToList();
            var status = altitudesInside.Any(a => a >= floor && a <= ceiling) ? VerticalStatus.Inside
                : altitudesInside.All(a => a > ceiling) ? VerticalStatus.Above
                : VerticalStatus.Below;

            result.Add(new SigmetOnRoute(s, inside.Min(p => p.DistanceNm), inside.Max(p => p.DistanceNm), status));
        }
        return result.OrderBy(r => r.EntryNm).ThenBy(r => r.Sigmet.SeriesId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Nombre de SIGMET valides à cet instant.</summary>
    public static int CountActive(IEnumerable<Sigmet> sigmets, DateTimeOffset now) => sigmets.Count(s => s.IsValidAt(now));

    /// <summary>Le point est-il dans le polygone ? Les longitudes sont ramenées dans la même plage : certains messages dépassent 180° ou traversent l'antiméridien.</summary>
    public static bool Contains(IReadOnlyList<(double Lat, double Lon)> polygon, double lat, double lon)
    {
        if (polygon.Count < 3) return false;

        // Déroule les longitudes du polygone (écart d'au plus 180° entre deux sommets) pour qu'il reste d'un seul tenant.
        var lons = new double[polygon.Count];
        lons[0] = polygon[0].Lon;
        for (int i = 1; i < polygon.Count; i++)
        {
            double l = polygon[i].Lon;
            while (l - lons[i - 1] > 180) l -= 360;
            while (l - lons[i - 1] < -180) l += 360;
            lons[i] = l;
        }

        // Le point peut se trouver dans n'importe quel « tour » de longitude du polygone déroulé.
        foreach (double shift in new[] { 0.0, -360.0, 360.0 })
            if (RayCast(polygon, lons, lat, lon + shift)) return true;
        return false;
    }

    private static bool RayCast(IReadOnlyList<(double Lat, double Lon)> polygon, double[] lons, double lat, double lon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double yi = polygon[i].Lat, yj = polygon[j].Lat, xi = lons[i], xj = lons[j];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>SIGMET internationaux d'aviationweather.gov (tous les messages en vigueur dans le monde, en un seul appel, gardés 10 minutes).</summary>
public sealed class SigmetService(HttpClient http, TimeProvider? clock = null)
{
    public const string Url = "https://aviationweather.gov/api/data/isigmet?format=json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private (DateTimeOffset At, IReadOnlyList<Sigmet> Items)? _cache;

    public async Task<IReadOnlyList<Sigmet>> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } hit && _clock.GetUtcNow() - hit.At < CacheLifetime) return hit.Items;
        var items = SigmetParser.Parse(await http.GetStringAsync(Url, ct));
        _cache = (_clock.GetUtcNow(), items);
        return items;
    }
}
