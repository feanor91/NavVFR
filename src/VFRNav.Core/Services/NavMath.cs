using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public static class NavMath
{
    private const double EarthRadiusNm = 3440.065;

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    public static double Norm360(double a) => ((a % 360) + 360) % 360;

    public static double DistanceNm(Waypoint a, Waypoint b) => DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon);

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = Rad(lat2 - lat1), dLon = Rad(lon2 - lon1);
        double h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Sqrt(h));
    }

    public static double TrueCourse(Waypoint a, Waypoint b)
    {
        double dLon = Rad(b.Lon - a.Lon);
        double y = Math.Sin(dLon) * Math.Cos(Rad(b.Lat));
        double x = Math.Cos(Rad(a.Lat)) * Math.Sin(Rad(b.Lat)) - Math.Sin(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Cos(dLon);
        return Norm360(Deg(Math.Atan2(y, x)));
    }

    /// <summary>Triangle des vitesses. windDir = direction d'où vient le vent.</summary>
    public static (double Wca, double TrueHeading, double GroundSpeed) WindTriangle(double tc, double tas, double windDir, double windKt)
    {
        double rel = Rad(windDir - tc);
        double s = windKt / tas * Math.Sin(rel);
        if (Math.Abs(s) >= 1) return (0, tc, 0);
        double wca = Math.Asin(s);
        double gs = tas * Math.Cos(wca) - windKt * Math.Cos(rel);
        return (Deg(wca), Norm360(tc + Deg(wca)), Math.Max(gs, 0));
    }

    public static NavLog ComputeLog(IReadOnlyList<Waypoint> route, FlightParameters p)
    {
        var legs = new List<LegResult>();
        double dist = 0, minutes = 0;
        for (int i = 0; i < route.Count - 1; i++)
        {
            var a = route[i];
            var b = route[i + 1];
            double d = DistanceNm(a, b);
            double tc = TrueCourse(a, b);
            var (wca, th, gs) = WindTriangle(tc, p.TasKt, p.WindDir, p.WindKt);
            double min = gs > 0 ? d / gs * 60 : double.PositiveInfinity;
            legs.Add(new LegResult(a.Name, b.Name, d, tc, Norm360(tc - p.VariationEast), wca,
                Norm360(th - p.VariationEast), gs, min, min / 60 * p.BurnLph));
            dist += d;
            minutes += min;
        }
        return new NavLog(legs, dist, minutes, minutes / 60 * p.BurnLph);
    }

    public static string FormatTime(double minutes)
    {
        if (double.IsInfinity(minutes) || double.IsNaN(minutes)) return "--";
        int m = (int)Math.Round(minutes);
        return $"{m / 60}h{m % 60:00}";
    }
}
