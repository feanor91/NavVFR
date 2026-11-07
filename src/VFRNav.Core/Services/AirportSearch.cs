using System.Globalization;
using System.Text;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>
/// Recherche d'un terrain par code OACI ou local, code IATA, nom ou commune, sans tenir compte des majuscules ni des accents.
/// L'index est construit une fois (48 000 terrains) : chaque frappe ne fait ensuite que comparer des textes déjà préparés.
/// </summary>
public sealed class AirportSearchIndex
{
    private readonly record struct Entry(Airport Airport, string Ident, string Alias, string Iata, string Text);

    private readonly List<Entry> _entries;

    public AirportSearchIndex(IEnumerable<Airport> airports) =>
        _entries = airports.Select(a => new Entry(a, a.Ident.ToUpperInvariant(), (a.SimIdent ?? "").ToUpperInvariant(), (a.Iata ?? "").ToUpperInvariant(), Fold($"{a.Name} {a.City}").Replace('-', ' '))).ToList();

    public int Count => _entries.Count;

    /// <summary>
    /// Meilleurs résultats : code exact, puis code qui commence par la saisie, puis nom ou commune qui commence par elle, puis qui la contient.
    /// À égalité, le terrain le plus important d'abord. Il faut au moins deux caractères, sauf pour un code exact.
    /// </summary>
    public IReadOnlyList<Airport> Find(string query, int max = 8)
    {
        string code = query.Trim().ToUpperInvariant();
        string text = Fold(query.Trim()).Replace('-', ' ');
        if (code.Length == 0 || (code.Length < 2 && !_entries.Any(e => e.Ident == code || e.Alias == code || e.Iata == code))) return [];

        var scored = new List<(int Score, Airport Airport)>();
        foreach (var e in _entries)
        {
            int score = e.Ident == code || e.Alias == code || e.Iata == code ? 0
                : e.Ident.StartsWith(code, StringComparison.Ordinal) || (code.Length >= 2 && (e.Iata.StartsWith(code, StringComparison.Ordinal) || e.Alias.StartsWith(code, StringComparison.Ordinal))) ? 1
                : e.Text.StartsWith(text, StringComparison.Ordinal) || e.Text.Contains(' ' + text, StringComparison.Ordinal) ? 2
                : text.Length >= 3 && e.Text.Contains(text, StringComparison.Ordinal) ? 3
                : -1;
            if (score >= 0) scored.Add((score, e.Airport));
        }
        return scored.OrderBy(s => s.Score).ThenBy(s => AirportDisplay.Rank(s.Airport)).ThenBy(s => s.Airport.Name, StringComparer.Ordinal)
            .Take(max).Select(s => s.Airport).ToList();
    }

    /// <summary>Minuscules sans accents : « Côte d'Azur » devient « cote d'azur ».</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
