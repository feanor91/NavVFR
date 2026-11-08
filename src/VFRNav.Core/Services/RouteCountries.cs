using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Pays saisis et pays traversés par une route : de quoi savoir pour quels pays charger espaces aériens et balises.</summary>
public static class RouteCountries
{
    /// <summary>
    /// Pays d'Europe (Union européenne, Royaume-Uni, Suisse, Norvège, Islande, Balkans, Moldavie, Ukraine) : ce que désigne « EUROPE » dans la saisie.
    /// La Russie, la Biélorussie et la Turquie n'y sont pas : leurs données aéronautiques sont rarement utiles ici et très lourdes.
    /// </summary>
    public static readonly IReadOnlyList<string> Europe =
    [
        "AL", "AD", "AT", "BA", "BE", "BG", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "GR", "HR", "HU", "IE", "IS", "IT",
        "LI", "LT", "LU", "LV", "MC", "MD", "ME", "MK", "MT", "NL", "NO", "PL", "PT", "RO", "RS", "SE", "SI", "SK", "SM", "UA",
    ];

    /// <summary>
    /// Codes pays ISO de deux lettres d'une saisie (« FR, gb  BE »), en majuscules, sans doublon, dans l'ordre. « EUROPE » (ou « EUROPA ») est remplacé
    /// par la liste des pays d'Europe. Le reste est ignoré.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        var codes = new List<string>();
        foreach (var raw in (text ?? "").Split([' ', ',', ';', '/', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToUpperInvariant();
            if (token is "EUROPE" or "EUROPA") codes.AddRange(Europe);
            else if (token.Length == 2 && token.All(char.IsLetter)) codes.Add(token);
        }
        return [.. codes.Distinct()];
    }

    /// <summary>
    /// Pays traversés par la route : on la parcourt tous les <paramref name="stepNm"/> milles et on prend le pays du terrain le plus proche
    /// (à moins de <paramref name="maxNm"/>). En mer, loin de tout terrain, rien n'est ajouté.
    /// </summary>
    public static IReadOnlyList<string> Along(IReadOnlyList<Waypoint> route, IReadOnlyList<Airport> airports, double stepNm = 25, double maxNm = 80)
    {
        if (route.Count == 0 || airports.Count == 0) return [];
        var grid = new Dictionary<(int, int), List<Airport>>();
        foreach (var a in airports.Where(a => !string.IsNullOrEmpty(a.Country)))
        {
            var key = Cell(a.Lat, a.Lon);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(a);
        }

        var found = new List<string>();
        var points = route.Count == 1 ? route.Select(w => (w.Lat, w.Lon)).ToList() : RouteGeometry.Sample(route, stepNm).Select(s => (s.Lat, s.Lon)).ToList();
        foreach (var (lat, lon) in points)
        {
            var (cy, cx) = Cell(lat, lon);
            int reach = (int)Math.Ceiling(maxNm / 60.0 / CellDegrees) + 1;
            string? country = null;
            double best = maxNm;
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                    if (grid.TryGetValue((y, x), out var list))
                        foreach (var a in list)
                        {
                            double nm = NavMath.DistanceNm(lat, lon, a.Lat, a.Lon);
                            if (nm <= best) { best = nm; country = a.Country; }
                        }
            if (country is not null && !found.Contains(country, StringComparer.OrdinalIgnoreCase)) found.Add(country.ToUpperInvariant());
        }
        return found;
    }

    /// <summary>Pays à charger : ceux saisis, puis ceux que traverse la route.</summary>
    public static IReadOnlyList<string> Wanted(IEnumerable<string> typed, IEnumerable<string> alongRoute) =>
        [.. typed.Concat(alongRoute).Select(c => c.ToUpperInvariant()).Distinct()];

    // Cases d'un demi-degré : une recherche dans les cases voisines couvre la portée voulue.
    private const double CellDegrees = 0.5;
    private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat / CellDegrees), (int)Math.Floor(lon / CellDegrees));
}
