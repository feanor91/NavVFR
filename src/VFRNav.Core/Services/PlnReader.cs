using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

/// <param name="CruiseAltFt">Altitude de croisière du plan, si le fichier en donne une.</param>
public sealed record PlnPlan(IReadOnlyList<Waypoint> Route, double? CruiseAltFt, string? Title);

/// <summary>
/// Lit un plan de vol .PLN (AceXML) de Microsoft Flight Simulator : ceux que MSFS Rhumb écrit comme ceux d'autres outils ou du simulateur.
/// Les notes et altitudes par point de MSFS Rhumb, qu'un .PLN ne peut pas porter, sont relues dans le commentaire XML que
/// <see cref="PlnWriter"/> y ajoute ; un plan venu d'ailleurs s'ouvre sans elles.
/// </summary>
public static partial class PlnReader
{
    public const string ExtrasMarker = "VFRNav:";

    /// <summary>Lit le texte d'un fichier .PLN. Lève <see cref="FormatException"/> si ce n'est pas un plan de vol exploitable.</summary>
    public static PlnPlan Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException ex) { throw new FormatException(Loc.Get("PlnReader_Ce_fichier_n_est_pas") + ex.Message, ex); }

        var plan = doc.Descendants("FlightPlan.FlightPlan").FirstOrDefault()
            ?? throw new FormatException(Loc.Get("PlnReader_Ce_fichier_ne_contient_pas"));

        double? cruise = Number(plan.Element("CruisingAlt")?.Value);
        var extras = ReadExtras(doc);
        var route = new List<Waypoint>();
        foreach (var (el, index) in plan.Elements("ATCWaypoint").Select((e, i) => (e, i)))
        {
            string id = (string?)el.Attribute("id") ?? $"WP{index + 1}";
            var m = PositionRx().Match(el.Element("WorldPosition")?.Value ?? "");
            if (!m.Success) throw new FormatException(Loc.F("PlnReader_Position_illisible_pour_le_point", id));

            double lat = Sign(m.Groups[1].Value) * Dms(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            double lon = Sign(m.Groups[5].Value) * Dms(m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
            double alt = Number(m.Groups[9].Value) ?? 0;

            string? icao = el.Element("ICAO")?.Element("ICAOIdent")?.Value;
            bool airport = string.Equals(el.Element("ATCWaypointType")?.Value, "Airport", StringComparison.OrdinalIgnoreCase);
            var wp = new Waypoint { Name = id, Lat = lat, Lon = lon };
            if (airport) { wp.Icao = string.IsNullOrWhiteSpace(icao) ? id : icao.Trim(); wp.ElevationFt = alt; }

            if (extras.TryGetValue(index, out var x))
            {
                wp.Notes = x.Notes;
                wp.FullName = x.FullName;
                wp.AltitudeFt = x.AltitudeFt;
            }
            else if (!airport && cruise is { } c && Math.Abs(alt - c) > 0.5)
            {
                wp.AltitudeFt = alt; // plan venu d'ailleurs : un point qui n'est pas à la croisière a son altitude propre
            }
            route.Add(wp);
        }
        if (route.Count == 0) throw new FormatException(Loc.Get("PlnReader_Le_plan_de_vol_ne"));
        return new PlnPlan(route, cruise, plan.Element("Title")?.Value);
    }

    // ---------- informations propres à MSFS Rhumb (notes, altitudes, nom complet) ----------

    internal sealed record Extra(string? Notes, string? FullName, double? AltitudeFt);

    private sealed class ExtraDto
    {
        public int I { get; set; }
        public string? N { get; set; }
        public string? F { get; set; }
        public double? A { get; set; }
    }

    internal static string WriteExtras(IReadOnlyList<Waypoint> route)
    {
        var list = route.Select((w, i) => new ExtraDto { I = i, N = Blank(w.Notes), F = Blank(w.FullName), A = w.AltitudeFt })
            .Where(d => d.N is not null || d.F is not null || d.A is not null).ToList();
        return list.Count == 0 ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list)));
    }

    private static Dictionary<int, Extra> ReadExtras(XDocument doc)
    {
        var result = new Dictionary<int, Extra>();
        foreach (var comment in doc.DescendantNodes().OfType<XComment>())
        {
            string text = comment.Value.Trim();
            if (!text.StartsWith(ExtrasMarker, StringComparison.Ordinal)) continue;
            try
            {
                var list = JsonSerializer.Deserialize<List<ExtraDto>>(Encoding.UTF8.GetString(Convert.FromBase64String(text[ExtrasMarker.Length..].Trim())),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                foreach (var d in list ?? []) result[d.I] = new Extra(d.N, d.F, d.A);
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { /* commentaire altéré : on ignore les extras */ }
        }
        return result;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---------- positions ----------

    private static int Sign(string letter) => letter is "S" or "W" ? -1 : 1;

    private static double Dms(string degrees, string minutes, string seconds) =>
        (Number(degrees) ?? 0) + (Number(minutes) ?? 0) / 60 + (Number(seconds) ?? 0) / 3600;

    private static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>« N48° 49' 18.84",E2° 37' 22.08",+000351.00 » : latitude, longitude, altitude en pieds.</summary>
    [GeneratedRegex(@"^\s*([NS])\s*(\d+)\D+?(\d+)\D+?(\d+(?:\.\d+)?)\D*,\s*([EW])\s*(\d+)\D+?(\d+)\D+?(\d+(?:\.\d+)?)\D*,\s*([+-]?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex PositionRx();
}
