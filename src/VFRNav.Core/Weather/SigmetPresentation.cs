using System.Globalization;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Weather;

/// <summary>Un phénomène dans la légende : code SIGMET, libellé et couleur.</summary>
public sealed record SigmetLegendEntry(string Code, string Label, string Hex);

/// <summary>
/// Tout ce qu'il faut pour montrer les SIGMET sur la carte : couleur par phénomène, textes (niveaux, validité, infobulle),
/// sélection des messages à afficher et tracé des polygones.
/// </summary>
public static class SigmetPresentation
{
    private static CultureInfo Fr => VFRNav.Core.Localization.Loc.FormatCulture;
    private const string OtherHex = "#757575";

    /// <summary>
    /// Couleur d'un phénomène. Choisies pour ne pas se confondre avec le radar (bleus, jaune, orange, rouge) ni avec les zones
    /// aériennes (rouge, bleu, violet clair, orange) : framboise, brun, cyan, vert, gris sombre, pourpre, ocre, indigo.
    /// </summary>
    public static string HazardColorHex(string code) => code.ToUpperInvariant() switch
    {
        "TS" => "#C2185B",
        "TURB" => "#8D5524",
        "ICE" => "#0097A7",
        "MTW" => "#2E7D32",
        "VA" => "#455A64",
        "TC" => "#7B1FA2",
        "DS" or "SS" => "#B8860B",
        "GR" => "#3949AB",
        _ => OtherHex,
    };

    public static IReadOnlyList<SigmetLegendEntry> Legend { get; } =
    [
        Entry("TS", Loc.Get("Sigmet_Orages")),
        Entry("TURB", Loc.Get("Sigmet_Turbulence")),
        Entry("ICE", Loc.Get("Sigmet_Givrage")),
        Entry("MTW", Loc.Get("Sigmet_Ondes_orographiques")),
        Entry("VA", Loc.Get("Sigmet_Cendres_volcaniques")),
        Entry("TC", Loc.Get("SigmetPresentation_Cyclone")),
        Entry("", Loc.Get("SigmetPresentation_Autre")),
    ];

    private static SigmetLegendEntry Entry(string code, string label) => new(code, label, HazardColorHex(code));

    // ---------- textes ----------

    /// <summary>Niveau en clair : « FL350 », « SFC » pour le sol, <paramref name="missing"/> quand la limite n'existe pas.</summary>
    public static string LevelText(double? feet, string missing) =>
        feet is not { } ft ? missing : ft <= 0 ? "SFC" : "FL" + Math.Round(ft / 100).ToString("000", Fr);

    /// <summary>Tranche d'altitude du message, ex. « SFC → FL350 ».</summary>
    public static string LevelRange(Sigmet s) => $"{LevelText(s.BaseFt, "SFC")} → {LevelText(s.TopFt, Loc.Get("FlightSheetBuilder_illimite"))}";

    /// <summary>Étiquette courte posée sur la zone, ex. « TS FL350 » ou « TURB FL150-FL280 ».</summary>
    public static string LabelText(Sigmet s)
    {
        string hazard = s.Hazard.ToUpperInvariant();
        if (s.TopFt is null) return hazard;
        string top = LevelText(s.TopFt, "");
        return s.BaseFt is > 0 ? $"{hazard} {LevelText(s.BaseFt, "")}-{top}" : $"{hazard} {top}";
    }

    /// <summary>Validité en UTC et temps restant, ex. « 05:00 → 07:00 UTC, reste 1 h 36 ».</summary>
    public static string ValidityText(Sigmet s, DateTimeOffset now)
    {
        string Time(DateTimeOffset t) => t.UtcDateTime.Date == now.UtcDateTime.Date ? t.UtcDateTime.ToString("HH:mm", Fr) : t.UtcDateTime.ToString("dd/MM HH:mm", Fr);
        string range = $"{Time(s.ValidFrom)} → {Time(s.ValidTo)} UTC";
        if (now < s.ValidFrom) return Loc.F("SigmetPresentation_commence_dans", range, Duration(s.ValidFrom - now));
        if (now < s.ValidTo) return Loc.F("SigmetPresentation_reste", range, Duration(s.ValidTo - now));
        return range + Loc.Get("SigmetPresentation_expire");
    }

    private static string Duration(TimeSpan span)
    {
        int minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        return minutes < 60 ? Loc.F("SigmetPresentation_min", minutes) : $"{minutes / 60} h {minutes % 60:00}";
    }

    /// <summary>Texte du message sur une seule ligne, raccourci pour tenir dans une infobulle ou sur la feuille.</summary>
    public static string RawOneLine(string raw, int maxLength = 300)
    {
        string text = string.Join(' ', raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    /// <summary>Titre et lignes de l'infobulle d'un SIGMET.</summary>
    public static (string Title, IReadOnlyList<string> Lines) TipText(Sigmet s, DateTimeOffset now)
    {
        string fir = s.FirName.Trim().Length > 0 ? s.FirName.Trim() : s.FirId;
        var lines = new List<string>
        {
            Loc.F("SigmetPresentation_serie", fir, s.SeriesId),
            Loc.Get("SigmetPresentation_Niveaux") + LevelRange(s),
            Loc.Get("SigmetPresentation_Validite") + ValidityText(s, now),
        };
        if (s.RawText is { Length: > 0 } raw) lines.Add(RawOneLine(raw));
        return (s.Description, lines);
    }

    // ---------- sélection ----------

    /// <summary>
    /// SIGMET à afficher : valides à cet instant et, si <paramref name="altitudeFt"/> est donnée, dont la tranche d'altitude contient cette altitude.
    /// </summary>
    public static IReadOnlyList<Sigmet> Select(IEnumerable<Sigmet> all, DateTimeOffset now, double? altitudeFt = null) =>
        all.Where(s => s.IsValidAt(now) && (altitudeFt is not { } alt || (alt >= (s.BaseFt ?? 0) && alt <= (s.TopFt ?? double.MaxValue)))).ToList();

    /// <summary>SIGMET dont une zone contient le point (pour l'infobulle au survol).</summary>
    public static IReadOnlyList<Sigmet> At(IEnumerable<Sigmet> sigmets, double lat, double lon) =>
        sigmets.Where(s => s.Areas.Any(area => SigmetAnalysis.Contains(area, lat, lon))).ToList();

    // ---------- tracé ----------

    /// <summary>
    /// Contour prêt à dessiner : longitudes continues (une zone à cheval sur l'antiméridien ne devient pas un ruban autour du monde),
    /// ramenées dans [-180, 180] d'après le centre, et contour fermé.
    /// </summary>
    public static IReadOnlyList<(double Lat, double Lon)> DrawRing(IReadOnlyList<(double Lat, double Lon)> ring)
    {
        if (ring.Count == 0) return ring;
        var points = new List<(double Lat, double Lon)> { ring[0] };
        double previous = ring[0].Lon;
        for (int i = 1; i < ring.Count; i++)
        {
            double lon = ring[i].Lon;
            while (lon - previous > 180) lon -= 360;
            while (lon - previous < -180) lon += 360;
            points.Add((ring[i].Lat, lon));
            previous = lon;
        }

        double shift = Math.Floor((points.Average(p => p.Lon) + 180) / 360) * 360;   // centre ramené dans [-180, 180[
        if (shift != 0) points = points.Select(p => (p.Lat, p.Lon - shift)).ToList();
        if (points[0] != points[^1]) points.Add(points[0]);
        return points;
    }

    /// <summary>Point où poser l'étiquette : moyenne des sommets du contour tracé.</summary>
    public static (double Lat, double Lon) LabelPoint(IReadOnlyList<(double Lat, double Lon)> ring)
    {
        var drawn = DrawRing(ring);
        var vertices = drawn.Count > 1 ? drawn.Take(drawn.Count - 1).ToList() : drawn.ToList();
        return (vertices.Average(p => p.Lat), vertices.Average(p => p.Lon));
    }
}
