using System.Globalization;
using System.Text;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Models;

/// <param name="Term">Sigle tel qu'on le voit sur les cartes, dans l'application ou dans un message météo.</param>
/// <param name="Meaning">Signification en toutes lettres.</param>
/// <param name="Explanation">Ce que cela veut dire en pratique pour un pilote à vue.</param>
public sealed record GlossaryEntry(string Category, string Term, string Meaning, string Explanation);

/// <summary>
/// Sigles des cartes aéronautiques (VAC, OACI), des zones, des messages météo et de MSFS Rhumb, expliqués en français.
/// Texte général pour la simulation : il ne remplace pas l'AIP ni les publications du SIA.
/// </summary>
public static class Glossary
{
    public static string Zones => Loc.Get("xaml_Espaces_aeriens");
    public static string Limits => Loc.Get("Glossary_Altitudes_hauteurs_et_unites");
    public static string Services => Loc.Get("Glossary_Services_radio_et_documents");
    public static string Navigation => Loc.Get("Glossary_Navigation_et_radionavigation");
    public static string Aerodrome => Loc.Get("Glossary_Aerodrome_et_cartes_VAC");
    public static string Weather => Loc.Get("Glossary_Meteo");
    public static string Rules => Loc.Get("Glossary_Regles_de_vol");
    public static string Symbols => Loc.Get("Glossary_Symboles_de_la_carte");

    private static readonly Localized<IReadOnlyList<string>> CategoriesByLanguage = new(() => [Zones, Limits, Services, Navigation, Aerodrome, Weather, Rules, Symbols]);
    public static IReadOnlyList<string> Categories => CategoriesByLanguage.Value;

    private static readonly Localized<IReadOnlyList<GlossaryEntry>> EntriesByLanguage = new(() => [
        // ---- espaces aériens
        new(Zones, "CTR", Loc.Get("Glossary_Zone_de_controle_Control_Zone"), Loc.Get("Glossary_Espace_controle_du_sol_jusqu")),
        new(Zones, "TMA", Loc.Get("Glossary_Region_de_controle_terminale"), Loc.Get("Glossary_Espace_controle_en_altitude_au")),
        new(Zones, "CTA", Loc.Get("Glossary_Region_de_controle"), Loc.Get("Glossary_Espace_controle_en_altitude_voisin")),
        new(Zones, "LTA / UTA", Loc.Get("Glossary_Region_de_controle_inferieure_superieure"), Loc.Get("Glossary_Espaces_controles_a_moyenne_ou")),
        new(Zones, "FIR / UIR", Loc.Get("Glossary_Region_d_information_de_vol"), Loc.Get("Glossary_Grands_decoupages_du_ciel_dans")),
        new(Zones, "R", Loc.Get("Glossary_Zone_reglementee"), Loc.Get("Glossary_Le_vol_n_y_est")),
        new(Zones, "D", Loc.Get("Glossary_Zone_dangereuse"), Loc.Get("Glossary_Activites_risquees_pour_un_avion")),
        new(Zones, "P", Loc.Get("Glossary_Zone_interdite"), Loc.Get("Glossary_Survol_interdit_sauf_autorisation_speciale")),
        new(Zones, "LF-R324, LF-D…, LF-P…", Loc.Get("Glossary_Numero_de_zone"), Loc.Get("Glossary_LF_designe_la_France_la")),
        new(Zones, "TRA / TSA", Loc.Get("Glossary_Zone_temporairement_reservee_segreguee"), Loc.Get("Glossary_Reservee_a_une_activite_souvent")),
        new(Zones, "ZRT", Loc.Get("Glossary_Zone_reglementee_temporaire_designation_de"), Loc.Get("Glossary_Restriction_qui_n_existe_que")),
        new(Zones, "RMZ", Loc.Get("Glossary_Zone_a_radio_obligatoire_Radio"), Loc.Get("Glossary_Il_faut_ecouter_la_frequence")),
        new(Zones, "TMZ", Loc.Get("Glossary_Zone_a_transpondeur_obligatoire_Transponder"), Loc.Get("Glossary_Le_transpondeur_doit_etre_allume")),
        new(Zones, "ATZ", Loc.Get("Glossary_Zone_de_circulation_d_aerodrome"), Loc.Get("Glossary_Zone_autour_d_un_aerodrome")),
        new(Zones, "SIV", Loc.Get("Glossary_Service_d_information_de_vol"), Loc.Get("Glossary_Un_controleur_donne_des_informations")),
        new(Zones, "FIS", Loc.Get("Glossary_Flight_Information_Service"), Loc.Get("Glossary_Nom_international_du_service_d")),
        new(Zones, "ADIZ", Loc.Get("Glossary_Zone_d_identification_de_defense"), Loc.Get("Glossary_Les_aeronefs_doivent_etre_identifies")),
        new(Zones, Loc.Get("Glossary_Classe_A"), Loc.Get("Glossary_Espace_aerien_de_classe_A"), Loc.Get("Glossary_Reserve_aux_vols_aux_instruments")),
        new(Zones, Loc.Get("Glossary_Classe_B_C"), Loc.Get("Glossary_Espaces_aeriens_de_classes_B"), Loc.Get("Glossary_Clairance_du_controle_obligatoire_pour")),
        new(Zones, Loc.Get("Glossary_Classe_D"), Loc.Get("Glossary_Espace_aerien_de_classe_D"), Loc.Get("Glossary_Clairance_et_radio_obligatoires_pour")),
        new(Zones, Loc.Get("Glossary_Classe_E"), Loc.Get("Glossary_Espace_aerien_de_classe_E"), Loc.Get("Glossary_Controle_pour_les_IFR_un")),
        new(Zones, Loc.Get("Glossary_Classe_G"), Loc.Get("Glossary_Espace_aerien_non_controle"), Loc.Get("Glossary_On_vole_sous_sa_propre")),

        // ---- altitudes et unités
        new(Limits, "SFC", Loc.Get("Glossary_Surface"), Loc.Get("Glossary_Limite_qui_part_du_sol")),
        new(Limits, "GND / AGL", Loc.Get("Glossary_Au_dessus_du_sol_Ground"), Loc.Get("Glossary_Hauteur_mesuree_a_partir_du")),
        new(Limits, "AMSL / MSL", Loc.Get("Glossary_Au_dessus_du_niveau_moyen"), Loc.Get("Glossary_Altitude_mesuree_a_partir_du")),
        new(Limits, "FL", Loc.Get("Glossary_Niveau_de_vol_Flight_Level"), Loc.Get("Glossary_Altitude_en_centaines_de_pieds")),
        new(Limits, "ft", Loc.Get("Glossary_Pied_feet"), Loc.Get("Glossary_Unite_d_altitude_1_ft")),
        new(Limits, "NM", Loc.Get("Glossary_Mille_nautique"), Loc.Get("Glossary_Unite_de_distance_1_NM")),
        new(Limits, "kt", "Nœud", Loc.Get("Glossary_Vitesse_d_un_mille_nautique")),
        new(Limits, "hPa", Loc.Get("Glossary_Hectopascal"), Loc.Get("Glossary_Unite_de_pression_atmospherique_Au")),
        new(Limits, "QNH", Loc.Get("Glossary_Calage_altimetrique"), Loc.Get("Glossary_Pression_ramenee_au_niveau_de")),
        new(Limits, "QFE", Loc.Get("Glossary_Pression_au_niveau_du_terrain"), Loc.Get("Glossary_Affiche_sur_l_altimetre_il")),
        new(Limits, "1013", Loc.Get("Glossary_Calage_standard"), Loc.Get("Glossary_Pression_de_reference_1013_25")),
        new(Limits, "MAX / MIN", Loc.Get("Glossary_Limite_maximale_minimale"), Loc.Get("Glossary_Sur_une_carte_VAC_1500")),

        // ---- services, radio et documents
        new(Services, "ATC", Loc.Get("Glossary_Controle_de_la_circulation_aerienne"), Loc.Get("Glossary_Le_service_qui_donne_les")),
        new(Services, "ATS", Loc.Get("Glossary_Services_de_la_circulation_aerienne"), Loc.Get("Glossary_Ensemble_des_services_rendus_aux")),
        new(Services, "TWR", Loc.Get("Glossary_Tour_de_controle"), Loc.Get("Glossary_Controle_les_decollages_et_atterrissages")),
        new(Services, "APP", Loc.Get("Glossary_Approche"), Loc.Get("Glossary_Controle_les_arrivees_et_departs")),
        new(Services, "GND / SOL", Loc.Get("Glossary_Controle_au_sol"), Loc.Get("Glossary_Gere_la_circulation_des_avions")),
        new(Services, "AFIS", Loc.Get("Glossary_Service_d_information_de_vol_2"), Loc.Get("Glossary_Un_agent_informe_sur_le")),
        new(Services, "A/A", "Air-air", Loc.Get("Glossary_Frequence_d_auto_information_entre")),
        new(Services, "ATIS", Loc.Get("Glossary_Service_automatique_d_information_de"), Loc.Get("Glossary_Message_radio_continu_piste_en")),
        new(Services, "RTF", Loc.Get("Glossary_Radiotelephonie"), Loc.Get("Glossary_Communications_radio_entre_le_pilote")),
        new(Services, "SIA", Loc.Get("Glossary_Service_de_l_information_aeronautique"), Loc.Get("Glossary_Organisme_francais_qui_publie_l")),
        new(Services, "AIP", Loc.Get("Glossary_Publication_d_information_aeronautique"), Loc.Get("Glossary_Le_document_officiel_qui_decrit")),
        new(Services, "SUP AIP / AIC", Loc.Get("Glossary_Supplement_AIP_circulaire_d_information"), Loc.Get("Glossary_Modifications_temporaires_de_l_AIP")),
        new(Services, "NOTAM", Loc.Get("Glossary_Avis_aux_navigants_Notice_to"), Loc.Get("Glossary_Message_qui_signale_un_changement")),
        new(Services, "SOFIA", Loc.Get("Glossary_Service_en_ligne_d_information"), Loc.Get("Glossary_Site_du_SIA_ou_l")),
        new(Services, "OACI / ICAO", Loc.Get("Glossary_Organisation_de_l_aviation_civile"), Loc.Get("Glossary_Elle_fixe_les_codes_utilises")),
        new(Services, "VAC", Loc.Get("Glossary_Carte_d_approche_a_vue"), Loc.Get("Glossary_Carte_d_un_aerodrome_pour")),

        // ---- navigation
        new(Navigation, "VOR", Loc.Get("Glossary_Radiophare_omnidirectionnel_VHF"), Loc.Get("Glossary_Balise_qui_donne_la_direction")),
        new(Navigation, "NDB", Loc.Get("Glossary_Radiophare_non_directionnel"), Loc.Get("Glossary_Balise_simple_sur_une_frequence")),
        new(Navigation, "ADF", Loc.Get("Glossary_Radiogoniometre_automatique"), Loc.Get("Glossary_L_instrument_de_bord_dont")),
        new(Navigation, "DME", Loc.Get("Glossary_Equipement_de_mesure_de_distance"), Loc.Get("Glossary_Donne_la_distance_en_NM")),
        new(Navigation, "ILS", Loc.Get("Glossary_Systeme_d_atterrissage_aux_instruments"), Loc.Get("Glossary_Guidage_radio_precis_vers_la")),
        new(Navigation, "GPS / RNAV", Loc.Get("Glossary_Navigation_par_satellite_de_surface"), Loc.Get("Glossary_Navigation_par_positions_calculees_plutot")),
        new(Navigation, "WP", Loc.Get("Glossary_Point_de_route_waypoint"), Loc.Get("Glossary_Point_tournant_ou_repere_pose")),
        new(Navigation, "Rv", Loc.Get("Glossary_Route_vraie"), Loc.Get("Glossary_Direction_de_la_route_par")),
        new(Navigation, "Rm", Loc.Get("Glossary_Route_magnetique"), Loc.Get("Glossary_Route_vraie_corrigee_de_la")),
        new(Navigation, "Cm", Loc.Get("Glossary_Cap_magnetique"), Loc.Get("Glossary_Direction_a_tenir_avec_l")),
        new(Navigation, Loc.Get("Ui_Derive"), Loc.Get("Glossary_Correction_de_derive"), Loc.Get("Glossary_Angle_dont_il_faut_corriger")),
        new(Navigation, "GS / Vs", Loc.Get("Glossary_Vitesse_sol_Ground_Speed"), Loc.Get("Glossary_Vitesse_de_l_avion_par")),
        new(Navigation, "TAS / Vp", Loc.Get("Glossary_Vitesse_propre_True_Air_Speed"), Loc.Get("Glossary_Vitesse_de_l_avion_par_2")),
        new(Navigation, "QDM / QDR", Loc.Get("Glossary_Relevement_magnetique_vers_depuis_une"), Loc.Get("Glossary_QDM_cap_magnetique_pour_aller")),
        new(Navigation, Loc.Get("Glossary_Declinaison"), Loc.Get("Glossary_Variation_magnetique"), Loc.Get("Glossary_Ecart_entre_le_nord_geographique")),
        new(Navigation, "ETA / ETD / ETE", Loc.Get("Glossary_Heure_d_arrivee_de_depart"), Loc.Get("Glossary_ETA_heure_d_arrivee_prevue")),

        // ---- aérodrome et cartes VAC
        new(Aerodrome, "ARP", Loc.Get("Glossary_Point_de_reference_d_aerodrome"), Loc.Get("Glossary_Position_officielle_de_l_aerodrome")),
        new(Aerodrome, "RWY", Loc.Get("Glossary_Piste_runway"), Loc.Get("Glossary_Indiquee_par_son_QFU_arrondi")),
        new(Aerodrome, "QFU", Loc.Get("Glossary_Orientation_magnetique_de_la_piste"), Loc.Get("Glossary_Cap_magnetique_de_l_axe")),
        new(Aerodrome, "TWY", Loc.Get("Glossary_Voie_de_circulation_taxiway"), Loc.Get("Glossary_Chemin_que_suit_l_avion")),
        new(Aerodrome, "THR", Loc.Get("Glossary_Seuil_de_piste_threshold"), Loc.Get("Glossary_Debut_de_la_partie_de")),
        new(Aerodrome, "ELEV", Loc.Get("Glossary_Altitude_de_l_aerodrome"), Loc.Get("Glossary_Altitude_du_point_le_plus")),
        new(Aerodrome, "AD", Loc.Get("Glossary_Aerodrome"), Loc.Get("Glossary_Terrain_d_aviation_Les_cartes")),
        new(Aerodrome, "PAPI", Loc.Get("Glossary_Indicateur_de_pente_d_approche"), Loc.Get("Glossary_Quatre_feux_a_cote_de")),
        new(Aerodrome, "ACFT", Loc.Get("Glossary_Aeronef"), Loc.Get("Glossary_Designe_un_avion_un_helicoptere")),
        new(Aerodrome, "ULM", Loc.Get("Glossary_Ultra_leger_motorise"), Loc.Get("Glossary_Petit_avion_leger_Certains_aerodromes")),
        new(Aerodrome, "PN", Loc.Get("Glossary_Preavis_necessaire"), Loc.Get("Glossary_Il_faut_prevenir_l_aerodrome")),
        new(Aerodrome, "PPR", Loc.Get("Glossary_Autorisation_prealable_Prior_Permission_Required"), Loc.Get("Glossary_L_aerodrome_doit_avoir_donne")),
        new(Aerodrome, "H24", Loc.Get("Glossary_Vingt_quatre_heures_sur_vingt"), Loc.Get("Glossary_Le_service_ou_l_aerodrome")),
        new(Aerodrome, "HJ", Loc.Get("Glossary_Heures_de_jour"), Loc.Get("Glossary_Du_lever_au_coucher_du")),
        new(Aerodrome, "HO", Loc.Get("Glossary_Heures_de_bureau"), Loc.Get("Glossary_Aux_heures_d_ouverture_du")),
        new(Aerodrome, Loc.Get("Glossary_Point_de_report"), Loc.Get("Glossary_Point_de_compte_rendu_VFR"), Loc.Get("Glossary_Repere_connu_village_etang_route")),
        new(Aerodrome, Loc.Get("Glossary_Circuit"), Loc.Get("Glossary_Circuit_d_aerodrome"), Loc.Get("Glossary_Trajectoire_standard_autour_de_la")),
        new(Aerodrome, Loc.Get("Glossary_Main_gauche_droite"), Loc.Get("Glossary_Sens_du_circuit"), Loc.Get("Glossary_Virages_vers_la_gauche_ou")),

        // ---- météo
        new(Weather, "METAR", Loc.Get("Glossary_Observation_meteo_d_aerodrome"), Loc.Get("Glossary_Message_code_qui_donne_le")),
        new(Weather, "TAF", Loc.Get("Glossary_Prevision_d_aerodrome"), Loc.Get("Glossary_Prevision_de_la_meteo_sur")),
        new(Weather, "SPECI", Loc.Get("Glossary_Observation_speciale"), Loc.Get("Glossary_METAR_emis_en_dehors_de")),
        new(Weather, "CAVOK", Loc.Get("Glossary_Ceiling_and_Visibility_OK"), Loc.Get("Glossary_Visibilite_de_10_km_ou")),
        new(Weather, "FEW / SCT / BKN / OVC", Loc.Get("Glossary_Quantite_de_nuages"), Loc.Get("Glossary_FEW_1_a_2_huitiemes")),
        new(Weather, "BECMG / TEMPO / PROB", Loc.Get("Glossary_Evolution_dans_un_TAF"), Loc.Get("Glossary_BECMG_devient_TEMPO_par_moments")),
        new(Weather, "NOSIG", Loc.Get("Glossary_Pas_de_changement_significatif"), Loc.Get("Glossary_Aucune_evolution_notable_prevue_dans")),
        new(Weather, Loc.Get("Glossary_Visibilite"), Loc.Get("Glossary_Visibilite_horizontale"), Loc.Get("Glossary_Donnee_en_metres_9999_10")),
        new(Weather, "VMC / IMC", Loc.Get("Glossary_Conditions_meteorologiques_de_vol_a"), Loc.Get("Glossary_Les_conditions_minimales_de_visibilite")),
        new(Weather, "VFR / MVFR / IFR / LIFR", Loc.Get("Glossary_Categories_de_vol_d_apres"), Loc.Get("Glossary_VFR_bonnes_conditions_MVFR_marginales")),

        // ---- règles de vol
        new(Rules, "VFR", Loc.Get("Glossary_Regles_de_vol_a_vue"), Loc.Get("Glossary_On_pilote_en_s_aidant")),
        new(Rules, "IFR", Loc.Get("Glossary_Regles_de_vol_aux_instruments"), Loc.Get("Glossary_On_pilote_avec_les_instruments")),
        new(Rules, "SVFR", Loc.Get("Glossary_VFR_special"), Loc.Get("Glossary_Autorisation_du_controle_d_entrer")),
        new(Rules, Loc.Get("Glossary_Clairance"), Loc.Get("Glossary_Autorisation_du_controle"), Loc.Get("Glossary_Accord_du_controle_pour_entrer")),
        new(Rules, Loc.Get("Glossary_Transit"), Loc.Get("Glossary_Traversee_d_une_zone"), Loc.Get("Glossary_Passage_dans_un_espace_controle")),

        // ---- symboles de la carte
        new(Symbols, Loc.Get("Glossary_Pastille_de_terrain"), Loc.Get("Glossary_Terrain_et_meteo"), Loc.Get("Glossary_Rond_sur_un_terrain_sa")),
        new(Symbols, Loc.Get("Glossary_Triangle_jaune"), Loc.Get("Glossary_Point_de_report_VFR"), Loc.Get("Glossary_Point_de_report_d_un")),
        new(Symbols, Loc.Get("Glossary_Losange_violet"), "VOR", Loc.Get("Glossary_Balise_VOR_avec_sa_frequence")),
        new(Symbols, Loc.Get("Glossary_Rond_orange"), "NDB", Loc.Get("Glossary_Radiophare_non_directionnel_avec_sa")),
        new(Symbols, Loc.Get("Glossary_Contour_rouge"), Loc.Get("Glossary_Zones_reglementees_dangereuses_interdites"), Loc.Get("Glossary_Zones_R_D_P_a")),
        new(Symbols, Loc.Get("Glossary_Contour_bleu"), Loc.Get("Glossary_CTR_et_TMA"), Loc.Get("Glossary_Espaces_controles_la_classe_et")),
        new(Symbols, Loc.Get("Glossary_Contour_violet"), Loc.Get("Glossary_Radio_transpondeur_circulation_d_aerodrome"), Loc.Get("Glossary_RMZ_TMZ_ATZ_la_radio")),
        new(Symbols, Loc.Get("Glossary_Contour_orange"), Loc.Get("Glossary_Loisirs_et_sport_aerien"), Loc.Get("Glossary_Planeur_parachutage_voltige_ULM_regarder")),
        new(Symbols, Loc.Get("Glossary_Texte_le_long_du_trait"), Loc.Get("Glossary_Nom_et_limites_de_la"), Loc.Get("Glossary_Le_nom_le_plancher_et")),
        new(Symbols, Loc.Get("Glossary_Avion_rose"), Loc.Get("Glossary_Votre_avion_dans_MSFS"), Loc.Get("Glossary_Position_et_cap_de_l")),
        new(Symbols, Loc.Get("Glossary_Ronds_numerotes"), Loc.Get("Glossary_Points_de_la_route"), Loc.Get("Glossary_Depart_et_arrivee_en_bleu")),
    ]);
    public static IReadOnlyList<GlossaryEntry> Entries => EntriesByLanguage.Value;

    /// <summary>Entrées dont le sigle, le sens ou l'explication contient le texte cherché (sans tenir compte des accents ni de la casse).</summary>
    public static IReadOnlyList<GlossaryEntry> Search(string? query)
    {
        string q = Normalize(query ?? "");
        if (q.Length == 0) return Entries;
        return [.. Entries
            .Select(e => (Entry: e, Rank: Rank(e, q)))
            .Where(t => t.Rank > 0)
            .OrderByDescending(t => t.Rank)
            .Select(t => t.Entry)];
    }

    private static int Rank(GlossaryEntry e, string q)
    {
        string term = Normalize(e.Term);
        if (term == q) return 4;
        if (term.Split(' ', '/', ',', '-').Any(w => w.Trim() == q)) return 3;
        if (term.Contains(q, StringComparison.Ordinal)) return 2;
        return Normalize(e.Meaning).Contains(q, StringComparison.Ordinal) || Normalize(e.Explanation).Contains(q, StringComparison.Ordinal) ? 1 : 0;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Trim();
    }
}
