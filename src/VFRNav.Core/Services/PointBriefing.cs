using System.Globalization;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Gravité de ce qu'une zone demande au pilote.</summary>
public enum Severity { Free = 0, Info = 1, Caution = 2, Clearance = 3, Forbidden = 4 }

/// <param name="Verdict">Ce que la zone implique à ce point (« Clairance obligatoire »…).</param>
/// <param name="Action">Ce qu'il faut faire.</param>
/// <param name="Frequencies">Fréquences publiées pour la zone (« Paris Info 124.350 »).</param>
public sealed record ZoneBrief(Airspace Zone, VerticalStatus Status, Severity Severity, string Verdict, string Action, IReadOnlyList<string> Frequencies);

/// <param name="AltitudeSource">« saisie », « circuit (terrain + 1 000 ft) » ou « croisière ».</param>
/// <param name="Frequencies">Fréquences à avoir sous la main à ce point : terrain, zones et information de vol.</param>
public sealed record PointBriefing(int Index, Waypoint Point, double AltitudeFt, string AltitudeSource, IReadOnlyList<ZoneBrief> Zones, IReadOnlyList<string> Frequencies)
{
    /// <summary>La plus haute gravité parmi les zones où l'on se trouve.</summary>
    public Severity Worst => Zones.Where(z => z.Status == VerticalStatus.Inside).Select(z => z.Severity).DefaultIfEmpty(Severity.Free).Max();

    public string Name => Point.Icao ?? Point.Name;
}

/// <summary>
/// Pour chaque point de la route : à quelle altitude on y passe, quelles zones on y traverse, ce que chacune demande
/// (survol, radio, clairance) et les fréquences à contacter. Texte général pour la simulation : il ne remplace pas l'AIP.
/// </summary>
public static class PointBriefings
{
    public const double CircuitHeightFt = 1000;
    public const double FloorMarginFt = 1000;

    /// <summary>Altitude à ce point et d'où elle vient. Au départ et à l'arrivée d'un terrain sans altitude saisie : l'altitude du circuit.</summary>
    public static (double Ft, string Source) AltitudeFor(IReadOnlyList<Waypoint> route, int index, double cruiseFt)
    {
        var wp = route[index];
        if (wp.AltitudeFt is { } set) return (set, "saisie");
        bool endpoint = route.Count > 1 && (index == 0 || index == route.Count - 1);
        if (wp.IsAirport && wp.ElevationFt > 0 && endpoint) return (wp.ElevationFt + CircuitHeightFt, "circuit (terrain + 1 000 ft)");
        return (cruiseFt, "croisière");
    }

    public static IReadOnlyList<PointBriefing> ForRoute(IReadOnlyList<Waypoint> route, double cruiseFt, IReadOnlyList<Airspace> zones,
        Func<Waypoint, IReadOnlyList<string>>? aerodromeFrequencies = null) =>
        [.. route.Select((_, i) => For(route, i, cruiseFt, zones, aerodromeFrequencies?.Invoke(route[i])))];

    public static PointBriefing For(IReadOnlyList<Waypoint> route, int index, double cruiseFt, IEnumerable<Airspace> zones, IReadOnlyList<string>? aerodromeFrequencies = null)
    {
        var wp = route[index];
        var (alt, source) = AltitudeFor(route, index, cruiseFt);
        var briefs = new List<ZoneBrief>();
        foreach (var zone in zones)
        {
            if (!zone.Contains(wp.Lon, wp.Lat)) continue;
            if (alt >= zone.LowerFt && alt <= zone.UpperFt)
            {
                if (Assess(zone) is { } b) briefs.Add(b);
            }
            else if (alt < zone.LowerFt && zone.LowerFt - alt <= FloorMarginFt && Assess(zone) is { Severity: >= Severity.Caution } above)
            {
                briefs.Add(above with
                {
                    Status = VerticalStatus.Below,
                    Severity = Severity.Info,
                    Verdict = "Hors zone : tu passes sous le plancher",
                    Action = $"Reste sous {zone.Lower} (marge {zone.LowerFt - alt:N0} ft) pour ne pas y entrer."
                });
            }
        }

        var ordered = briefs.OrderBy(b => b.Status).ThenByDescending(b => b.Severity).ThenBy(b => b.Zone.LowerFt).ToList();
        var freqs = new List<string>();
        if (aerodromeFrequencies is not null) freqs.AddRange(aerodromeFrequencies);
        foreach (var b in ordered.Where(b => b.Status == VerticalStatus.Inside))
            foreach (var f in b.Frequencies) if (!freqs.Contains(f)) freqs.Add(f);
        return new PointBriefing(index, wp, alt, source, ordered, freqs);
    }

    /// <summary>Ce qu'une zone demande à qui s'y trouve, ou null pour une zone sans conséquence (FIR, voies aériennes…).</summary>
    public static ZoneBrief? Assess(Airspace z)
    {
        var freqs = Frequencies(z);
        string call = freqs.Count > 0 ? $" sur {string.Join(" / ", freqs)}" : "";
        string cls = z.IcaoClass;

        (Severity, string, string)? r = z.TypeCode switch
        {
            3 => (Severity.Forbidden, "Survol interdit", "N'entre pas : une autorisation spéciale est nécessaire. Change d'altitude ou de route."),
            1 or 8 or 9 or 31 => (Severity.Caution, "Zone réglementée : entrée soumise à conditions",
                $"Vérifie qu'elle est active (NOTAM, SIA). Si elle l'est, contacte l'organisme{call} ou contourne-la."),
            29 => (Severity.Caution, "Restriction de survol à basse altitude", "Respecte la hauteur minimale publiée pour cette zone, ou évite-la."),
            2 => (Severity.Caution, "Zone dangereuse", "Survol non interdit mais déconseillé quand elle est active : vérifie son activation."),
            17 or 18 => (Severity.Info, "Zone d'alerte ou d'avertissement", "Reste vigilant ; vérifie les NOTAM."),
            19 => (Severity.Caution, "Zone protégée", "Évite de la survoler à basse altitude."),
            4 or 36 or 7 or 24 or 25 or 26 or 34 or 35 => Controlled(z, cls, call),
            5 or 22 => (Severity.Caution, "Transpondeur obligatoire", "Allume le transpondeur et affiche le code demandé avant d'entrer."),
            6 => (Severity.Caution, "Radio obligatoire (RMZ)", $"Écoute la fréquence{call} avant d'entrer et annonce ta position et tes intentions. Pas de clairance demandée."),
            13 or 14 => (Severity.Caution, "Zone d'aérodrome", $"Annonce-toi{call} et respecte le circuit et la circulation locale."),
            33 => (Severity.Info, "Information de vol disponible (FIS)", $"Tu peux appeler{call} pour être renseigné ; ce n'est pas obligatoire."),
            12 => (Severity.Caution, "Zone d'identification (ADIZ)", "Fais-toi identifier avant d'entrer."),
            21 or 28 => (Severity.Caution, "Activité de loisir ou sportive", "Regarde dehors en permanence, passe au large ou franchis la zone bien au-dessus ou en dessous."),
            20 or 23 or 32 => (Severity.Info, z.TypeCode == 20 ? "Zone d'activité d'hélicoptères" : "Zone d'information de trafic", "Reste attentif au trafic."),
            _ => null
        };
        return r is var (sev, verdict, action) ? new ZoneBrief(z, VerticalStatus.Inside, sev, verdict, action, freqs) : null;
    }

    private static (Severity, string, string) Controlled(Airspace z, string cls, string call) => cls switch
    {
        "A" => (Severity.Forbidden, "Vol à vue interdit (classe A)",
            $"Demande une clairance{call}. Sans elle : change d'altitude (sous {z.Lower}) ou de route."),
        "B" or "C" => (Severity.Clearance, $"Clairance obligatoire (classe {cls})",
            $"Appelle le contrôle{call} avant l'entrée et attends la clairance. Sans elle : passe sous {z.Lower} ou contourne."),
        "D" => (Severity.Clearance, "Clairance et radio obligatoires (classe D)",
            $"Appelle le contrôle{call} avant l'entrée et attends la clairance. Sans elle : passe sous {z.Lower} ou contourne."),
        "E" or "F" or "G" => (Severity.Free, $"Survol autorisé sans clairance (classe {cls})",
            $"Aucune clairance demandée{(call.Length > 0 ? $" ; tu peux écouter{call}" : "")}."),
        _ => (Severity.Caution, "Espace contrôlé (classe non précisée)",
            $"Considère la clairance comme nécessaire : contacte{(call.Length > 0 ? call : " le contrôle")} avant d'entrer.")
    };

    /// <summary>Fréquences publiées pour la zone : « Paris Info 124.350 ».</summary>
    public static IReadOnlyList<string> Frequencies(Airspace z) =>
        z.Details?.Frequencies is { Count: > 0 } f
            ? [.. f.Select(x => x.Name.Length > 0 ? $"{x.Name.Trim()} {x.Value}" : x.Value).Distinct()]
            : [];

    /// <summary>« Tour 118.350 »-style : fréquences d'un terrain.</summary>
    public static string Format(string service, decimal mhz) => $"{service} {mhz.ToString("0.000", CultureInfo.InvariantCulture)}";

    /// <summary>Repère visuel court pour la gravité.</summary>
    public static string Mark(Severity s) => s switch
    {
        Severity.Forbidden => "⛔",
        Severity.Clearance => "⚠",
        Severity.Caution => "⚠",
        Severity.Info => "ℹ",
        _ => "✔"
    };

}
