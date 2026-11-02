using System.Globalization;
using System.Xml.Linq;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Écrit un plan de vol .PLN (AceXML) lisible par Microsoft Flight Simulator, et relu tel quel par <see cref="PlnReader"/>.</summary>
public static class PlnWriter
{
    private static string Dms(double value, char pos, char neg, int degWidth)
    {
        char letter = value >= 0 ? pos : neg;
        double v = Math.Abs(value);
        int d = (int)Math.Floor(v);
        int m = (int)Math.Floor((v - d) * 60);
        double s = ((v - d) * 60 - m) * 60;
        if (s >= 59.995) { s = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        var inv = CultureInfo.InvariantCulture;
        return $"{letter}{d.ToString(inv).PadLeft(degWidth, '0')}° {m.ToString("00", inv)}' {s.ToString("00.00", inv)}\"";
    }

    public static string Lla(Waypoint wp, double altFt)
    {
        double alt = Math.Round(altFt, 2);
        string sign = alt >= 0 ? "+" : "-";
        string a = Math.Abs(alt).ToString("000000.00", CultureInfo.InvariantCulture);
        return $"{Dms(wp.Lat, 'N', 'S', 2)},{Dms(wp.Lon, 'E', 'W', 3)},{sign}{a}";
    }

    public static string Build(IReadOnlyList<Waypoint> route, double cruiseAltFt)
    {
        if (route.Count < 2) throw new ArgumentException("Il faut au moins deux points.", nameof(route));
        var first = route[0];
        var last = route[^1];
        string depId = first.Icao ?? first.Name;
        string dstId = last.Icao ?? last.Name;
        string title = $"{depId} to {dstId}";

        var plan = new XElement("FlightPlan.FlightPlan",
            new XElement("Title", title),
            new XElement("FPType", "VFR"),
            new XElement("RouteType", "Direct"),
            new XElement("CruisingAlt", cruiseAltFt.ToString("0.000", CultureInfo.InvariantCulture)),
            new XElement("DepartureID", depId),
            new XElement("DepartureLLA", Lla(first, first.ElevationFt)),
            new XElement("DestinationID", dstId),
            new XElement("DestinationLLA", Lla(last, last.ElevationFt)),
            new XElement("Descr", title),
            new XElement("DepartureName", first.FullName ?? first.Name),
            new XElement("DestinationName", last.FullName ?? last.Name),
            new XElement("AppVersion",
                new XElement("AppVersionMajor", "11"),
                new XElement("AppVersionBuild", "282174")));

        for (int i = 0; i < route.Count; i++)
        {
            var wp = route[i];
            string id = wp.Icao ?? (string.IsNullOrWhiteSpace(wp.Name) ? $"WP{i}" : wp.Name);
            var el = new XElement("ATCWaypoint", new XAttribute("id", id),
                new XElement("ATCWaypointType", wp.IsAirport ? "Airport" : "User"),
                new XElement("WorldPosition", Lla(wp, wp.IsAirport ? wp.ElevationFt : wp.AltitudeFt ?? cruiseAltFt)));
            if (wp.IsAirport) el.Add(new XElement("ICAO", new XElement("ICAOIdent", wp.Icao)));
            plan.Add(el);
        }

        var root = new XElement("SimBase.Document", new XAttribute("Type", "AceXML"), new XAttribute("version", "1,0"),
            new XElement("Descr", "AceXML Document"), plan);
        // Notes, altitudes par point et noms complets : un .PLN n'a pas de champ pour eux, ils vont dans un commentaire que le simulateur ignore.
        string extras = PlnReader.WriteExtras(route);
        if (extras.Length > 0) root.Add(new XComment($" {PlnReader.ExtrasMarker}{extras} "));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        return doc.Declaration + Environment.NewLine + doc.Root;
    }
}
