using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Un point échantillonné le long de la route.</summary>
/// <param name="DistanceNm">Distance depuis le départ, en milles nautiques.</param>
/// <param name="LegIndex">Numéro du tronçon (0 = premier) auquel appartient le point.</param>
public sealed record RoutePoint(double DistanceNm, double Lat, double Lon, int LegIndex);

public static class RouteGeometry
{
    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;

    /// <summary>Point situé à la fraction f (0 à 1) du grand cercle entre a et b.</summary>
    public static (double Lat, double Lon) Interpolate(double lat1, double lon1, double lat2, double lon2, double f)
    {
        double p1 = Rad(lat1), l1 = Rad(lon1), p2 = Rad(lat2), l2 = Rad(lon2);
        double d = 2 * Math.Asin(Math.Sqrt(Math.Pow(Math.Sin((p2 - p1) / 2), 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin((l2 - l1) / 2), 2)));
        if (d < 1e-12) return (lat1, lon1);
        double a = Math.Sin((1 - f) * d) / Math.Sin(d), b = Math.Sin(f * d) / Math.Sin(d);
        double x = a * Math.Cos(p1) * Math.Cos(l1) + b * Math.Cos(p2) * Math.Cos(l2);
        double y = a * Math.Cos(p1) * Math.Sin(l1) + b * Math.Cos(p2) * Math.Sin(l2);
        double z = a * Math.Sin(p1) + b * Math.Sin(p2);
        return (Deg(Math.Atan2(z, Math.Sqrt(x * x + y * y))), Deg(Math.Atan2(y, x)));
    }

    private const double EarthRadiusNm = 3440.065;

    /// <summary>Point atteint en partant de (lat, lon) sur le cap vrai donné, après la distance donnée (grand cercle).</summary>
    public static (double Lat, double Lon) Destination(double lat, double lon, double bearingDeg, double distanceNm)
    {
        double p1 = Rad(lat), l1 = Rad(lon), brg = Rad(bearingDeg), d = distanceNm / EarthRadiusNm;
        double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(brg));
        double l2 = l1 + Math.Atan2(Math.Sin(brg) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        return (Deg(p2), ((Deg(l2) + 540) % 360) - 180);
    }

    /// <summary>
    /// Cercle de rayon <paramref name="radiusNm"/> autour d'un point, en lignes prêtes à dessiner sur une carte Mercator.
    /// La ligne est coupée là où elle franchit la ligne de changement de date ou dépasse la latitude affichable
    /// (<paramref name="maxLat"/>) : un très grand cercle donne alors plusieurs arcs, un petit cercle une seule boucle fermée.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> CircleLines(
        double lat, double lon, double radiusNm, int points = 180, double maxLat = 84)
    {
        var lines = new List<IReadOnlyList<(double, double)>>();
        if (!(radiusNm > 0) || points < 8) return lines;

        var ring = new List<(double Lat, double Lon)>();
        for (int i = 0; i <= points; i++) ring.Add(Destination(lat, lon, 360.0 * i / points, radiusNm));

        var current = new List<(double, double)>();
        for (int i = 0; i < ring.Count; i++)
        {
            bool visible = Math.Abs(ring[i].Lat) <= maxLat;
            bool jump = current.Count > 0 && Math.Abs(ring[i].Lon - current[^1].Item2) > 180;
            if (!visible || jump)
            {
                if (current.Count > 1) lines.Add(current);
                current = [];
                if (!visible) continue;
            }
            current.Add(ring[i]);
        }
        if (current.Count > 1) lines.Add(current);

        // Boucle entière sans coupure : la dernière ligne rejoint déjà la première, rien à recoller.
        return lines;
    }

    /// <summary>
    /// Tracé de la route pour la carte : chaque branche suit le grand cercle (c'est lui que mesurent la distance et le cap du log),
    /// échantillonné tous les <paramref name="stepNm"/> milles, et non la ligne droite de la projection Mercator qui est trompeuse sur
    /// les longues branches. Les lignes sont coupées là où la route franchit l'antiméridien (avec des points sur ±180°), et les latitudes bornées.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> GreatCircleLines(IReadOnlyList<Waypoint> route, double stepNm = 30, double maxLat = 85)
    {
        var lines = new List<IReadOnlyList<(double, double)>>();
        if (route.Count < 2) return lines;

        var current = new List<(double Lat, double Lon)>();
        for (int leg = 0; leg < route.Count - 1; leg++)
        {
            var a = route[leg];
            var b = route[leg + 1];
            int n = Math.Clamp((int)Math.Ceiling(NavMath.DistanceNm(a, b) / Math.Max(stepNm, 1)), 1, 720);
            for (int k = leg == 0 ? 0 : 1; k <= n; k++)
            {
                var (lat, lon) = k == 0 ? (a.Lat, a.Lon) : k == n ? (b.Lat, b.Lon) : Interpolate(a.Lat, a.Lon, b.Lat, b.Lon, (double)k / n);
                lat = Math.Clamp(lat, -maxLat, maxLat);

                if (current.Count > 0 && Math.Abs(lon - current[^1].Lon) > 180)
                {
                    // Franchissement de l'antiméridien : on termine la ligne sur le bord et on repart de l'autre bord à la même latitude.
                    var (pLat, pLon) = current[^1];
                    double edge = pLon > 0 ? 180 : -180;
                    double unwrapped = lon + (pLon > 0 ? 360 : -360);
                    double t = (edge - pLon) / (unwrapped - pLon);
                    double edgeLat = pLat + t * (lat - pLat);
                    current.Add((edgeLat, edge));
                    if (current.Count > 1) lines.Add(current);
                    current = [(edgeLat, -edge)];
                }
                current.Add((lat, lon));
            }
        }
        if (current.Count > 1) lines.Add(current);
        return lines;
    }

    /// <summary>
    /// Points régulièrement espacés le long de la route, extrémités des tronçons comprises.
    /// Le pas est agrandi si besoin pour ne pas dépasser <paramref name="maxPoints"/> points.
    /// </summary>
    public static IReadOnlyList<RoutePoint> Sample(IReadOnlyList<Waypoint> route, double stepNm = 1, int maxPoints = 400)
    {
        var points = new List<RoutePoint>();
        if (route.Count < 2) return points;

        double total = 0;
        for (int i = 0; i < route.Count - 1; i++) total += NavMath.DistanceNm(route[i], route[i + 1]);
        double step = Math.Max(stepNm, total / Math.Max(maxPoints - route.Count, 1));

        double done = 0;
        for (int leg = 0; leg < route.Count - 1; leg++)
        {
            var a = route[leg];
            var b = route[leg + 1];
            double length = NavMath.DistanceNm(a, b);
            int n = Math.Max(1, (int)Math.Ceiling(length / step));
            for (int k = leg == 0 ? 0 : 1; k <= n; k++)
            {
                double f = (double)k / n;
                var (lat, lon) = k == 0 ? (a.Lat, a.Lon) : k == n ? (b.Lat, b.Lon) : Interpolate(a.Lat, a.Lon, b.Lat, b.Lon, f);
                points.Add(new RoutePoint(done + length * f, lat, lon, leg));
            }
            done += length;
        }
        return points;
    }

    /// <summary>
    /// Distance du point au tronçon ab en milles, et position de sa projection sur le tronçon (0 à 1).
    /// Calcul sur plan local : exact à quelques dizaines de milles, ce qui suffit pour « proche de la route ».
    /// </summary>
    public static (double DistanceNm, double Along) DistanceToSegment(double lat, double lon, Waypoint a, Waypoint b)
    {
        double cos = Math.Cos(Rad((a.Lat + b.Lat) / 2));
        double ax = a.Lon * cos * 60, ay = a.Lat * 60;
        double bx = b.Lon * cos * 60, by = b.Lat * 60;
        double px = lon * cos * 60, py = lat * 60;
        double dx = bx - ax, dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        double cx = ax + t * dx - px, cy = ay + t * dy - py;
        return (Math.Sqrt(cx * cx + cy * cy), t);
    }
}
