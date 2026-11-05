using System.Globalization;
using VFRNav.Core.Models;

namespace VFRNav.Core.Weather;

/// <summary>Heure de départ saisie (UTC) et heures de passage aux points de la route.</summary>
public static class DepartureTime
{
    private static readonly string[] FullFormats =
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HHmm", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HHmm", "dd-MM-yyyy HH:mm"];

    private static readonly string[] NoYearFormats = ["dd/MM HH:mm", "dd/MM HHmm", "dd-MM HH:mm"];
    private static readonly string[] TimeOnlyFormats = ["HH:mm", "HHmm", "H:mm", "HH'h'mm"];

    /// <summary>
    /// Lit une heure UTC : « 2026-11-05 14:30 », « 05/11/2026 14:30 », « 05/11 14:30 » (année courante) ou « 14:30 » / « 1430 » (jour courant).
    /// Une saisie vide ou illisible donne faux.
    /// </summary>
    public static bool TryParse(string? text, DateTimeOffset now, out DateTimeOffset result)
    {
        result = default;
        text = text?.Trim().Replace("Z", "", StringComparison.OrdinalIgnoreCase).Replace("UTC", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrEmpty(text)) return false;
        var style = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        var inv = CultureInfo.InvariantCulture;

        if (DateTime.TryParseExact(text, FullFormats, inv, style, out var full)) { result = new DateTimeOffset(full, TimeSpan.Zero); return true; }
        if (DateTime.TryParseExact(text, NoYearFormats, inv, style, out var noYear))
        {
            result = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, noYear.Month, noYear.Day, noYear.Hour, noYear.Minute, 0, DateTimeKind.Utc));
            return true;
        }
        if (DateTime.TryParseExact(text, TimeOnlyFormats, inv, style, out var timeOnly))
        {
            var day = now.UtcDateTime.Date;
            result = new DateTimeOffset(DateTime.SpecifyKind(day + timeOnly.TimeOfDay, DateTimeKind.Utc));
            return true;
        }
        return false;
    }

    public static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Heure de passage à chaque point de la route (le premier : le départ), d'après le temps de chaque tronçon.</summary>
    public static IReadOnlyList<DateTimeOffset> Etas(DateTimeOffset departure, NavLog log)
    {
        var result = new List<DateTimeOffset> { departure };
        double minutes = 0;
        foreach (var leg in log.Legs)
        {
            minutes += double.IsFinite(leg.Minutes) ? leg.Minutes : 0;
            result.Add(departure.AddMinutes(minutes));
        }
        return result;
    }

    /// <summary>« 14:35 » pour un instant UTC.</summary>
    public static string ClockText(DateTimeOffset time) => time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
}
