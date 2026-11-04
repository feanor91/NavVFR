namespace VFRNav.Core.Services;

/// <summary>Où se trouve la zone affichée par rapport aux deux cercles de portée.</summary>
public enum RangeViewState
{
    /// <summary>Un cercle au moins traverse la zone affichée : on le dessine.</summary>
    Crossing,

    /// <summary>Toute la zone affichée est dans la portée utile (avec réserve) : le cercle est plus grand que l'écran.</summary>
    InsideUsable,

    /// <summary>Toute la zone affichée est au-delà de la portée utile mais dans la portée tout carburant : on y arrive en entamant la réserve.</summary>
    InsideReserve,

    /// <summary>Toute la zone affichée est hors de portée, même avec tout le carburant.</summary>
    Beyond,
}

/// <summary>Place des cercles de portée dans la zone affichée de la carte : quand les dessiner, où poser leur étiquette.</summary>
public static class RangeView
{
    /// <summary>Points régulièrement répartis dans la zone affichée (une grille de n × n), pour mesurer où elle se situe.</summary>
    public static IReadOnlyList<(double Lat, double Lon)> Grid(double minLat, double minLon, double maxLat, double maxLon, int n = 7)
    {
        n = Math.Max(2, n);
        var points = new List<(double, double)>(n * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                points.Add((minLat + (maxLat - minLat) * i / (n - 1), minLon + (maxLon - minLon) * j / (n - 1)));
        return points;
    }

    /// <summary>
    /// Classe la zone affichée d'après la distance de ses points au centre des cercles.
    /// <paramref name="usableNm"/> vaut 0 quand la réserve consomme toute l'autonomie : il n'y a alors pas de cercle utile.
    /// </summary>
    public static RangeViewState Classify(double originLat, double originLon, IEnumerable<(double Lat, double Lon)> viewPoints, double usableNm, double fullNm)
    {
        bool allInsideUsable = true, allBeyondFull = true, allInsideFull = true;
        foreach (var (lat, lon) in viewPoints)
        {
            double d = NavMath.DistanceNm(originLat, originLon, lat, lon);
            if (d > usableNm) allInsideUsable = false;
            if (d <= fullNm) allBeyondFull = false;
            if (d > fullNm) allInsideFull = false;
        }

        if (usableNm > 0 && allInsideUsable) return RangeViewState.InsideUsable;
        if (allBeyondFull) return RangeViewState.Beyond;
        // Entre les deux cercles sur toute la zone : aucun trait visible, mais on est dans la réserve.
        if (allInsideFull && usableNm > 0 && NoPointInside(originLat, originLon, viewPoints, usableNm)) return RangeViewState.InsideReserve;
        if (allInsideFull && usableNm <= 0) return RangeViewState.InsideReserve;
        return RangeViewState.Crossing;
    }

    private static bool NoPointInside(double lat0, double lon0, IEnumerable<(double Lat, double Lon)> points, double radiusNm) =>
        points.All(p => NavMath.DistanceNm(lat0, lon0, p.Lat, p.Lon) > radiusNm);

    /// <summary>
    /// Point du cercle visible qui est le plus proche du centre de l'écran : l'endroit où l'étiquette se lit toujours.
    /// Null si aucun point du cercle n'est dans la zone affichée.
    /// </summary>
    public static (double Lat, double Lon)? NearestVisiblePoint(
        IEnumerable<IReadOnlyList<(double Lat, double Lon)>> lines,
        double minLat, double minLon, double maxLat, double maxLon, double centerLat, double centerLon)
    {
        (double Lat, double Lon)? best = null;
        double bestDistance = double.MaxValue;
        foreach (var line in lines)
            foreach (var (lat, lon) in line)
            {
                if (lat < minLat || lat > maxLat || lon < minLon || lon > maxLon) continue;
                double d = NavMath.DistanceNm(centerLat, centerLon, lat, lon);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = (lat, lon);
            }
        return best;
    }
}
