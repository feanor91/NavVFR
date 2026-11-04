using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

/// <summary>Une zone qui contient un point, avec la position de l'altitude de vol par rapport à elle.</summary>
public sealed record ZoneAtPoint(Airspace Zone, VerticalStatus Status);

/// <summary>Un repère proche : distance en milles et relèvement vrai depuis le point.</summary>
public sealed record NearbyNavaid(Navaid Navaid, double DistanceNm, double BearingTrue);

public sealed record NearbyAirport(Airport Airport, double DistanceNm, double BearingTrue);

/// <summary>Informations calculables sans réseau autour d'un point de la route, pour l'aide en direct.</summary>
public static class WaypointInsight
{
    private static Waypoint At(double lat, double lon) => new() { Lat = lat, Lon = lon };

    /// <summary>
    /// Zones qui contiennent le point à l'altitude de croisière, ou dont le plancher est au plus
    /// <paramref name="ceilingMarginFt"/> au-dessus. Celles où l'on est « dedans » viennent d'abord.
    /// </summary>
    public static IReadOnlyList<ZoneAtPoint> ZonesAt(double lat, double lon, IEnumerable<Airspace> zones, double altitudeFt, double ceilingMarginFt = 1000)
    {
        var result = new List<ZoneAtPoint>();
        foreach (var zone in zones)
        {
            if (zone.LowerFt > altitudeFt + ceilingMarginFt) continue;
            if (!zone.Contains(lon, lat)) continue;
            var status = altitudeFt < zone.LowerFt ? VerticalStatus.Below : altitudeFt > zone.UpperFt ? VerticalStatus.Above : VerticalStatus.Inside;
            result.Add(new ZoneAtPoint(zone, status));
        }
        return result.OrderBy(z => z.Status).ThenBy(z => z.Zone.LowerFt).ToList();
    }

    public static IReadOnlyList<NearbyNavaid> NearestNavaids(double lat, double lon, IEnumerable<Navaid> navaids, int count = 3, double maxNm = 40)
    {
        var from = At(lat, lon);
        return navaids
            .Select(n => new NearbyNavaid(n, NavMath.DistanceNm(from, At(n.Lat, n.Lon)), NavMath.TrueCourse(from, At(n.Lat, n.Lon))))
            .Where(n => n.DistanceNm <= maxNm)
            .OrderBy(n => n.DistanceNm)
            .Take(count)
            .ToList();
    }

    public static IReadOnlyList<NearbyAirport> NearestAirports(double lat, double lon, IEnumerable<Airport> airports, int count = 3, double maxNm = 30)
    {
        var from = At(lat, lon);
        return airports
            .Select(a => new NearbyAirport(a, NavMath.DistanceNm(from, At(a.Lat, a.Lon)), NavMath.TrueCourse(from, At(a.Lat, a.Lon))))
            .Where(a => a.DistanceNm <= maxNm)
            .OrderBy(a => a.DistanceNm)
            .Take(count)
            .ToList();
    }

    /// <summary>« 3,2 NM au 310° » : repère par rapport au point.</summary>
    public static string Relative(double distanceNm, double bearingTrue) =>
        Loc.F("Insight_Relative", distanceNm.ToString("0.0", Loc.FormatCulture), bearingTrue);

    /// <summary>
    /// Résumé de la route en construction : zones où l'on vole à son altitude (dont la classe A, interdite au vol à vue)
    /// et tronçons où le relief laisse moins de <paramref name="marginFt"/> sous la croisière.
    /// </summary>
    public static IReadOnlyList<string> RouteCheck(
        IReadOnlyList<Waypoint> route, double cruiseFt, IReadOnlyList<AirspaceCrossing> crossings, TerrainProfile? terrain, double marginFt = 1000, bool zonesLoaded = true, AltitudeProfile? altitudes = null)
    {
        var lines = new List<string>();
        if (route.Count < 2) return lines;

        var classA = crossings.Where(c => c.Status == VerticalStatus.Inside && c.Zone.IcaoClass == "A").Select(c => c.Zone.Name).Distinct().ToList();
        if (classA.Count > 0) lines.Add(Loc.F("WaypointInsight_Classe_A_a_ton_altitude", string.Join(", ", classA)));

        var inside = crossings.Where(c => c.Status == VerticalStatus.Inside && c.Zone.IcaoClass != "A").ToList();
        var restricted = inside.Where(c => c.Zone.Kind == AirspaceKind.Restricted).Select(c => c.Zone.Name).Distinct().ToList();
        if (restricted.Count > 0) lines.Add(Loc.F("WaypointInsight_Zone_s_reglementee_s_a", string.Join(", ", restricted)));
        int others = inside.Count(c => c.Zone.Kind != AirspaceKind.Restricted);
        if (others > 0) lines.Add(Loc.F("WaypointInsight_autre_s_zone_s_traversee", others));
        if (!zonesLoaded) lines.Add(Loc.Get("WaypointInsight_Zones_non_chargees_cle_OpenAIP"));
        else if (classA.Count == 0 && restricted.Count == 0 && others == 0) lines.Add(Loc.Get("WaypointInsight_Aucune_zone_connue_a_ton"));

        if (terrain is not null)
        {
            var warnings = altitudes is null ? terrain.Warnings(cruiseFt, marginFt) : terrain.Warnings(altitudes, marginFt);
            foreach (var w in warnings)
            {
                string leg = w.LegIndex + 1 < route.Count ? $"{route[w.LegIndex].Name} → {route[w.LegIndex + 1].Name}" : Loc.F("WaypointInsight_troncon", w.LegIndex + 1);
                string margin = w.ClearanceFt < 0 ? Loc.F("WaypointInsight_relief_ft_AU_DESSUS_de", Math.Abs(w.ClearanceFt)) : Loc.F("WaypointInsight_marge_de_ft_seulement", w.ClearanceFt);
                lines.Add(Loc.F("Insight_Terrain_warning", leg, w.MaxFt, margin));
            }
            if (warnings.Count == 0) lines.Add(Loc.F("WaypointInsight_Relief_point_le_plus_haut", terrain.MaxFt, marginFt));
        }
        return lines;
    }
}
