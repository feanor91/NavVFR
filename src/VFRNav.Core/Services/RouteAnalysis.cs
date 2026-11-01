using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public enum VerticalStatus { Inside, Above, Below }

/// <summary>Passage de la route dans une zone.</summary>
/// <param name="EntryNm">Distance depuis le départ à l'entrée dans la zone.</param>
/// <param name="ExitNm">Distance à la sortie.</param>
/// <param name="Status">Dans la zone, au-dessus de son plafond ou sous son plancher, à l'altitude de croisière.</param>
public sealed record AirspaceCrossing(Airspace Zone, double EntryNm, double ExitNm, VerticalStatus Status);

/// <summary>Balise proche de la route.</summary>
/// <param name="AlongNm">Position le long de la route (distance depuis le départ) du point le plus proche.</param>
/// <param name="OffsetNm">Écart latéral à la route.</param>
public sealed record NavaidNearRoute(Navaid Navaid, double AlongNm, double OffsetNm);

public static class RouteAnalysis
{
    /// <summary>
    /// Zones que la route traverse latéralement. Une zone dont le plancher est plus haut que
    /// <paramref name="cruiseFt"/> + <paramref name="ceilingMarginFt"/> est ignorée.
    /// </summary>
    public static IReadOnlyList<AirspaceCrossing> AirspacesAlong(IReadOnlyList<RoutePoint> samples, IEnumerable<Airspace> zones, double cruiseFt, double ceilingMarginFt = 1000)
    {
        var result = new List<AirspaceCrossing>();
        if (samples.Count == 0) return result;
        double minLat = samples.Min(s => s.Lat), maxLat = samples.Max(s => s.Lat);
        double minLon = samples.Min(s => s.Lon), maxLon = samples.Max(s => s.Lon);

        foreach (var zone in zones)
        {
            if (zone.LowerFt > cruiseFt + ceilingMarginFt) continue;
            if (!OverlapsBox(zone, minLat, minLon, maxLat, maxLon)) continue;

            double? entry = null;
            double last = 0;
            foreach (var s in samples)
            {
                bool inside = zone.Contains(s.Lon, s.Lat);
                if (inside && entry is null) entry = s.DistanceNm;
                if (!inside && entry is not null)
                {
                    result.Add(Crossing(zone, entry.Value, last, cruiseFt));
                    entry = null;
                }
                last = s.DistanceNm;
            }
            if (entry is not null) result.Add(Crossing(zone, entry.Value, last, cruiseFt));
        }
        return result.OrderBy(c => c.EntryNm).ThenBy(c => c.Zone.LowerFt).ToList();
    }

    private static AirspaceCrossing Crossing(Airspace zone, double entry, double exit, double cruiseFt) =>
        new(zone, entry, exit, cruiseFt < zone.LowerFt ? VerticalStatus.Below : cruiseFt > zone.UpperFt ? VerticalStatus.Above : VerticalStatus.Inside);

    private static bool OverlapsBox(Airspace zone, double minLat, double minLon, double maxLat, double maxLon)
    {
        foreach (var ring in zone.Rings)
        {
            if (ring.Count == 0) continue;
            if (ring.Max(p => p.Lat) < minLat || ring.Min(p => p.Lat) > maxLat || ring.Max(p => p.Lon) < minLon || ring.Min(p => p.Lon) > maxLon) continue;
            return true;
        }
        return false;
    }

    /// <summary>Balises à moins de <paramref name="maxOffsetNm"/> de la route, dans l'ordre du trajet.</summary>
    public static IReadOnlyList<NavaidNearRoute> NavaidsAlong(IReadOnlyList<Waypoint> route, IEnumerable<Navaid> navaids, double maxOffsetNm = 15)
    {
        var result = new List<NavaidNearRoute>();
        if (route.Count < 2) return result;

        var legStart = new double[route.Count - 1];
        double done = 0;
        for (int i = 0; i < route.Count - 1; i++) { legStart[i] = done; done += NavMath.DistanceNm(route[i], route[i + 1]); }

        foreach (var n in navaids)
        {
            double best = double.MaxValue, along = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                var (d, t) = RouteGeometry.DistanceToSegment(n.Lat, n.Lon, route[i], route[i + 1]);
                if (d < best) { best = d; along = legStart[i] + t * NavMath.DistanceNm(route[i], route[i + 1]); }
            }
            if (best <= maxOffsetNm) result.Add(new NavaidNearRoute(n, along, best));
        }
        return result.OrderBy(r => r.AlongNm).ToList();
    }
}
