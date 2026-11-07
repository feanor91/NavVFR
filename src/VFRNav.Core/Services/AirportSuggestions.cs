namespace VFRNav.Core.Services;

/// <summary>Codes OACI proches d'une saisie probablement fautive (« EGGL » pour « EGLL »).</summary>
public static class AirportSuggestions
{
    /// <summary>
    /// Codes connus qui ne diffèrent de <paramref name="code"/> que d'une lettre (remplacée, ajoutée ou retirée) ou de deux lettres voisines inversées.
    /// Le code lui-même est exclu ; au plus <paramref name="max"/> résultats, les plus « lourds » d'abord selon <paramref name="weight"/>
    /// (par exemple le nombre de procédures : un grand aéroport avant une piste d'herbe), puis par ordre alphabétique.
    /// </summary>
    public static IReadOnlyList<string> Similar(string code, IEnumerable<string> known, int max = 3, Func<string, int>? weight = null)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length < 3) return [];
        return [.. known
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => k.Length == 4 && k != code && IsOneEditAway(code, k))
            .Distinct()
            .OrderByDescending(k => weight?.Invoke(k) ?? 0)
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(max)];
    }

    private static bool IsOneEditAway(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length == b.Length)
        {
            int first = -1, count = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) { if (first < 0) first = i; count++; }
            if (count == 1) return true;
            return count == 2 && first + 1 < a.Length && a[first] == b[first + 1] && a[first + 1] == b[first];   // deux lettres voisines inversées
        }
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        int s = 0, l = 0, skipped = 0;
        while (s < shorter.Length && l < longer.Length)
        {
            if (shorter[s] == longer[l]) { s++; l++; continue; }
            if (++skipped > 1) return false;
            l++;
        }
        return true;
    }
}
