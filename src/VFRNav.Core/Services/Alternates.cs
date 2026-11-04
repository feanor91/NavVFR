using System.Globalization;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

/// <summary>Un terrain de dégagement (détournement) vu depuis le dernier point de la route.</summary>
/// <param name="Known">Faux si le code n'est pas dans la base de terrains : aucun calcul possible.</param>
public sealed record AlternateInfo(string Icao, string Name, bool Known, double DistanceNm, int TrueCourse, int MagneticHeading, string Time, double FuelL)
{
    public string Line
    {
        get
        {
            var fr = VFRNav.Core.Localization.Loc.FormatCulture;
            return Known
                ? Loc.F(fr, "Alternates_Line", Icao, Name, DistanceNm.ToString("0", fr), TrueCourse, MagneticHeading, Time, FuelL.ToString("0.#", fr))
                : Loc.F("Alternates_terrain_inconnu_verifie_le_code", Icao);
        }
    }
}

/// <summary>Terrains de dégagement saisis par le pilote : lecture de la saisie, distance, cap, temps et carburant depuis l'arrivée.</summary>
public static class Alternates
{
    /// <summary>Codes saisis (séparés par des espaces, virgules, points-virgules ou barres), en majuscules, sans doublon, dans l'ordre.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        [.. (text ?? "").Split([' ', ',', ';', '/', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .Distinct()];

    /// <summary>Distance, cap, temps et carburant de <paramref name="from"/> (le terrain d'arrivée) à chaque terrain de dégagement, avec le vent et la vitesse de <paramref name="p"/>.</summary>
    public static IReadOnlyList<AlternateInfo> For(Waypoint from, IReadOnlyList<string> codes, IReadOnlyList<Airport> airports, FlightParameters p)
    {
        var result = new List<AlternateInfo>();
        foreach (string code in codes)
        {
            var airport = airports.FirstOrDefault(a => string.Equals(a.Ident, code, StringComparison.OrdinalIgnoreCase));
            if (airport is null) { result.Add(new AlternateInfo(code, "", false, 0, 0, 0, "", 0)); continue; }
            var leg = NavMath.ComputeLog([from, airport.ToWaypoint()], p).Legs[0];
            result.Add(new AlternateInfo(airport.Ident, airport.Name, true, leg.DistanceNm, (int)Math.Round(leg.TrueCourse), (int)Math.Round(leg.MagneticHeading), leg.Time, leg.FuelL));
        }
        return result;
    }
}
