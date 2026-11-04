using System.Globalization;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

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
        if (wp.AltitudeFt is { } set) return (set, Loc.Get("Briefing_Source_entered"));
        bool endpoint = route.Count > 1 && (index == 0 || index == route.Count - 1);
        if (wp.IsAirport && wp.ElevationFt > 0 && endpoint) return (wp.ElevationFt + CircuitHeightFt, Loc.Get("Briefing_Source_circuit"));
        return (cruiseFt, Loc.Get("PointBriefing_croisiere"));
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
                    Verdict = Loc.Get("PointBriefing_Hors_zone_tu_passes_sous"),
                    Action = Loc.F("PointBriefing_Reste_sous_marge_ft_pour", zone.Lower, zone.LowerFt - alt)
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
        string call = freqs.Count > 0 ? Loc.F("PointBriefing_sur", string.Join(" / ", freqs)) : "";
        string cls = z.IcaoClass;

        (Severity, string, string)? r = z.TypeCode switch
        {
            3 => (Severity.Forbidden, Loc.Get("PointBriefing_Survol_interdit"), Loc.Get("PointBriefing_N_entre_pas_une_autorisation")),
            1 or 8 or 9 or 31 => (Severity.Caution, Loc.Get("PointBriefing_Zone_reglementee_entree_soumise_a"),
                Loc.F("PointBriefing_Verifie_qu_elle_est_active", call)),
            29 => (Severity.Caution, Loc.Get("Airspace_Restriction_de_survol_a_basse"), Loc.Get("PointBriefing_Respecte_la_hauteur_minimale_publiee")),
            2 => (Severity.Caution, Loc.Get("Glossary_Zone_dangereuse"), Loc.Get("PointBriefing_Survol_non_interdit_mais_deconseille")),
            17 or 18 => (Severity.Info, Loc.Get("PointBriefing_Zone_d_alerte_ou_d"), Loc.Get("PointBriefing_Reste_vigilant_verifie_les_NOTAM")),
            19 => (Severity.Caution, Loc.Get("Airspace_Zone_protegee"), Loc.Get("PointBriefing_Evite_de_la_survoler_a")),
            4 or 36 or 7 or 24 or 25 or 26 or 34 or 35 => Controlled(z, cls, call),
            5 or 22 => (Severity.Caution, Loc.Get("PointBriefing_Transpondeur_obligatoire"), Loc.Get("PointBriefing_Allume_le_transpondeur_et_affiche")),
            6 => (Severity.Caution, Loc.Get("PointBriefing_Radio_obligatoire_RMZ"), Loc.F("PointBriefing_Ecoute_la_frequence_avant_d", call)),
            13 or 14 => (Severity.Caution, Loc.Get("PointBriefing_Zone_d_aerodrome"), Loc.F("PointBriefing_Annonce_toi_et_respecte_le", call)),
            33 => (Severity.Info, Loc.Get("PointBriefing_Information_de_vol_disponible_FIS"), Loc.F("PointBriefing_Tu_peux_appeler_pour_etre", call)),
            12 => (Severity.Caution, Loc.Get("PointBriefing_Zone_d_identification_ADIZ"), Loc.Get("PointBriefing_Fais_toi_identifier_avant_d")),
            21 or 28 => (Severity.Caution, Loc.Get("PointBriefing_Activite_de_loisir_ou_sportive"), Loc.Get("PointBriefing_Regarde_dehors_en_permanence_passe")),
            20 or 23 or 32 => (Severity.Info, z.TypeCode == 20 ? Loc.Get("PointBriefing_Zone_d_activite_d_helicopteres") : Loc.Get("PointBriefing_Zone_d_information_de_trafic"), Loc.Get("PointBriefing_Reste_attentif_au_trafic")),
            _ => null
        };
        return r is var (sev, verdict, action) ? new ZoneBrief(z, VerticalStatus.Inside, sev, verdict, action, freqs) : null;
    }

    private static (Severity, string, string) Controlled(Airspace z, string cls, string call) => cls switch
    {
        "A" => (Severity.Forbidden, Loc.Get("PointBriefing_Vol_a_vue_interdit_classe"),
            Loc.F("PointBriefing_Demande_une_clairance_Sans_elle", call, z.Lower)),
        "B" or "C" => (Severity.Clearance, Loc.F("PointBriefing_Clairance_obligatoire_classe", cls),
            Loc.F("PointBriefing_Appelle_le_controle_avant_l", call, z.Lower)),
        "D" => (Severity.Clearance, Loc.Get("PointBriefing_Clairance_et_radio_obligatoires_classe"),
            Loc.F("PointBriefing_Appelle_le_controle_avant_l", call, z.Lower)),
        "E" or "F" or "G" => (Severity.Free, Loc.F("PointBriefing_Survol_autorise_sans_clairance_classe", cls),
            Loc.F("PointBriefing_Aucune_clairance_demandee", (call.Length > 0 ? Loc.F("PointBriefing_tu_peux_ecouter", call) : ""))),
        _ => (Severity.Caution, Loc.Get("PointBriefing_Espace_controle_classe_non_precisee"),
            Loc.F("PointBriefing_Considere_la_clairance_comme_necessaire", (call.Length > 0 ? call : Loc.Get("PointBriefing_le_controle"))))
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
