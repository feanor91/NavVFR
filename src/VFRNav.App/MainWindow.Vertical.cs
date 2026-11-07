using System.Windows;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.App;

// ---------- Profil vertical IFR : altitude atteignable, TOC, TOD et vitesses de décollage ----------
public partial class MainWindow
{
    /// <summary>Altitude de croisière demandée en IFR : le niveau saisi (FL), sinon celle de l'appareil.</summary>
    private double RequestedIfrCruiseFt(Aircraft aircraft) =>
        int.TryParse(IfrFlBox.Text.Trim(), out int fl) && fl > 0 ? fl * 100.0 : aircraft.CruiseAltFt;

    /// <summary>Route magnétique du départ vers l'arrivée, pour la règle semi-circulaire des niveaux.</summary>
    private double MagneticTrack(Waypoint from, Waypoint to) => NavMath.Norm360(NavMath.TrueCourse(from, to) - ReadParams().VariationEast);

    /// <summary>Profil vertical de la route actuelle avec l'appareil IFR, ou null (moins de deux points, pas d'appareil).</summary>
    private (VerticalPlan Plan, Aircraft Aircraft)? CurrentVerticalPlan()
    {
        if (_route.Count < 2 || AircraftFor(FlightMode.Ifr) is not { } aircraft) return null;
        var route = _route.ToList();
        double distance = NavMath.ComputeLog(route, ReadParams()).TotalDistanceNm;
        var plan = VerticalPlanner.Compute(distance, route[0].ElevationFt, route[^1].ElevationFt, RequestedIfrCruiseFt(aircraft), aircraft, MagneticTrack(route[0], route[^1]));
        return (plan, aircraft);
    }

    /// <summary>Lignes du profil vertical et des vitesses de décollage (panneau IFR, feuille de route).</summary>
    private IReadOnlyList<string> VerticalLines()
    {
        if (CurrentVerticalPlan() is not { } v) return [];
        var route = _route;
        var lines = VerticalPlanner.Lines(v.Plan, route[0].Icao ?? route[0].Name, route[^1].Icao ?? route[^1].Name, v.Aircraft.Name).ToList();
        if (VerticalPlanner.TakeoffSpeedsLine(v.Aircraft) is { } speeds) lines.Insert(0, speeds);
        return lines;
    }

    private void RefreshVerticalInfo()
    {
        if (!IsLoaded) return;
        IfrPerfInfo.Text = string.Join("\n", VerticalLines());
    }

    /// <summary>
    /// Place « TOC » (fin de la montée) et « TOD » (début de la descente) sur la route, à l'altitude de croisière : la route les montre sur la carte
    /// et dans le profil. Sur une route trop courte pour une croisière, un seul point « TOC/TOD » marque le sommet.
    /// </summary>
    private static void InsertVerticalPoints(List<Waypoint> route, VerticalPlan plan)
    {
        double total = Total(route);
        if (plan.TopOfClimbNm >= plan.TopOfDescentNm - 1)
        {
            Insert(route, (plan.TopOfClimbNm + plan.TopOfDescentNm) / 2, "TOC/TOD", plan.CruiseFt);
            return;
        }
        if (plan.TopOfDescentNm < total - 1) Insert(route, plan.TopOfDescentNm, "TOD", plan.CruiseFt);   // d'abord le plus loin : les distances du début ne bougent pas
        if (plan.TopOfClimbNm > 1) Insert(route, plan.TopOfClimbNm, "TOC", plan.CruiseFt);
    }

    private static double Total(IReadOnlyList<Waypoint> route)
    {
        double total = 0;
        for (int i = 1; i < route.Count; i++) total += NavMath.DistanceNm(route[i - 1], route[i]);
        return total;
    }

    private static void Insert(List<Waypoint> route, double nm, string name, double altitudeFt)
    {
        double along = 0;
        for (int i = 1; i < route.Count; i++)
        {
            double leg = NavMath.DistanceNm(route[i - 1], route[i]);
            if (along + leg >= nm && leg > 0)
            {
                var (lat, lon) = RouteGeometry.Interpolate(route[i - 1].Lat, route[i - 1].Lon, route[i].Lat, route[i].Lon, (nm - along) / leg);
                route.Insert(i, new Waypoint { Name = name, Lat = Math.Round(lat, 5), Lon = Math.Round(lon, 5), AltitudeFt = altitudeFt });
                return;
            }
            along += leg;
        }
    }
}
