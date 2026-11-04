using System.Globalization;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public sealed class OurAirportsService(HttpClient http)
{
    public const string Url = "https://davidmegginson.github.io/ourairports-data/airports.csv";
    private static readonly HashSet<string> KeptTypes = ["large_airport", "medium_airport", "small_airport"];

    public async Task<IReadOnlyList<Airport>> LoadAsync(string country, CancellationToken ct = default)
    {
        await using var stream = await http.GetStreamAsync(Url, ct);
        using var reader = new StreamReader(stream);
        return Parse(reader, country);
    }

    public const string NavaidsUrl = "https://davidmegginson.github.io/ourairports-data/navaids.csv";

    public async Task<IReadOnlyList<Navaid>> LoadNavaidsAsync(string country, CancellationToken ct = default)
    {
        await using var stream = await http.GetStreamAsync(NavaidsUrl, ct);
        using var reader = new StreamReader(stream);
        return ParseNavaids(reader, country);
    }

    /// <summary>VOR et NDB du pays (les DME et TACAN seuls sont ignorés).</summary>
    public static IReadOnlyList<Navaid> ParseNavaids(TextReader reader, string country)
    {
        var result = new List<Navaid>();
        string[]? header = null;
        int iIdent = 0, iName = 0, iType = 0, iFreq = 0, iLat = 0, iLon = 0, iElev = 0, iCountry = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in CsvParser.Parse(reader))
        {
            if (header is null)
            {
                header = r;
                iIdent = Array.IndexOf(header, "ident");
                iName = Array.IndexOf(header, "name");
                iType = Array.IndexOf(header, "type");
                iFreq = Array.IndexOf(header, "frequency_khz");
                iLat = Array.IndexOf(header, "latitude_deg");
                iLon = Array.IndexOf(header, "longitude_deg");
                iElev = Array.IndexOf(header, "elevation_ft");
                iCountry = Array.IndexOf(header, "iso_country");
                continue;
            }
            if (r.Length <= iCountry || !string.Equals(r[iCountry], country, StringComparison.OrdinalIgnoreCase)) continue;
            NavaidKind? kind = r[iType] switch
            {
                "VOR" or "VOR-DME" or "VORTAC" => NavaidKind.Vor,
                "NDB" or "NDB-DME" => NavaidKind.Ndb,
                _ => null
            };
            if (kind is null) continue;
            if (!double.TryParse(r[iLat], NumberStyles.Float, inv, out var lat) ||
                !double.TryParse(r[iLon], NumberStyles.Float, inv, out var lon) ||
                !double.TryParse(r[iFreq], NumberStyles.Float, inv, out var freq)) continue;
            double.TryParse(r[iElev], NumberStyles.Float, inv, out var elev);
            result.Add(new Navaid(r[iIdent], r[iName], kind.Value, r[iType], freq, lat, lon, elev));
        }
        return result;
    }

    public const string RunwaysUrl = "https://davidmegginson.github.io/ourairports-data/runways.csv";

    /// <summary>
    /// Tous les terrains pour avions du monde (grands, moyens, petits : ni héliports, ni hydrobases, ni terrains fermés), avec leur code IATA,
    /// leur commune, leur pays, leur continent et le service régulier. Les pistes viennent de <see cref="ParseRunways"/>.
    /// </summary>
    public static IReadOnlyList<Airport> ParseWorld(TextReader reader)
    {
        var result = new List<Airport>(50_000);
        string[]? header = null;
        int iIdent = 0, iType = 0, iName = 0, iLat = 0, iLon = 0, iElev = 0, iCountry = 0, iContinent = 0, iCity = 0, iService = 0, iIata = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in CsvParser.Parse(reader))
        {
            if (header is null)
            {
                header = r;
                iIdent = Array.IndexOf(header, "ident");
                iType = Array.IndexOf(header, "type");
                iName = Array.IndexOf(header, "name");
                iLat = Array.IndexOf(header, "latitude_deg");
                iLon = Array.IndexOf(header, "longitude_deg");
                iElev = Array.IndexOf(header, "elevation_ft");
                iCountry = Array.IndexOf(header, "iso_country");
                iContinent = Array.IndexOf(header, "continent");
                iCity = Array.IndexOf(header, "municipality");
                iService = Array.IndexOf(header, "scheduled_service");
                iIata = Array.IndexOf(header, "iata_code");
                continue;
            }
            if (r.Length <= Math.Max(iCountry, Math.Max(iIata, iService)) || !KeptTypes.Contains(r[iType])) continue;
            if (!double.TryParse(r[iLat], NumberStyles.Float, inv, out var lat) ||
                !double.TryParse(r[iLon], NumberStyles.Float, inv, out var lon)) continue;
            double.TryParse(r[iElev], NumberStyles.Float, inv, out var elev);
            result.Add(new Airport(r[iIdent], r[iName], r[iType], lat, lon, elev, false,
                Iata: r[iIata].Length > 0 ? r[iIata] : null, City: r[iCity].Length > 0 ? r[iCity] : null,
                Country: r[iCountry].ToUpperInvariant(), Continent: r[iContinent].ToUpperInvariant(), Scheduled: r[iService] == "yes"));
        }
        return result;
    }

    /// <summary>Piste la plus longue de chaque terrain (hors pistes fermées), et la plus longue des pistes en dur.</summary>
    public static IReadOnlyDictionary<string, (int LongestFt, int? LongestPavedFt)> ParseRunways(TextReader reader)
    {
        var map = new Dictionary<string, (int, int?)>(StringComparer.OrdinalIgnoreCase);
        string[]? header = null;
        int iAirport = 0, iLength = 0, iSurface = 0, iClosed = 0;
        foreach (var r in CsvParser.Parse(reader))
        {
            if (header is null)
            {
                header = r;
                iAirport = Array.IndexOf(header, "airport_ident");
                iLength = Array.IndexOf(header, "length_ft");
                iSurface = Array.IndexOf(header, "surface");
                iClosed = Array.IndexOf(header, "closed");
                continue;
            }
            if (r.Length <= Math.Max(iClosed, iSurface) || r[iClosed] == "1" || !int.TryParse(r[iLength], out int length) || length <= 0) continue;
            map.TryGetValue(r[iAirport], out var known);
            int longest = Math.Max(known.Item1, length);
            int? paved = IsPaved(r[iSurface]) ? Math.Max(known.Item2 ?? 0, length) : known.Item2;
            map[r[iAirport]] = (longest, paved);
        }
        return map;
    }

    /// <summary>Revêtement en dur d'après le code d'OurAirports (« ASP », « CON », « ASPH-G », « BIT », « PEM »...).</summary>
    public static bool IsPaved(string surface)
    {
        string s = surface.ToUpperInvariant();
        return s.Contains("ASP") || s.Contains("CON") || s.Contains("BIT") || s.Contains("PEM") || s.Contains("TAR") || s.Contains("MAC") || s.Contains("PAV");
    }

    /// <summary>Ajoute à chaque terrain la longueur de sa piste la plus longue et de sa piste en dur la plus longue.</summary>
    public static IReadOnlyList<Airport> WithRunways(IReadOnlyList<Airport> airports, IReadOnlyDictionary<string, (int LongestFt, int? LongestPavedFt)> runways)
    {
        var result = new List<Airport>(airports.Count);
        foreach (var a in airports)
            result.Add(runways.TryGetValue(a.Ident, out var r) ? a with { LongestRunwayFt = r.LongestFt, LongestPavedRunwayFt = r.LongestPavedFt } : a);
        return result;
    }

    public static IReadOnlyList<Airport> Parse(TextReader reader, string country)
    {
        var result = new List<Airport>();
        string[]? header = null;
        int iIdent = 0, iType = 0, iName = 0, iLat = 0, iLon = 0, iElev = 0, iCountry = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in CsvParser.Parse(reader))
        {
            if (header is null)
            {
                header = r;
                iIdent = Array.IndexOf(header, "ident");
                iType = Array.IndexOf(header, "type");
                iName = Array.IndexOf(header, "name");
                iLat = Array.IndexOf(header, "latitude_deg");
                iLon = Array.IndexOf(header, "longitude_deg");
                iElev = Array.IndexOf(header, "elevation_ft");
                iCountry = Array.IndexOf(header, "iso_country");
                continue;
            }
            if (r.Length <= iCountry || !string.Equals(r[iCountry], country, StringComparison.OrdinalIgnoreCase) || !KeptTypes.Contains(r[iType])) continue;
            if (!double.TryParse(r[iLat], NumberStyles.Float, inv, out var lat) ||
                !double.TryParse(r[iLon], NumberStyles.Float, inv, out var lon)) continue;
            double.TryParse(r[iElev], NumberStyles.Float, inv, out var elev);
            result.Add(new Airport(r[iIdent], r[iName], r[iType], lat, lon, elev));
        }
        return result;
    }
}
