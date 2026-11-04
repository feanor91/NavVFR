namespace VFRNav.Core.Models;

/// <summary>Filtres d'affichage des terrains sur la carte.</summary>
/// <param name="WithCode">Terrains avec un code (LFPL, LF3126...).</param>
/// <param name="WithoutCode">Terrains sans code, repérés par un identifiant local de la forme FR-0123.</param>
/// <param name="VacOnly">Seulement les terrains dont la carte VAC est disponible.</param>
/// <param name="ScheduledOnly">Seulement les terrains où des lignes régulières sont exploitées.</param>
/// <param name="IataOnly">Seulement les terrains qui ont un code IATA (aéroports commerciaux).</param>
/// <param name="MinRunwayFt">Longueur minimale de la piste la plus longue, en pieds ; 0 = pas de limite.</param>
/// <param name="PavedOnly">Seulement les terrains avec une piste en dur (la longueur minimale s'applique alors à la piste en dur).</param>
/// <param name="Countries">Codes ISO des pays retenus (« FR », « GB »...) ; vide ou null = tous.</param>
/// <param name="Continent">Continent retenu (EU, NA, SA, AF, AS, OC, AN) ; null = tous. Se combine avec <paramref name="Countries"/> : les deux doivent convenir.</param>
public sealed record AirportFilter(
    bool WithCode = true,
    bool WithoutCode = true,
    bool VacOnly = false,
    bool Large = true,
    bool Medium = true,
    bool Small = true,
    bool ScheduledOnly = false,
    bool IataOnly = false,
    int MinRunwayFt = 0,
    bool PavedOnly = false,
    IReadOnlyList<string>? Countries = null,
    string? Continent = null)
{
    /// <summary>Codes pays lus dans un texte libre (« fr, gb ; de »), en majuscules, sans doublon ; les mots qui ne font pas deux lettres sont ignorés.</summary>
    public static IReadOnlyList<string> ParseCountries(string? text) =>
        (text ?? "").Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length == 2 && t.All(char.IsLetter)).Select(t => t.ToUpperInvariant()).Distinct().ToList();

    public static bool IsLocalCode(Airport a) => a.Ident.Contains('-');

    public bool Matches(Airport a)
    {
        if (IsLocalCode(a) ? !WithoutCode : !WithCode) return false;
        if (VacOnly && !a.HasVac) return false;
        if (ScheduledOnly && !a.Scheduled) return false;
        if (IataOnly && string.IsNullOrEmpty(a.Iata)) return false;
        if (MinRunwayFt > 0 || PavedOnly)
        {
            int? runway = PavedOnly ? a.LongestPavedRunwayFt : a.LongestRunwayFt;
            if (runway is null || runway < MinRunwayFt) return false;
        }
        if (Countries is { Count: > 0 } && (a.Country is null || !Countries.Contains(a.Country, StringComparer.OrdinalIgnoreCase))) return false;
        if (Continent is { Length: > 0 } && !string.Equals(a.Continent, Continent, StringComparison.OrdinalIgnoreCase)) return false;
        return a.Type switch
        {
            "large_airport" => Large,
            "medium_airport" => Medium,
            _ => Small
        };
    }
}
