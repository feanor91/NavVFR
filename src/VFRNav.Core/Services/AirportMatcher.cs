using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>
/// Rapproche les terrains de MSFS de ceux d'OurAirports. Les deux bases n'utilisent pas toujours le même code pour une même piste : MSFS donne un code
/// de type OACI (« EGGL ») à des pistes sans OACI que OurAirports désigne autrement (« GB-0309 »). On les rapproche par la position.
/// </summary>
public static class AirportMatcher
{
    /// <summary>Distance maximale entre deux positions du même terrain, en milles nautiques.</summary>
    public const double MaxNm = 1.0;

    /// <summary>
    /// Pour chaque terrain de MSFS dont le code est inconnu d'OurAirports, le terrain d'OurAirports le plus proche à moins de <paramref name="maxNm"/>
    /// et qui n'a pas déjà de code MSFS. Résultat : code OurAirports → code MSFS.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Match(IReadOnlyList<Airport> airports, IEnumerable<(string Ident, double Lat, double Lon)> simAirports, double maxNm = MaxNm)
    {
        var known = new HashSet<string>(airports.Select(a => a.Ident), StringComparer.OrdinalIgnoreCase);
        var grid = new Dictionary<(int, int), List<Airport>>();
        foreach (var a in airports)
        {
            var key = Cell(a.Lat, a.Lon);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(a);
        }

        var best = new Dictionary<string, (string Sim, double Nm)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ident, lat, lon) in simAirports)
        {
            if (ident.Length == 0 || known.Contains(ident)) continue;
            var (cy, cx) = Cell(lat, lon);
            Airport? nearest = null;
            double nearestNm = maxNm;
            for (int y = cy - 1; y <= cy + 1; y++)
                for (int x = cx - 1; x <= cx + 1; x++)
                    if (grid.TryGetValue((y, x), out var list))
                        foreach (var a in list)
                        {
                            double nm = NavMath.DistanceNm(lat, lon, a.Lat, a.Lon);
                            if (nm <= nearestNm) { nearest = a; nearestNm = nm; }
                        }
            if (nearest is null) continue;
            // Un terrain d'OurAirports n'a qu'un code MSFS : le plus proche l'emporte.
            if (!best.TryGetValue(nearest.Ident, out var current) || nearestNm < current.Nm) best[nearest.Ident] = (ident, nearestNm);
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Sim, StringComparer.OrdinalIgnoreCase);
    }

    // Case d'environ 0,05° (3 NM) : une recherche dans les huit cases voisines couvre largement 1 NM.
    private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat * 20), (int)Math.Floor(lon * 20));
}
