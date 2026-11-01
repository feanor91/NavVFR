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
