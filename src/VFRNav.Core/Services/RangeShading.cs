using VFRNav.Core.Localization;
namespace VFRNav.Core.Services;

/// <summary>
/// Cercle de portée qui enferme un pôle : sur une carte Mercator il ne peut pas être fermé, il devient une grande vague d'un bord à l'autre.
/// On le montre alors en grisant la zone hors de portée. Toutes les surfaces sont données en latitude/longitude, bornées à ±<see cref="MaxLat"/>.
/// </summary>
public static class RangeShading
{
    /// <summary>Latitude limite de la projection Mercator affichée.</summary>
    public const double MaxLat = 85;

    /// <summary>Le pôle contenu dans le cercle : 'N', 'S', ou null si le cercle est fermé sur la carte.</summary>
    public static char? EnclosedPole(double originLat, double radiusNm)
    {
        if (!(radiusNm > 0)) return null;
        if ((90 - originLat) * 60 < radiusNm) return 'N';
        if ((90 + originLat) * 60 < radiusNm) return 'S';
        return null;
    }

    /// <summary>
    /// Frontière du cercle sous la forme latitude = f(longitude), de -180° à +180° (mêmes valeurs aux deux bords).
    /// Valable seulement pour un cercle qui enferme un pôle : la longitude y est alors strictement monotone le long du cercle.
    /// </summary>
    public static IReadOnlyList<(double Lon, double Lat)> Boundary(double originLat, double originLon, double radiusNm, int points = 720)
    {
        var ring = new List<(double Lon, double Lat)>(points);
        double previous = 0;
        for (int i = 0; i < points; i++)
        {
            var (lat, lon) = RouteGeometry.Destination(originLat, originLon, 360.0 * i / points, radiusNm);
            if (i > 0)
            {
                while (lon - previous > 180) lon -= 360;
                while (lon - previous < -180) lon += 360;
            }
            ring.Add((lon, lat));
            previous = lon;
        }

        // Longitudes ramenées dans [-180, 180[, puis rangées de l'ouest vers l'est.
        var sorted = ring.Select(p => (Lon: ((p.Lon + 180) % 360 + 360) % 360 - 180, p.Lat)).OrderBy(p => p.Lon).ToList();

        // Raccord aux bords de la carte : même latitude à -180° et à +180°, interpolée entre le dernier et le premier point.
        var first = sorted[0];
        var last = sorted[^1];
        double gap = first.Lon + 360 - last.Lon;
        double edgeLat = gap <= 0 ? first.Lat : last.Lat + (first.Lat - last.Lat) * (180 - last.Lon) / gap;
        sorted.Insert(0, (-180, edgeLat));
        sorted.Add((180, edgeLat));
        return sorted.Select(p => (p.Lon, Math.Clamp(p.Lat, -MaxLat, MaxLat))).ToList();
    }

    /// <summary>Contour fermé de la zone <b>hors</b> du cercle (de l'autre côté de la frontière que le pôle enfermé), en (latitude, longitude).</summary>
    public static IReadOnlyList<(double Lat, double Lon)> OutsideRing(double originLat, double originLon, double radiusNm)
    {
        char pole = EnclosedPole(originLat, radiusNm) ?? throw new ArgumentException(Loc.Get("RangeShading_Le_cercle_n_enferme_aucun"), nameof(radiusNm));
        var ring = Boundary(originLat, originLon, radiusNm).Select(p => (p.Lat, p.Lon)).ToList();
        double farLat = pole == 'N' ? -MaxLat : MaxLat;     // le bord opposé au pôle enfermé
        ring.Add((farLat, 180));
        ring.Add((farLat, -180));
        ring.Add(ring[0]);
        return ring;
    }

    /// <summary>
    /// Contour fermé de la bande entre deux cercles qui enferment le même pôle (ici : la réserve, entre la portée utile et tout le carburant).
    /// Le grand cercle est parcouru d'ouest en est, le petit d'est en ouest.
    /// </summary>
    public static IReadOnlyList<(double Lat, double Lon)> BandRing(double originLat, double originLon, double innerNm, double outerNm)
    {
        if (EnclosedPole(originLat, innerNm) is null || EnclosedPole(originLat, innerNm) != EnclosedPole(originLat, outerNm))
            throw new ArgumentException(Loc.Get("RangeShading_Les_deux_cercles_doivent_enfermer"), nameof(innerNm));
        var ring = Boundary(originLat, originLon, outerNm).Select(p => (p.Lat, p.Lon)).ToList();
        ring.AddRange(Boundary(originLat, originLon, innerNm).Select(p => (p.Lat, p.Lon)).Reverse());
        ring.Add(ring[0]);
        return ring;
    }

    /// <summary>Le point est-il dans le contour (latitude, longitude, longitudes de -180° à 180°) ? Sert aux tests et aux vérifications.</summary>
    public static bool Contains(IReadOnlyList<(double Lat, double Lon)> ring, double lat, double lon)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double yi = ring[i].Lat, yj = ring[j].Lat, xi = ring[i].Lon, xj = ring[j].Lon;
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}
