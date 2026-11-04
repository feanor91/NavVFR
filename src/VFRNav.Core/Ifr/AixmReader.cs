using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Ifr;

/// <summary>Zone géographique pour ne garder que les données utiles (le fichier du SIA couvre aussi l'outre-mer).</summary>
public sealed record GeoArea(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public bool Contains(double lat, double lon) => lat >= MinLat && lat <= MaxLat && lon >= MinLon && lon <= MaxLon;

    /// <summary>France métropolitaine avec une marge pour les points frontaliers.</summary>
    public static GeoArea MetropolitanFrance { get; } = new(40.5, -6.0, 52.0, 10.5);
}

public sealed record AixmLoadResult(IfrNavData Data, IReadOnlyList<string> Warnings, IlsCatalog? IlsOrNull = null)
{
    /// <summary>ILS de l'export (France), avec la position du seuil de leur piste.</summary>
    public IlsCatalog Ils => IlsOrNull ?? IlsCatalog.Empty;
}

/// <summary>
/// Lit l'export AIXM 4.5 du SIA (« AIXM4.5_all_FR_OM_aaaa-mm-jj.xml », ou le zip qui le contient) :
/// points désignés, balises VOR/NDB/DME, terrains et routes ATS.
/// Cet export ne contient PAS les SID/STAR : la base obtenue n'a donc aucune procédure.
/// </summary>
public static class AixmReader
{
    public static AixmLoadResult LoadFile(string path, GeoArea? area = null)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.StartsWith("AIXM4.5", StringComparison.OrdinalIgnoreCase)
                                                        && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException(Loc.Get("AixmReader_Le_zip_ne_contient_pas"));
            using var stream = entry.Open();
            return Read(stream, area);
        }
        using var file = File.OpenRead(path);
        return Read(file, area);
    }

    public static AixmLoadResult Read(Stream stream, GeoArea? area = null)
    {
        var warnings = new List<string>();
        var fixes = new Dictionary<string, IfrFix>(StringComparer.OrdinalIgnoreCase);
        var segments = new List<(string Airway, XElement Start, XElement End, int? Lower, int? Upper)>();
        string? cycle = null;
        var thresholds = new Dictionary<(string Airport, string Runway), (double Lat, double Lon, double True, double? Magnetic)>();
        var ilsElements = new List<XElement>();
        DateTime? effective = null;
        int collisions = 0;

        void AddFix(string? ident, double? lat, double? lon, FixKind kind)
        {
            if (string.IsNullOrWhiteSpace(ident) || lat is null || lon is null) return;
            if (area is not null && !area.Contains(lat.Value, lon.Value)) return;
            if (fixes.TryGetValue(ident, out var known))
            {
                // Même identifiant à un autre endroit (ex. un point d'outre-mer) : on garde le premier.
                if (NavMath.DistanceNm(known.Lat, known.Lon, lat.Value, lon.Value) > 3) collisions++;
                return;
            }
            fixes[ident] = new IfrFix(ident, lat.Value, lon.Value, kind);
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true };
        using var xml = XmlReader.Create(stream, settings);
        xml.MoveToContent();
        if (xml.LocalName != "AIXM-Snapshot") throw new InvalidDataException(Loc.Get("AixmReader_Ce_fichier_n_est_pas"));
        // La date porte un décalage horaire (« 2026-10-29T00:00:00.000+01:00 ») : on garde la date locale du fichier.
        if (DateTimeOffset.TryParse(xml.GetAttribute("effective"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var eff))
        {
            effective = eff.Date;
            cycle = AiracCycleOf(eff.Date);
        }

        xml.Read();
        while (!xml.EOF && xml.NodeType != XmlNodeType.EndElement)
        {
            if (xml.NodeType != XmlNodeType.Element) { xml.Read(); continue; }
            switch (xml.LocalName)
            {
                case "Vor" or "Ndb" or "Dme" or "Dpn" or "Ahp" or "Rsg" or "Rdn" or "Ils":
                    var e = (XElement)XNode.ReadFrom(xml);
                    switch (e.Name.LocalName)
                    {
                        case "Vor": AddPoint(e, "VorUid", FixKind.Vor); break;
                        case "Ndb": AddPoint(e, "NdbUid", FixKind.Ndb); break;
                        case "Dme": AddPoint(e, "DmeUid", FixKind.Dme); break;
                        case "Dpn": AddPoint(e, "DpnUid", FixKind.Waypoint); break;
                        case "Ahp": AddAirport(e); break;
                        case "Rsg": AddSegment(e); break;
                        case "Rdn": AddThreshold(e); break;
                        case "Ils": ilsElements.Add(e); break;
                    }
                    break;
                default:
                    xml.Skip();
                    break;
            }
        }

        // Les tronçons portent leurs coordonnées : un point absent des listes ci-dessus est ajouté depuis eux.
        var result = new List<AirwaySegment>();
        foreach (var (airway, start, end, lower, upper) in segments)
        {
            var from = EndpointIdent(start);
            var to = EndpointIdent(end);
            if (from is null || to is null) continue;
            AddFix(from, Lat(start), Lon(start), KindOf(start));
            AddFix(to, Lat(end), Lon(end), KindOf(end));
            result.Add(new AirwaySegment(airway, from, to, AirwayDirection.Both, lower, upper));
        }

        if (collisions > 0)
            warnings.Add(Loc.F("AixmReader_identifiant_s_de_point_utilise", collisions));

        var ils = ilsElements.Select(e => ReadIls(e, thresholds, area)).OfType<IlsApproach>().ToList();
        return new AixmLoadResult(new IfrNavData(fixes.Values, result, [], cycle, effective), warnings, new IlsCatalog(ils));

        void AddPoint(XElement e, string uid, FixKind kind)
        {
            var u = e.Element(uid);
            AddFix(u?.Element("codeId")?.Value, Lat(u), Lon(u), kind);
        }

        void AddAirport(XElement e)
        {
            // Seuls les terrains à code OACI à quatre lettres servent de départ ou d'arrivée.
            string? id = e.Element("AhpUid")?.Element("codeId")?.Value;
            if (e.Element("codeType")?.Value != "AD" || id is not { Length: 4 } || !id.All(char.IsLetter)) return;
            AddFix(id, Lat(e), Lon(e), FixKind.Airport);
        }

        void AddThreshold(XElement e)
        {
            var uid = e.Element("RdnUid");
            string? airport = uid?.Element("RwyUid")?.Element("AhpUid")?.Element("codeId")?.Value;
            string? runway = uid?.Element("txtDesig")?.Value;
            double? lat = Lat(e), lon = Lon(e);
            if (airport is null || runway is null || lat is null || lon is null || !TryDouble(e.Element("valTrueBrg")?.Value, out double trueBearing)) return;
            thresholds[(airport, runway.Trim())] = (lat.Value, lon.Value, trueBearing, TryDouble(e.Element("valMagBrg")?.Value, out double magnetic) ? magnetic : null);
        }

        void AddSegment(XElement e)
        {
            var uid = e.Element("RsgUid");
            string? airway = uid?.Element("RteUid")?.Element("txtDesig")?.Value;
            var start = uid?.Elements().FirstOrDefault(x => x.Name.LocalName.EndsWith("UidSta", StringComparison.Ordinal));
            var end = uid?.Elements().FirstOrDefault(x => x.Name.LocalName.EndsWith("UidEnd", StringComparison.Ordinal));
            if (airway is null || start is null || end is null) return;
            segments.Add((airway, start, end, FlightLevel(e, "Lower"), FlightLevel(e, "Upper")));
        }
    }

    private static bool TryDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Un ILS de l'export : localizer (indicatif, fréquence), pente de descente (en degrés), catégorie, et le seuil et l'axe de la piste
    /// lus dans les pistes. Un ILS dont le seuil est inconnu ou hors de la zone retenue est ignoré.
    /// </summary>
    private static IlsApproach? ReadIls(XElement e, Dictionary<(string Airport, string Runway), (double Lat, double Lon, double True, double? Magnetic)> thresholds, GeoArea? area)
    {
        var uid = e.Element("IlsUid")?.Element("RdnUid");
        string? airport = uid?.Element("RwyUid")?.Element("AhpUid")?.Element("codeId")?.Value;
        string? runway = uid?.Element("txtDesig")?.Value?.Trim();
        var localizer = e.Element("Ilz");
        if (airport is null || runway is null || localizer is null || !thresholds.TryGetValue((airport, runway), out var threshold)) return null;
        if (!TryDouble(localizer.Element("valFreq")?.Value, out double frequency)) return null;
        if (area is not null && !area.Contains(threshold.Lat, threshold.Lon)) return null;

        var glide = e.Element("Igp");
        double? slope = TryDouble(glide?.Element("valSlope")?.Value, out double s) ? s : null;
        double? reference = TryDouble(glide?.Element("valRdh")?.Value, out double r) ? (glide?.Element("uomRdh")?.Value == "FT" ? r / 3.28084 : r) : null;
        double? elevation = TryDouble(glide?.Element("valElev")?.Value, out double el) && glide?.Element("uomDistVer")?.Value == "FT" ? el : null;

        return new IlsApproach(airport, runway, e.Element("codeCat")?.Value ?? "", localizer.Element("codeId")?.Value ?? "", frequency, slope,
            threshold.Lat, threshold.Lon, threshold.True, threshold.Magnetic, elevation, reference);
    }

    private static string? EndpointIdent(XElement e) => e.Element("codeId")?.Value;

    private static FixKind KindOf(XElement e) => e.Name.LocalName switch
    {
        var n when n.StartsWith("Vor", StringComparison.Ordinal) => FixKind.Vor,
        var n when n.StartsWith("Ndb", StringComparison.Ordinal) => FixKind.Ndb,
        var n when n.StartsWith("Dme", StringComparison.Ordinal) => FixKind.Dme,
        _ => FixKind.Waypoint,
    };

    private static double? Lat(XElement? e) => e is null ? null : ParseCoordinate(e.Element("geoLat")?.Value, 2);
    private static double? Lon(XElement? e) => e is null ? null : ParseCoordinate(e.Element("geoLong")?.Value, 3);

    /// <summary>Limite verticale d'un tronçon en niveau de vol (une altitude en pieds est ramenée à FL = pieds / 100).</summary>
    private static int? FlightLevel(XElement segment, string side)
    {
        string? value = segment.Element("valDistVer" + side)?.Value;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
        return segment.Element("uomDistVer" + side)?.Value == "FT" ? n / 100 : n;
    }

    /// <summary>
    /// Lit « 480133.00N », « 0035450.00E », « 163353N » (degrés, minutes, secondes)
    /// ou « 47.46654728N », « 007.66545889E » (degrés décimaux, utilisés pour certains points).
    /// </summary>
    public static double? ParseCoordinate(string? text, int degreeDigits)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return null;
        text = text.Trim();
        char hemisphere = char.ToUpperInvariant(text[^1]);
        if (hemisphere is not ('N' or 'S' or 'E' or 'W')) return null;
        string number = text[..^1];

        int dot = number.IndexOf('.');
        string integer = dot < 0 ? number : number[..dot];
        double value;
        if (integer.Length == degreeDigits)
        {
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return null;
        }
        else if (integer.Length == degreeDigits + 4) // DDMMSS[.ss]
        {
            if (!int.TryParse(integer[..degreeDigits], out int d) || !int.TryParse(integer.AsSpan(degreeDigits, 2), out int m)
                || !double.TryParse(number[(degreeDigits + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return null;
            value = d + m / 60.0 + s / 3600.0;
        }
        else if (integer.Length == degreeDigits + 2) // DDMM[.mm]
        {
            if (!int.TryParse(integer[..degreeDigits], out int d)
                || !double.TryParse(number[degreeDigits..], NumberStyles.Float, CultureInfo.InvariantCulture, out double m)) return null;
            value = d + m / 60.0;
        }
        else return null;

        return hemisphere is 'S' or 'W' ? -value : value;
    }

    /// <summary>
    /// Numéro AIRAC « aann » d'une date de mise en vigueur. Les cycles durent 28 jours à partir du 22 janvier 2026 (cycle 2601) ;
    /// le premier cycle d'une année est le premier à débuter à partir du 1er janvier.
    /// </summary>
    public static string AiracCycleOf(DateTime effective)
    {
        int year = effective.Year;
        if (effective.Date < FirstCycleOf(year)) year--;
        int number = (int)((effective.Date - FirstCycleOf(year)).TotalDays / 28) + 1;
        return $"{year % 100:00}{number:00}";
    }

    private static DateTime FirstCycleOf(int year)
    {
        var jan1 = new DateTime(year, 1, 1);
        int offset = (int)(jan1 - new DateTime(2026, 1, 22)).TotalDays;
        int mod = ((offset % 28) + 28) % 28;
        return mod == 0 ? jan1 : jan1.AddDays(28 - mod);
    }
}
