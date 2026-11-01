namespace VFRNav.Core.Models;

/// <summary>Famille d'espace aérien, qui décide du style de tracé et du filtre.</summary>
public enum AirspaceKind { Other, Restricted, Ctr, Tma, Radio, Leisure, Sector }

public sealed record AirspaceFrequency(string Name, string Value);

/// <summary>Informations complémentaires affichées dans l'infobulle.</summary>
/// <param name="Flags">Conditions d'accès : sur NOTAM, sur demande, accord spécial...</param>
public sealed record AirspaceDetails(string? Remarks = null, IReadOnlyList<string>? Flags = null, IReadOnlyList<AirspaceFrequency>? Frequencies = null);

/// <summary>Espace aérien : anneaux extérieurs en (lon, lat), limites en texte et en pieds.</summary>
/// <param name="LowerFt">Plancher en pieds (le sol compte pour 0, les altitudes AGL sont prises telles quelles).</param>
/// <param name="UpperFt">Plafond en pieds.</param>
/// <param name="TypeCode">Champ <c>type</c> d'OpenAIP, pour le libellé de l'infobulle.</param>
public sealed record Airspace(string Name, string IcaoClass, string Lower, string Upper,
    IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> Rings, AirspaceKind Kind = AirspaceKind.Other,
    double LowerFt = 0, double UpperFt = double.MaxValue, int TypeCode = 0, AirspaceDetails? Details = null)
{
    /// <summary>Texte porté le long du contour : désignation puis limites.</summary>
    public string Label => $"{Name}   {Lower} - {Upper}";

    /// <summary>Correspondance avec le champ <c>type</c> d'OpenAIP (table de la documentation de l'API).</summary>
    public static AirspaceKind KindFromOpenAipType(int type) => type switch
    {
        1 or 2 or 3 or 8 or 9 or 17 or 18 or 19 or 29 or 31 => AirspaceKind.Restricted,
        4 or 36 => AirspaceKind.Ctr,
        7 or 24 or 25 or 26 or 34 or 35 => AirspaceKind.Tma,
        5 or 6 or 13 or 14 or 20 or 22 or 23 => AirspaceKind.Radio,
        21 or 28 => AirspaceKind.Leisure,
        10 or 11 or 12 or 15 or 16 or 27 or 30 or 32 or 33 => AirspaceKind.Sector,
        _ => AirspaceKind.Other
    };

    public static string TypeName(int type) => type switch
    {
        1 => "Zone réglementée (R)",
        2 => "Zone dangereuse (D)",
        3 => "Zone interdite (P)",
        4 => "CTR",
        5 => "TMZ",
        6 => "RMZ",
        7 => "TMA",
        8 => "TRA",
        9 => "TSA",
        10 => "FIR",
        11 => "UIR",
        12 => "ADIZ",
        13 => "ATZ",
        14 => "MATZ",
        15 => "Voie aérienne",
        16 => "MTR",
        17 => "Zone d'alerte",
        18 => "Zone d'avertissement",
        19 => "Zone protégée",
        20 => "HTZ",
        21 => "Secteur planeur",
        22 => "TRP",
        23 => "TIZ",
        24 => "TIA",
        25 => "MTA",
        26 => "CTA",
        27 => "Secteur ACC",
        28 => "Activité aérienne sportive",
        29 => "Restriction de survol à basse altitude",
        30 => "MRT",
        31 => "TFR",
        32 => "Secteur VFR",
        33 => "Secteur FIS",
        34 => "LTA",
        35 => "UTA",
        36 => "MCTR",
        _ => "Espace aérien"
    };

    /// <summary>
    /// Limite OpenAIP : unité 0 = mètres, 1 = pieds, 6 = niveau de vol ; référence 0 = sol, 1 = niveau de la mer, 2 = standard.
    /// </summary>
    public static (string Text, double Feet) FormatLimit(int value, int unit, int referenceDatum)
    {
        if (unit == 6 || referenceDatum == 2) return ($"FL{value}", value * 100.0);
        if (value == 0 && referenceDatum == 0) return ("SFC", 0);
        double feet = unit == 0 ? value * 3.28084 : value;
        string suffix = referenceDatum == 0 ? " GND" : " AMSL";
        return (unit == 0 ? $"{value}m{suffix}" : $"{value}ft{suffix}", feet);
    }

    /// <summary>Vrai si le point est dans l'un des contours (les trous éventuels sont ignorés).</summary>
    public bool Contains(double lon, double lat)
    {
        foreach (var ring in Rings)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (xi, yi) = (ring[i].Lon, ring[i].Lat);
                var (xj, yj) = (ring[j].Lon, ring[j].Lat);
                if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }

    /// <summary>
    /// Distance, en mètres Web Mercator (l'unité de la carte), entre le point et le contour de la zone le plus proche.
    /// Sert à savoir quelle zone dessine le trait que l'on survole.
    /// </summary>
    public double EdgeDistanceMercator(double lon, double lat)
    {
        var (px, py) = Mercator(lon, lat);
        double best = double.MaxValue;
        foreach (var ring in Rings)
        {
            if (ring.Count < 2) continue;
            var (ax, ay) = Mercator(ring[^1].Lon, ring[^1].Lat);
            foreach (var (blon, blat) in ring)
            {
                var (bx, by) = Mercator(blon, blat);
                best = Math.Min(best, SegmentDistance(px, py, ax, ay, bx, by));
                (ax, ay) = (bx, by);
            }
        }
        return best;
    }

    private static (double X, double Y) Mercator(double lon, double lat)
    {
        const double R = 6378137.0;
        double clamped = Math.Clamp(lat, -85.05, 85.05) * Math.PI / 180;
        return (R * lon * Math.PI / 180, R * Math.Log(Math.Tan(Math.PI / 4 + clamped / 2)));
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(px - (ax + t * dx), 2) + Math.Pow(py - (ay + t * dy), 2));
    }

    /// <summary>Lignes de l'infobulle, sans le titre (le nom).</summary>
    public IReadOnlyList<string> DescribeBody()
    {
        var lines = new List<string>();
        string type = TypeName(TypeCode);
        lines.Add(IcaoClass.Length > 0 ? $"{type} · classe {IcaoClass}" : type);
        lines.Add($"Plafond : {(Upper.Length > 0 ? Upper : "?")}    Plancher : {(Lower.Length > 0 ? Lower : "?")}");
        if (Details?.Flags is { Count: > 0 } flags) lines.Add(string.Join(" · ", flags));
        if (Details?.Remarks is { Length: > 0 } remarks) lines.Add(remarks.Trim());
        if (Details?.Frequencies is { Count: > 0 } freqs)
            lines.Add("Fréquences : " + string.Join("   ", freqs.Select(f => f.Name.Length > 0 ? $"{f.Name} {f.Value}" : f.Value)));
        return lines;
    }
}

/// <summary>Quelles familles d'espaces aériens afficher, et jusqu'à quel plancher.</summary>
/// <param name="MaxFloorFt">Masque les zones dont le plancher est plus haut que cette valeur ; null = pas de limite.</param>
public sealed record AirspaceFilter(
    bool Restricted = true,
    bool Controlled = true,
    bool Radio = true,
    bool Leisure = true,
    bool Sectors = false,
    double? MaxFloorFt = 11500)
{
    public bool Matches(Airspace a)
    {
        if (MaxFloorFt is { } max && a.LowerFt > max) return false;
        return a.Kind switch
        {
            AirspaceKind.Restricted => Restricted,
            AirspaceKind.Ctr or AirspaceKind.Tma => Controlled,
            AirspaceKind.Radio => Radio,
            AirspaceKind.Leisure => Leisure,
            _ => Sectors
        };
    }
}
