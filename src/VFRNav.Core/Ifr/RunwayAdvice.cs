using System.Globalization;
using System.Text.RegularExpressions;

namespace VFRNav.Core.Ifr;

/// <summary>Vent au sol. <see cref="DirectionTrueDeg"/> est celui du METAR (nord vrai) ; null quand il est variable (VRB).</summary>
public sealed record WindInfo(int? DirectionTrueDeg, double SpeedKt, double? GustKt = null)
{
    /// <summary>Un vent trop faible ou variable ne décide pas de la piste : le contrôle choisit au cas par cas.</summary>
    public bool Decides => DirectionTrueDeg is not null && SpeedKt >= RunwayAdvice.MinDecisiveWindKt;
}

/// <param name="HeadwindKt">Composante de face (négative : vent arrière).</param>
/// <param name="CrosswindKt">Composante de travers, toujours positive.</param>
public sealed record RunwayChoice(string Runway, double HeadwindKt, double CrosswindKt)
{
    public string Describe() =>
        (HeadwindKt >= 0 ? $"vent de face {HeadwindKt:0} kt" : $"vent arrière {-HeadwindKt:0} kt") + $", travers {CrosswindKt:0} kt";
}

/// <summary>
/// Choix de la piste d'après le vent, comme le fait le contrôle : celle qui donne le plus de vent de face (donc le moins de vent arrière
/// et de travers). C'est la piste qui décide ensuite des SID et des STAR utilisables.
/// </summary>
public static partial class RunwayAdvice
{
    /// <summary>En dessous de cette force, le vent ne désigne pas de piste.</summary>
    public const double MinDecisiveWindKt = 3;

    /// <summary>Cap magnétique de la piste d'après son numéro : « 26L » donne 260°. Null si le nom n'est pas une piste.</summary>
    public static int? HeadingOf(string runway)
    {
        var m = RunwayRx().Match(runway.Trim());
        if (!m.Success) return null;
        int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return number is >= 1 and <= 36 ? number * 10 : null;
    }

    /// <summary>
    /// Pistes classées de la meilleure à la moins bonne (le plus de vent de face d'abord, le moins de travers ensuite).
    /// Le vent du METAR est en nord vrai et les pistes en nord magnétique : <paramref name="variationEastDeg"/> (Est positif) fait le passage.
    /// Deux pistes parallèles ont les mêmes composantes : elles restent dans l'ordre donné.
    /// </summary>
    public static IReadOnlyList<RunwayChoice> Rank(IEnumerable<string> runways, WindInfo wind, double variationEastDeg = 0)
    {
        if (!wind.Decides) return [];
        double windFromMagnetic = wind.DirectionTrueDeg!.Value - variationEastDeg;
        return [.. runways
            .Select(r => (Runway: r, Heading: HeadingOf(r)))
            .Where(r => r.Heading is not null)
            .Select(r =>
            {
                double angle = (windFromMagnetic - r.Heading!.Value) * Math.PI / 180;
                return new RunwayChoice(r.Runway, Math.Round(wind.SpeedKt * Math.Cos(angle), 1), Math.Round(Math.Abs(wind.SpeedKt * Math.Sin(angle)), 1));
            })
            .OrderByDescending(c => Math.Round(c.HeadwindKt))
            .ThenBy(c => Math.Round(c.CrosswindKt))];
    }

    /// <summary>Procédures utilisables avec une piste : celles dont au moins une variante la dessert (ou toutes les pistes).</summary>
    public static IEnumerable<IfrProcedure> ForRunway(IEnumerable<IfrProcedure> procedures, string? runway) =>
        runway is null ? procedures : procedures.Where(p => p.Variants.Any(v => v.AppliesTo(runway)));

    /// <summary>Pistes citées par les variantes des procédures d'un terrain (vide si elles servent toutes les pistes).</summary>
    public static IReadOnlyList<string> RunwaysOf(IEnumerable<IfrProcedure> procedures) =>
        [.. procedures.SelectMany(p => p.Variants).SelectMany(v => v.Runways).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    // ---------- vent d'un METAR ----------

    /// <summary>Vent du METAR de ce terrain dans un texte brut (une ou plusieurs lignes). Null si le terrain ou son vent est absent.</summary>
    public static WindInfo? ParseMetar(string raw, string icao)
    {
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Contains(icao, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var token in tokens)
            {
                var m = WindRx().Match(token);
                if (!m.Success) continue;
                double factor = m.Groups[5].Value == "MPS" ? 1.943844 : 1;
                double speed = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * factor;
                double? gust = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * factor : null;
                int? direction = m.Groups[1].Value == "VRB" ? null : int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return new WindInfo(direction, Math.Round(speed, 1), gust is null ? null : Math.Round(gust.Value, 1));
            }
        }
        return null;
    }

    [GeneratedRegex(@"^([0-9]{1,2})([LRCWAB]?)$")]
    private static partial Regex RunwayNumberRx();

    private static Regex RunwayRx() => RunwayNumberRx();

    [GeneratedRegex(@"^(VRB|\d{3})(\d{2,3})(G(\d{2,3}))?(KT|MPS)$")]
    private static partial Regex WindRx();
}
