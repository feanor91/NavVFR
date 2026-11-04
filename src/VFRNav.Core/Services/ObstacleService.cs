using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

public enum ObstacleKind { Mast, Tower, Chimney, WindTurbine }

/// <summary>Obstacle vertical d'OpenStreetMap.</summary>
/// <param name="HeightM">Hauteur au-dessus du sol en mètres ; null si OpenStreetMap ne la donne pas (on ne la devine pas).</param>
public sealed record Obstacle(long Id, ObstacleKind Kind, string Name, double Lat, double Lon, double? HeightM)
{
    public string KindText => Kind switch
    {
        ObstacleKind.Mast => Loc.Get("ObstacleService_Mat_antenne"),
        ObstacleKind.Tower => Loc.Get("ObstacleService_Tour"),
        ObstacleKind.Chimney => Loc.Get("ObstacleService_Cheminee"),
        _ => Loc.Get("ObstacleService_Eolienne")
    };
}

/// <param name="AlongNm">Position le long de la route.</param>
/// <param name="OffsetNm">Écart latéral à la route.</param>
/// <param name="GroundFt">Altitude du sol au pied de l'obstacle.</param>
/// <param name="TopFt">Altitude du sommet (sol + hauteur) ; null si la hauteur est inconnue.</param>
public sealed record RouteObstacle(Obstacle Obstacle, double AlongNm, double OffsetNm, double? GroundFt, double? TopFt)
{
    /// <summary>Marge entre l'altitude de croisière et le sommet ; négative : l'obstacle dépasse.</summary>
    public double? MarginFt(double cruiseFt) => TopFt is null ? null : cruiseFt - TopFt;
}

/// <summary>
/// Obstacles proches de la route d'après OpenStreetMap (Overpass API) : mâts, tours, cheminées, éoliennes.
/// La couverture dépend des contributeurs et beaucoup d'éléments n'ont pas de hauteur ; l'absence d'un obstacle ici
/// ne prouve pas qu'il n'existe pas.
/// </summary>
public sealed partial class ObstacleService(HttpClient http, string endpoint = ObstacleService.DefaultEndpoint)
{
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    public static string Attribution => Loc.Get("ObstacleService_Obstacles_contributeurs_OpenStreetMap_licenc");
    private const int MaxBoxes = 24;
    private const int MaxObstaclesForElevation = 600;
    private const double MetresToFeet = 3.28084;

    /// <summary>Des antennes à moins de cette distance sont un seul site (un pylône porte souvent les antennes de plusieurs opérateurs).</summary>
    public const double MergeRadiusNm = 0.06;

    /// <summary>Rectangles (sud, ouest, nord, est) couvrant la route élargie de <paramref name="corridorNm"/> de chaque côté.</summary>
    public static IReadOnlyList<(double South, double West, double North, double East)> BuildBoxes(IReadOnlyList<Waypoint> route, double corridorNm)
    {
        var boxes = new List<(double, double, double, double)>();
        double pad = corridorNm / 60.0;
        for (int i = 0; i < route.Count - 1; i++)
        {
            double length = NavMath.DistanceNm(route[i], route[i + 1]);
            int parts = Math.Max(1, (int)Math.Ceiling(length / 30)); // les tronçons longs sont coupés pour ne pas interroger un grand carré vide
            for (int k = 0; k < parts; k++)
            {
                var a = RouteGeometry.Interpolate(route[i].Lat, route[i].Lon, route[i + 1].Lat, route[i + 1].Lon, (double)k / parts);
                var b = RouteGeometry.Interpolate(route[i].Lat, route[i].Lon, route[i + 1].Lat, route[i + 1].Lon, (double)(k + 1) / parts);
                double lonPad = pad / Math.Max(Math.Cos((a.Lat + b.Lat) / 2 * Math.PI / 180), 0.2);
                boxes.Add((Math.Min(a.Lat, b.Lat) - pad, Math.Min(a.Lon, b.Lon) - lonPad, Math.Max(a.Lat, b.Lat) + pad, Math.Max(a.Lon, b.Lon) + lonPad));
            }
        }
        return boxes;
    }

    /// <summary>Une seule requête pour toutes les zones : Overpass limite le nombre de requêtes simultanées.</summary>
    public static string BuildQuery(IReadOnlyList<(double South, double West, double North, double East)> boxes)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("[out:json][timeout:40];(");
        foreach (var box in boxes)
        {
            string bbox = string.Create(inv, $"{box.South:0.0000},{box.West:0.0000},{box.North:0.0000},{box.East:0.0000}");
            sb.Append($"node[\"man_made\"~\"^(mast|tower|chimney|communications_tower)$\"]({bbox});");
            sb.Append($"node[\"power\"=\"generator\"][\"generator:source\"=\"wind\"]({bbox});");
        }
        return sb.Append(");out body;").ToString();
    }

    /// <summary>Obstacles à moins de <paramref name="corridorNm"/> de la route, dans l'ordre du trajet, avec l'altitude de leur sommet.</summary>
    public async Task<IReadOnlyList<RouteObstacle>> FindAsync(IReadOnlyList<Waypoint> route, TerrainService terrain, double corridorNm = 2, CancellationToken ct = default)
    {
        var boxes = BuildBoxes(route, corridorNm);
        if (boxes.Count > MaxBoxes) throw new InvalidOperationException(Loc.F("ObstacleService_Route_trop_longue_pour_interroger", boxes.Count, MaxBoxes));

        var found = new Dictionary<long, Obstacle>();
        foreach (var o in Parse(await QueryAsync(BuildQuery(boxes), ct))) found[o.Id] = o;

        var near = Merge(Near(route, found.Values, corridorNm), MergeRadiusNm);
        if (near.Count > MaxObstaclesForElevation) near = near.OrderByDescending(n => n.Obstacle.HeightM ?? 0).Take(MaxObstaclesForElevation).OrderBy(n => n.AlongNm).ToList();
        if (near.Count == 0) return near;

        var ground = await terrain.ElevationsFeetAsync(near.Select(n => (n.Obstacle.Lat, n.Obstacle.Lon)).ToList(), ct);
        return near.Select((n, i) => n with
        {
            GroundFt = ground[i],
            TopFt = n.Obstacle.HeightM is { } h ? ground[i] + h * MetresToFeet : null
        }).ToList();
    }

    private async Task<string> QueryAsync(string query, CancellationToken ct)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent([new("data", query)]), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(Loc.F("ObstacleService_Overpass_HTTP_service_parfois_sature", (int)response.StatusCode));
        return body;
    }

    /// <summary>
    /// Regroupe les obstacles voisins en un seul, en gardant le plus haut ; un obstacle sans hauteur disparaît s'il est
    /// à côté d'un obstacle de hauteur connue.
    /// </summary>
    public static List<RouteObstacle> Merge(IEnumerable<RouteObstacle> obstacles, double radiusNm)
    {
        var kept = new List<RouteObstacle>();
        foreach (var candidate in obstacles.OrderByDescending(o => o.Obstacle.HeightM ?? -1))
        {
            bool sameSite = kept.Any(k => DistanceNm(k.Obstacle, candidate.Obstacle) <= radiusNm);
            if (!sameSite) kept.Add(candidate);
        }
        return kept.OrderBy(k => k.AlongNm).ToList();
    }

    private static double DistanceNm(Obstacle a, Obstacle b) =>
        NavMath.DistanceNm(new Waypoint { Lat = a.Lat, Lon = a.Lon }, new Waypoint { Lat = b.Lat, Lon = b.Lon });

    /// <summary>Filtre des obstacles proches de la route et calcule leur position le long du trajet.</summary>
    public static List<RouteObstacle> Near(IReadOnlyList<Waypoint> route, IEnumerable<Obstacle> obstacles, double corridorNm)
    {
        var legStart = new double[Math.Max(route.Count - 1, 0)];
        double done = 0;
        for (int i = 0; i < legStart.Length; i++) { legStart[i] = done; done += NavMath.DistanceNm(route[i], route[i + 1]); }

        var result = new List<RouteObstacle>();
        foreach (var o in obstacles)
        {
            double best = double.MaxValue, along = 0;
            for (int i = 0; i < legStart.Length; i++)
            {
                var (d, t) = RouteGeometry.DistanceToSegment(o.Lat, o.Lon, route[i], route[i + 1]);
                if (d < best) { best = d; along = legStart[i] + t * NavMath.DistanceNm(route[i], route[i + 1]); }
            }
            if (best <= corridorNm) result.Add(new RouteObstacle(o, along, best, null, null));
        }
        return result.OrderBy(r => r.AlongNm).ToList();
    }

    public static IReadOnlyList<Obstacle> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<Obstacle>();
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return result;
        foreach (var e in elements.EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "node" || !e.TryGetProperty("tags", out var tags)) continue;
            string? Tag(string key) => tags.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            // Radars automatiques et ouvrages démolis ou désaffectés ne sont pas des obstacles pour un avion.
            if (Tag("highway") == "speed_camera" || Tag("disused") is not null || Tag("abandoned") is not null || Tag("demolished") is not null) continue;

            ObstacleKind? kind = Tag("man_made") switch
            {
                "mast" or "communications_tower" => ObstacleKind.Mast,
                "tower" => ObstacleKind.Tower,
                "chimney" => ObstacleKind.Chimney,
                _ => Tag("generator:source") == "wind" ? ObstacleKind.WindTurbine : null
            };
            if (kind is null) continue;

            result.Add(new Obstacle(e.GetProperty("id").GetInt64(), kind.Value, Tag("name") ?? Tag("ref") ?? "",
                e.GetProperty("lat").GetDouble(), e.GetProperty("lon").GetDouble(), ParseHeightMetres(Tag("height"))));
        }
        return result;
    }

    /// <summary>Hauteur OpenStreetMap : « 45 », « 45.5 », « 45 m », « 150 ft » ou « 150' » (mètres par défaut).</summary>
    public static double? ParseHeightMetres(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = HeightRx().Match(text.Trim());
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0) return null;
        return m.Groups[2].Value is "ft" or "'" ? value * 0.3048 : value;
    }

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*(m|ft|')?$")]
    private static partial Regex HeightRx();
}
