namespace VFRNav.Core.Models;

/// <summary>Filtres d'affichage des terrains sur la carte.</summary>
/// <param name="WithCode">Terrains avec un code (LFPL, LF3126...).</param>
/// <param name="WithoutCode">Terrains sans code, repérés par un identifiant local de la forme FR-0123.</param>
/// <param name="VacOnly">Seulement les terrains dont la carte VAC est disponible.</param>
public sealed record AirportFilter(
    bool WithCode = true,
    bool WithoutCode = true,
    bool VacOnly = false,
    bool Large = true,
    bool Medium = true,
    bool Small = true)
{
    public static bool IsLocalCode(Airport a) => a.Ident.Contains('-');

    public bool Matches(Airport a)
    {
        if (IsLocalCode(a) ? !WithoutCode : !WithCode) return false;
        if (VacOnly && !a.HasVac) return false;
        return a.Type switch
        {
            "large_airport" => Large,
            "medium_airport" => Medium,
            _ => Small
        };
    }
}
