using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Zone géographique visible, en degrés.</summary>
public readonly record struct GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public bool Contains(double lat, double lon) => lat >= MinLat && lat <= MaxLat && lon >= MinLon && lon <= MaxLon;
}

/// <param name="Shown">Terrains à dessiner.</param>
/// <param name="InView">Terrains de la zone visible qui passent les filtres.</param>
/// <param name="Hidden">Terrains de la zone visible qui ne sont pas dessinés (trop proches d'un plus important, ou au-delà du plafond).</param>
public sealed record AirportSelection(IReadOnlyList<Airport> Shown, int InView, int Hidden);

/// <summary>
/// Quels terrains dessiner, avec 48 000 terrains dans le monde : ceux de la zone visible, du plus important au moins important,
/// un seul par case de l'écran (les plus gros d'abord, les petits apparaissent en zoomant), jusqu'à un plafond.
/// </summary>
public static class AirportDisplay
{
    public const int DefaultCap = 800;

    /// <summary>Taille d'une case de l'écran, en pixels : au plus un terrain par case (les terrains de la route restent toujours).</summary>
    public const double CellPixels = 26;

    /// <summary>
    /// Importance d'un terrain, de 0 (le plus important) à 6 : grand à service régulier, grand, moyen à service régulier, moyen,
    /// petit à service régulier, petit avec une piste en dur d'au moins 3 000 ft, autre petit.
    /// </summary>
    public static int Rank(Airport a) => a.Type switch
    {
        "large_airport" => a.Scheduled ? 0 : 1,
        "medium_airport" => a.Scheduled ? 2 : 3,
        _ => a.Scheduled ? 4 : a.LongestPavedRunwayFt >= 3000 ? 5 : 6,
    };

    /// <summary>Position en Web Mercator (mètres), pour répartir les terrains en cases de l'écran.</summary>
    public static (double X, double Y) Mercator(double lat, double lon)
    {
        lat = Math.Clamp(lat, -85.05, 85.05);
        return (lon * 20037508.342789244 / 180, Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360)) * 6378137.0);
    }

    /// <param name="candidates">Terrains qui passent les filtres.</param>
    /// <param name="cellMeters">Taille d'une case de l'écran en mètres Mercator (pixels × résolution) ; 0 = pas de répartition.</param>
    /// <param name="pinned">Terrains toujours dessinés s'ils sont dans la zone (ceux de la route), même s'ils ne passent pas les filtres.</param>
    public static AirportSelection Select(
        IEnumerable<Airport> candidates, GeoBounds view, double cellMeters, int cap = DefaultCap, IEnumerable<Airport>? pinned = null)
    {
        var pins = (pinned ?? []).Where(a => view.Contains(a.Lat, a.Lon)).DistinctBy(a => a.Ident, StringComparer.OrdinalIgnoreCase).ToList();
        var pinIdents = pins.Select(a => a.Ident).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int inView = 0;
        var best = new Dictionary<(long, long), Airport>();
        var uncapped = new List<Airport>();
        foreach (var a in candidates)
        {
            if (!view.Contains(a.Lat, a.Lon) || pinIdents.Contains(a.Ident)) continue;
            inView++;
            if (cellMeters <= 0) { uncapped.Add(a); continue; }

            var (x, y) = Mercator(a.Lat, a.Lon);
            var key = ((long)Math.Floor(x / cellMeters), (long)Math.Floor(y / cellMeters));
            if (!best.TryGetValue(key, out var current) || Better(a, current)) best[key] = a;
        }

        var ranked = (cellMeters <= 0 ? uncapped : best.Values.ToList())
            .OrderBy(Rank).ThenByDescending(a => a.LongestRunwayFt ?? 0).ThenBy(a => a.Ident, StringComparer.Ordinal)
            .Take(Math.Max(0, cap - pins.Count));

        var shown = pins.Concat(ranked).ToList();
        // Les terrains de la route comptent dans la zone visible mais ne sont pas « masqués ».
        int total = inView + pins.Count;
        return new AirportSelection(shown, total, total - shown.Count);
    }

    private static bool Better(Airport a, Airport b)
    {
        int ra = Rank(a), rb = Rank(b);
        if (ra != rb) return ra < rb;
        return (a.LongestRunwayFt ?? 0) > (b.LongestRunwayFt ?? 0);
    }

    /// <summary>
    /// Terrains à moins de <paramref name="nm"/> milles de la route (le long du grand cercle de chaque branche).
    /// Sans route, tous les terrains sont gardés : le filtre n'a rien à comparer.
    /// </summary>
    public static IReadOnlyList<Airport> NearRoute(IEnumerable<Airport> airports, IReadOnlyList<Waypoint> route, double nm)
    {
        if (route.Count == 0 || !(nm > 0)) return airports.ToList();

        // La route est échantillonnée tous les 30 NM : le calcul sur plan local reste exact à cette échelle, même sur une longue branche.
        var lines = route.Count == 1
            ? [new List<(double Lat, double Lon)> { (route[0].Lat, route[0].Lon) }]
            : RouteGeometry.GreatCircleLines(route, 30, 85).Select(l => l.ToList()).ToList();
        var segments = new List<(Waypoint A, Waypoint B, double MinLat, double MaxLat, double MinLon, double MaxLon)>();
        double latPad = nm / 60;
        foreach (var line in lines)
            for (int i = 0; i < Math.Max(1, line.Count - 1); i++)
            {
                var a = new Waypoint { Lat = line[i].Lat, Lon = line[i].Lon };
                var b = line.Count > 1 ? new Waypoint { Lat = line[i + 1].Lat, Lon = line[i + 1].Lon } : a;
                double midLat = (a.Lat + b.Lat) / 2;
                double lonPad = latPad / Math.Max(Math.Cos(midLat * Math.PI / 180), 0.05);
                segments.Add((a, b, Math.Min(a.Lat, b.Lat) - latPad, Math.Max(a.Lat, b.Lat) + latPad, Math.Min(a.Lon, b.Lon) - lonPad, Math.Max(a.Lon, b.Lon) + lonPad));
            }

        var near = new List<Airport>();
        foreach (var ap in airports)
            foreach (var s in segments)
            {
                if (ap.Lat < s.MinLat || ap.Lat > s.MaxLat || ap.Lon < s.MinLon || ap.Lon > s.MaxLon) continue;
                if (RouteGeometry.DistanceToSegment(ap.Lat, ap.Lon, s.A, s.B).DistanceNm <= nm) { near.Add(ap); break; }
            }
        return near;
    }
}
