using System.Globalization;
using System.Text.RegularExpressions;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Models;

public sealed record GuideSection(string Heading, string Text);

/// <summary>
/// Explications en français pour un débutant : ce qu'est une zone, sa classe, ses limites et ce que cela change en vol à vue.
/// Texte général, pour la simulation : il ne remplace ni l'AIP ni les publications du SIA.
/// </summary>
public static partial class AirspaceGuide
{
    private static CultureInfo Fr => VFRNav.Core.Localization.Loc.FormatCulture;

    public static string Disclaimer => Loc.Get("AirspaceGuide_Explications_generales_pour_la_simulation");

    public static IReadOnlyList<GuideSection> Explain(Airspace a, double? altitudeFt = null)
    {
        var sections = new List<GuideSection>
        {
            new(Loc.F("AirspaceGuide_qu_est_ce_que_c", Airspace.TypeName(a.TypeCode)), TypeText(a.TypeCode))
        };
        if (a.IcaoClass.Length > 0) sections.Add(new(Loc.F("AirspaceGuide_Classe", a.IcaoClass), ClassText(a.IcaoClass)));

        sections.Add(new(Loc.Get("AirspaceGuide_Limites_verticales"),
            Loc.F("AirspaceGuide_Plancher_Plafond_La_zone_n", Show(a.Lower), Show(a.Upper))));
        if (altitudeFt is { } alt) sections.Add(new(Loc.F("AirspaceGuide_A_ton_altitude_ft", alt.ToString("N0", Fr)), PositionText(a, alt)));

        sections.Add(new(Loc.Get("AirspaceGuide_En_pratique_en_vol_a"), PracticeText(a)));

        if (a.Details?.Flags is { Count: > 0 } flags) sections.Add(new(Loc.Get("AirspaceGuide_Conditions_d_activation"), string.Join(", ", flags) + "."));
        if (a.Details?.Remarks is { Length: > 0 } remarks) sections.Add(new(Loc.Get("AirspaceGuide_Remarques_publiees"), remarks.Trim()));
        if (a.Details?.Frequencies is { Count: > 0 } freqs)
            sections.Add(new(Loc.Get("AirspaceGuide_Frequences_a_utiliser"), string.Join(" · ", freqs.Select(f => f.Name.Length > 0 ? $"{f.Name} {f.Value}" : f.Value))));
        return sections;
    }

    private static string Show(string limit) => limit.Length == 0 ? Loc.Get("AirspaceGuide_non_precise") : ExplainLimit(limit) is { Length: > 0 } e ? $"{limit} ({e})" : limit;

    /// <summary>Traduit une limite telle que « FL65 », « 1500ft AMSL », « 500ft GND » ou « SFC ».</summary>
    public static string ExplainLimit(string limit)
    {
        limit = limit.Trim();
        if (limit == "SFC") return Loc.Get("AirspaceGuide_depuis_le_sol");
        var m = FlRx().Match(limit);
        if (m.Success)
        {
            int fl = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return Loc.F("AirspaceGuide_niveau_de_vol_soit_environ", fl, (fl * 100).ToString("N0", Fr));
        }
        m = HeightRx().Match(limit);
        if (m.Success)
        {
            string unit = m.Groups[2].Value == "m" ? "m" : "ft";
            string n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString("N0", Fr);
            return m.Groups[3].Value == "GND"
                ? Loc.F("AirspaceGuide_au_dessus_du_sol", n, unit)
                : Loc.F("AirspaceGuide_d_altitude_au_dessus_du", n, unit);
        }
        return "";
    }

    private static string PositionText(Airspace a, double altitudeFt)
    {
        string approx = Loc.Get("AirspaceGuide_comparaison_faite_sans_tenir_compte");
        if (altitudeFt < a.LowerFt) return Loc.F("AirspaceGuide_Tu_serais_sous_le_plancher", approx);
        if (altitudeFt > a.UpperFt) return Loc.F("AirspaceGuide_Tu_serais_au_dessus_du", approx);
        return Loc.F("AirspaceGuide_Tu_serais_dans_la_zone", approx);
    }

    private static string ClassText(string cls) => cls switch
    {
        "A" => Loc.Get("AirspaceGuide_Reservee_aux_vols_aux_instruments"),
        "B" => Loc.Get("AirspaceGuide_Vols_IFR_et_VFR_acceptes"),
        "C" => Loc.Get("AirspaceGuide_Clairance_du_controle_obligatoire_pour"),
        "D" => Loc.Get("AirspaceGuide_Clairance_du_controle_et_radio"),
        "E" => Loc.Get("AirspaceGuide_Controle_pour_les_IFR_mais"),
        "F" => Loc.Get("AirspaceGuide_Non_utilisee_en_France_service"),
        "G" => Loc.Get("AirspaceGuide_Espace_non_controle_tu_vols"),
        _ => ""
    };

    private static string TypeText(int type) => type switch
    {
        1 => Loc.Get("AirspaceGuide_Zone_reglementee_R_le_vol"),
        2 => Loc.Get("AirspaceGuide_Zone_dangereuse_D_des_activites"),
        3 => Loc.Get("AirspaceGuide_Zone_interdite_P_le_survol"),
        4 => Loc.Get("AirspaceGuide_CTR_zone_de_controle_espace"),
        5 => Loc.Get("AirspaceGuide_TMZ_zone_a_transpondeur_obligatoire"),
        6 => Loc.Get("AirspaceGuide_RMZ_zone_a_radio_obligatoire"),
        7 => Loc.Get("AirspaceGuide_TMA_region_de_controle_terminale"),
        8 => Loc.Get("AirspaceGuide_TRA_zone_temporairement_reservee_reservee"),
        9 => Loc.Get("AirspaceGuide_TSA_zone_temporairement_segreguee_reservee"),
        10 => Loc.Get("AirspaceGuide_FIR_region_d_information_de"),
        11 => Loc.Get("AirspaceGuide_UIR_region_superieure_d_information"),
        12 => Loc.Get("AirspaceGuide_ADIZ_zone_ou_les_aeronefs"),
        13 => Loc.Get("AirspaceGuide_ATZ_zone_de_circulation_d"),
        14 => Loc.Get("AirspaceGuide_MATZ_zone_de_circulation_d"),
        15 => Loc.Get("AirspaceGuide_Voie_aerienne_couloir_utilise_surtout"),
        16 => Loc.Get("AirspaceGuide_MTR_itineraire_de_vol_militaire"),
        17 => Loc.Get("AirspaceGuide_Zone_d_alerte_activite_inhabituelle"),
        18 => Loc.Get("AirspaceGuide_Zone_d_avertissement_activite_dangereuse"),
        19 => Loc.Get("AirspaceGuide_Zone_protegee_survol_a_eviter"),
        20 => Loc.Get("AirspaceGuide_HTZ_zone_d_intense_activite"),
        21 => Loc.Get("AirspaceGuide_Secteur_planeur_des_planeurs_y"),
        22 => Loc.Get("AirspaceGuide_Zone_ou_le_transpondeur_est"),
        23 => Loc.Get("AirspaceGuide_TIZ_zone_d_information_de"),
        24 => Loc.Get("AirspaceGuide_TIA_espace_ou_un_service"),
        25 => Loc.Get("AirspaceGuide_MTA_region_de_controle_militaire"),
        26 => Loc.Get("AirspaceGuide_CTA_region_de_controle_espace"),
        27 => Loc.Get("AirspaceGuide_Secteur_de_controle_de_route"),
        28 => Loc.Get("AirspaceGuide_Activite_aerienne_sportive_voltige_parachutage"),
        29 => Loc.Get("AirspaceGuide_Restriction_de_survol_a_basse"),
        30 => Loc.Get("AirspaceGuide_MRT_route_militaire_d_entrainement"),
        31 => Loc.Get("AirspaceGuide_TFR_restriction_temporaire_de_vol"),
        32 => Loc.Get("AirspaceGuide_Secteur_VFR_espace_amenage_pour"),
        33 => Loc.Get("AirspaceGuide_Secteur_FIS_service_d_information"),
        34 => Loc.Get("AirspaceGuide_LTA_espace_controle_a_basse"),
        35 => Loc.Get("AirspaceGuide_UTA_espace_controle_en_haute"),
        36 => Loc.Get("AirspaceGuide_MCTR_zone_de_controle_militaire"),
        _ => Loc.Get("AirspaceGuide_Espace_aerien_dont_le_type")
    };

    private static string PracticeText(Airspace a) => a.TypeCode switch
    {
        3 => Loc.Get("AirspaceGuide_N_entre_pas_le_survol"),
        1 or 2 or 8 or 9 or 17 or 18 or 19 or 29 or 31 =>
            Loc.Get("AirspaceGuide_Avant_le_vol_verifie_si"),
        4 or 36 => Loc.Get("AirspaceGuide_Pour_entrer_appelle_le_controle"),
        7 or 24 or 25 or 26 or 34 or 35 => Loc.Get("AirspaceGuide_En_dessous_du_plancher_tu"),
        6 => Loc.Get("AirspaceGuide_Ecoute_la_frequence_avant_d"),
        5 or 22 => Loc.Get("AirspaceGuide_Transpondeur_allume_et_regle_sur"),
        13 or 14 => Loc.Get("AirspaceGuide_Annonce_toi_par_radio_sur"),
        33 => Loc.Get("AirspaceGuide_Tu_peux_appeler_la_frequence"),
        10 or 11 or 27 or 32 or 15 or 16 or 30 => Loc.Get("AirspaceGuide_Aucune_contrainte_particuliere_pour_un"),
        _ => a.Kind switch
        {
            AirspaceKind.Leisure => Loc.Get("AirspaceGuide_Regarde_dehors_en_permanence_ces"),
            AirspaceKind.Ctr or AirspaceKind.Tma => Loc.Get("AirspaceGuide_Verifie_la_classe_et_les"),
            AirspaceKind.Restricted => Loc.Get("AirspaceGuide_Verifie_l_activation_de_la"),
            AirspaceKind.Radio => Loc.Get("AirspaceGuide_Un_contact_radio_ou_un"),
            _ => Loc.Get("AirspaceGuide_Lis_les_remarques_publiees_de")
        }
    };

    [GeneratedRegex(@"^FL(\d+)$")] private static partial Regex FlRx();
    [GeneratedRegex(@"^(\d+)(ft|m) (AMSL|GND)$")] private static partial Regex HeightRx();
}
