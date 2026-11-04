using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Aerodromes;
using VFRNav.Core.Weather;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Un terrain de la route avec ses infos d'aérodrome et sa météo décodée.</summary>
public sealed record AirportBrief(Waypoint Waypoint, AerodromeInfo? Info, DecodedReport? Metar, DecodedReport? Taf);

public sealed record FlightSheetInput(
    IReadOnlyList<Waypoint> Route,
    FlightParameters Parameters,
    NavLog Log,
    TerrainProfile? Terrain,
    string? TerrainNote,
    IReadOnlyList<AirspaceCrossing> Crossings,
    IReadOnlyList<NavaidNearRoute> Navaids,
    IReadOnlyList<RouteObstacle> Obstacles,
    string? ObstacleNote,
    IReadOnlyList<AirportBrief> Airports,
    ImageSource? ProfileImage,
    string? WeatherNote,
    DateTimeOffset Now,
    IReadOnlyList<PointBriefing>? Briefings = null,
    AltitudeProfile? Altitudes = null,
    IReadOnlyList<string>? AlternateLines = null,
    SigmetReport? Sigmets = null,
    string? SigmetNote = null);

/// <summary>Construit la feuille de route (A4) : tableau de navigation, relief, terrains, météo, zones et balises.</summary>
public static class FlightSheetBuilder
{
    private static CultureInfo Fr => VFRNav.Core.Localization.Loc.FormatCulture;
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(11, 42, 85));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(228, 236, 248));
    private static readonly Brush WarningFill = new SolidColorBrush(Color.FromRgb(253, 226, 226));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(95, 105, 120));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(170, 180, 195));

    public static string Disclaimer => Loc.Get("FlightSheetBuilder_Document_de_preparation_pour_la")
        + Loc.Get("FlightSheetBuilder_pour_un_vol_reel_utilise");

    public static FlowDocument Build(FlightSheetInput input)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            PagePadding = new Thickness(40),
            ColumnWidth = 720,
            Background = Brushes.White
        };

        var first = input.Route[0];
        var last = input.Route[^1];
        string from = first.Icao ?? first.Name, to = last.Icao ?? last.Name;
        var p = input.Parameters;

        doc.Blocks.Add(Heading(Loc.F("FlightSheetBuilder_Feuille_de_route", from, to), 20, Accent));
        doc.Blocks.Add(Small(Loc.F("FlightSheetBuilder_Preparee_le_UTC_heure_locale", input.Now.UtcDateTime.ToString("dd/MM/yyyy HH:mm", Fr), input.Now.LocalDateTime.ToString("HH:mm", Fr))));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Pilote_Avion_immat_Depart_prevu")));

        // --- Paramètres et totaux
        doc.Blocks.Add(Section("Vol"));
        double reserveL = 0.5 * p.BurnLph;
        doc.Blocks.Add(Facts(
            (Loc.Get("FlightSheetBuilder_Distance"), $"{input.Log.TotalDistanceNm.ToString("0.#", Fr)} NM"),
            (Loc.Get("FlightSheetBuilder_Temps_estime"), NavMath.FormatTime(input.Log.TotalMinutes)),
            (Loc.Get("FlightSheetBuilder_Carburant_en_croisiere"), $"{input.Log.TotalFuelL.ToString("0.#", Fr)} L"),
            (Loc.Get("FlightSheetBuilder_Avec_reserve_30_min_indicatif"), $"{(input.Log.TotalFuelL + reserveL).ToString("0.#", Fr)} L"),
            (Loc.Get("FlightSheetBuilder_Vitesse_propre_saisie"), $"{p.TasKt:0} kt"),
            (Loc.Get("FlightSheetBuilder_Consommation_saisie"), $"{p.BurnLph.ToString("0.#", Fr)} L/h"),
            (Loc.Get("FlightSheetBuilder_Altitude_de_croisiere"), $"{p.CruiseAltFt.ToString("N0", Fr)} ft"),
            (Loc.Get("FlightSheetBuilder_Vent_suppose"), $"{p.WindDir:000}° / {p.WindKt:0} kt"),
            (Loc.Get("FlightSheetBuilder_Declinaison_magnetique"), $"{p.VariationEast.ToString("0.#", Fr)}° E")));

        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Le_carburant_est_calcule_avec")
            + Loc.Get("FlightSheetBuilder_verifie_les_dans_son_manuel")));

        // --- Tableau de navigation
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Navigation")));
        var navHeaders = new[] { "N°", "De → À", Loc.Get("FlightSheetBuilder_Dist_NM"), "Rv", "Rm", Loc.Get("Ui_Derive"), "Cm", "GS\nkt", Loc.Get("Ui_Temps"), Loc.Get("FlightSheetBuilder_Carb_L"), Loc.Get("FlightSheetBuilder_Heure_prevue"), Loc.Get("FlightSheetBuilder_Heure_reelle") };
        var navRows = new List<string[]>();
        var navStarts = new List<double>();
        for (int i = 0; i < input.Log.Legs.Count; i++)
        {
            var l = input.Log.Legs[i];
            navRows.Add([(i + 1).ToString(Fr), $"{l.From} → {l.To}", l.DistanceNm.ToString("0.0", Fr), $"{l.TrueCourse:000}°", $"{l.MagneticCourse:000}°",
                $"{l.WindCorrection:+0;-0;0}°", $"{l.MagneticHeading:000}°", $"{l.GroundSpeedKt:0}", l.Time, l.FuelL.ToString("0.0", Fr), "", ""]);
        }
        navRows.Add(["", Loc.Get("FlightSheetBuilder_Total"), input.Log.TotalDistanceNm.ToString("0.0", Fr), "", "", "", "", "", NavMath.FormatTime(input.Log.TotalMinutes),
            input.Log.TotalFuelL.ToString("0.0", Fr), "", ""]);
        doc.Blocks.Add(MakeTable(navHeaders, navRows, [0.4, 2.6, 0.8, 0.8, 0.8, 0.8, 0.8, 0.7, 1, 0.8, 1, 1], rightFrom: 2, boldLast: true));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Rv_route_vraie_Rm_route")));

        // --- Aéroports de dégagement
        if (input.AlternateLines is { Count: > 0 } alternates)
        {
            doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Aeroports_de_degagement")));
            foreach (string line in alternates) doc.Blocks.Add(Plain(line));
            doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Depuis_le_terrain_d_arrivee")));
        }

        // --- Points de navigation et notes
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Points_de_navigation_et_notes")));
        var pointRows = input.Route.Select((w, i) => new[]
        {
            (i + 1).ToString(Fr),
            w.Icao ?? w.Name,
            $"{w.Lat.ToString("0.0000", Fr)}, {w.Lon.ToString("0.0000", Fr)}",
            w.IsAirport && w.ElevationFt > 0 ? w.ElevationFt.ToString("N0", Fr) + " ft" : "",
            input.Briefings is { } bs && i < bs.Count ? bs[i].AltitudeFt.ToString("N0", Fr) + " ft" : (w.AltitudeFt ?? p.CruiseAltFt).ToString("N0", Fr) + " ft",
            w.Notes?.Trim() ?? ""
        }).ToList();
        doc.Blocks.Add(MakeTable(["N°", Loc.Get("FlightSheetBuilder_Point"), Loc.Get("FlightSheetBuilder_Position"), Loc.Get("FlightSheetBuilder_Elev"), Loc.Get("FlightSheetBuilder_Altitude_prevue"), Loc.Get("FlightSheetBuilder_Notes")], pointRows, [0.4, 1.3, 2.1, 0.9, 1.1, 4.6], rightFrom: 99));

        // --- Survol, autorisations et fréquences, point par point
        if (input.Briefings is { Count: > 0 } briefings)
        {
            doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Survol_autorisations_et_frequences_par")));
            AddBriefings(doc, briefings);
        }

        // --- Relief
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Relief_sous_la_route")));
        AddTerrain(doc, input);

        // --- Obstacles
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Obstacles_pres_de_la_route")));
        AddObstacles(doc, input);

        // --- Terrains
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Terrains")));
        foreach (var brief in input.Airports) AddAirport(doc, brief);
        if (input.WeatherNote is not null) doc.Blocks.Add(Small(input.WeatherNote));

        // --- SIGMET
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_SIGMET_sur_la_route")));
        AddSigmets(doc, input);

        // --- Zones
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Espaces_aeriens_traverses")));
        AddCrossings(doc, input);

        // --- Balises
        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Balises_VOR_et_NDB_ADF")));
        if (input.Navaids.Count == 0) doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Aucune_balise_a_proximite_ou")));
        else
        {
            var rows = input.Navaids.Select(n => new[]
            {
                n.AlongNm.ToString("0", Fr), n.Navaid.Ident, n.Navaid.Name, n.Navaid.Type, n.Navaid.FrequencyText, n.OffsetNm.ToString("0.0", Fr)
            }).ToList();
            doc.Blocks.Add(MakeTable([Loc.Get("FlightSheetBuilder_NM_depart"), Loc.Get("FlightSheetBuilder_Code"), "Nom", Loc.Get("FlightSheetBuilder_Type"), Loc.Get("FlightSheetBuilder_Frequence"), Loc.Get("FlightSheetBuilder_Ecart_NM")], rows, [0.8, 0.8, 2.6, 1.2, 1.4, 0.8], rightFrom: 99));
        }

        // --- NOTAM et sources
        doc.Blocks.Add(Section("NOTAM"));
        var codes = string.Join(" ", input.Airports.Select(a => a.Waypoint.Icao).Where(c => c is not null).Distinct());
        doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Non_inclus_dans_ce_document")
            + (codes.Length > 0 ? Loc.F("FlightSheetBuilder_pour", codes) : ".")));

        doc.Blocks.Add(Section(Loc.Get("FlightSheetBuilder_Sources")));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Terrains_et_balises_OurAirports_domaine")
            + Loc.Get("FlightSheetBuilder_Espaces_aeriens_OpenAIP_Meteo_et") + TerrainService.Attribution + " " + ObstacleService.Attribution));
        var note = Small(Disclaimer);
        note.FontStyle = FontStyles.Italic;
        doc.Blocks.Add(note);
        return doc;
    }

    // ---------- relief ----------

    private static void AddTerrain(FlowDocument doc, FlightSheetInput input)
    {
        if (input.Terrain is null)
        {
            doc.Blocks.Add(Plain(input.TerrainNote ?? Loc.Get("FlightSheetBuilder_Relief_non_calcule")));
            return;
        }
        var t = input.Terrain;
        double cruise = input.Parameters.CruiseAltFt;
        var high = t.Highest!;
        doc.Blocks.Add(Plain(Loc.F("FlightSheetBuilder_Point_le_plus_haut_ft", high.ElevationFt.ToString("N0", Fr), high.DistanceNm.ToString("0", Fr))
            + Loc.F("FlightSheetBuilder_Altitude_de_croisiere_ft_soit", cruise.ToString("N0", Fr), (cruise - high.ElevationFt).ToString("N0", Fr))));

        if (input.ProfileImage is not null)
            doc.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Image { Source = input.ProfileImage, Stretch = Stretch.Uniform, MaxWidth = 700, Margin = new Thickness(0, 4, 0, 4) }));

        var warnings = t.Warnings(cruise).ToDictionary(w => w.LegIndex);
        var rows = new List<string[]>();
        for (int i = 0; i < input.Log.Legs.Count; i++)
        {
            double? max = t.LegMaxFt(i);
            string margin = max is null ? "" : (cruise - max.Value).ToString("N0", Fr) + " ft";
            string verdict = !warnings.TryGetValue(i, out var w) ? "" : w.ClearanceFt < 0 ? Loc.Get("FlightSheetBuilder_Relief_plus_haut_que_la") : Loc.Get("FlightSheetBuilder_Marge_inferieure_a_1_000");
            rows.Add([input.Log.Legs[i].Route, max is null ? "" : max.Value.ToString("N0", Fr) + " ft", margin, verdict]);
        }
        doc.Blocks.Add(MakeTable([Loc.Get("FlightSheetBuilder_Troncon"), Loc.Get("FlightSheetBuilder_Point_le_plus_haut"), Loc.Get("FlightSheetBuilder_Marge_sous_la_croisiere"), Loc.Get("FlightSheetBuilder_Alerte")], rows, [3, 1.5, 1.7, 2.6], rightFrom: 99,
            highlight: i => warnings.ContainsKey(i)));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Le_relief_vient_d_un")
            + Loc.Get("FlightSheetBuilder_Garde_une_marge_confortable_au")));
    }

    // ---------- obstacles ----------

    private static void AddObstacles(FlowDocument doc, FlightSheetInput input)
    {
        if (input.ObstacleNote is not null) { doc.Blocks.Add(Plain(input.ObstacleNote)); return; }

        double cruise = input.Parameters.CruiseAltFt;
        var all = input.Obstacles;
        var known = all.Where(o => o.TopFt is not null).ToList();
        int unknown = all.Count - known.Count;
        if (all.Count == 0)
        {
            doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Aucun_mat_tour_cheminee_ni")));
        }
        else
        {
            string highest = known.Count > 0 ? Loc.F("FlightSheetBuilder_Sommet_le_plus_haut_ft", known.Max(o => o.TopFt!.Value).ToString("N0", Fr)) : "";
            doc.Blocks.Add(Plain(Loc.F("FlightSheetBuilder_obstacle_s_repertorie_s_dans", all.Count, known.Count, unknown, highest)));
        }

        var relevant = known.Where(o => cruise - o.TopFt!.Value < ProfileChart.ObstacleRelevantMarginFt).OrderBy(o => o.AlongNm).ToList();
        if (relevant.Count > 0)
        {
            var rows = relevant.Select(o => new[]
            {
                o.AlongNm.ToString("0.0", Fr),
                o.Obstacle.KindText + (o.Obstacle.Name.Length > 0 ? " · " + o.Obstacle.Name : ""),
                o.Obstacle.HeightM!.Value.ToString("0", Fr) + " m",
                o.TopFt!.Value.ToString("N0", Fr) + " ft",
                o.OffsetNm.ToString("0.0", Fr),
                o.MarginFt(cruise)!.Value.ToString("N0", Fr) + " ft"
            }).ToList();
            doc.Blocks.Add(MakeTable([Loc.Get("FlightSheetBuilder_NM_depart"), Loc.Get("FlightSheetBuilder_Obstacle"), Loc.Get("FlightSheetBuilder_Hauteur"), Loc.Get("FlightSheetBuilder_Sommet_altitude"), Loc.Get("FlightSheetBuilder_Ecart_route_NM"), Loc.Get("FlightSheetBuilder_Marge_sous_la_croisiere_2")], rows,
                [0.9, 3.4, 1, 1.3, 1.2, 1.4], rightFrom: 2, highlight: i => relevant[i].MarginFt(cruise)!.Value < 500));
            doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Obstacles_dont_le_sommet_est")));
        }
        else if (known.Count > 0)
            doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Aucun_obstacle_de_hauteur_connue")));

        if (unknown > 0)
            doc.Blocks.Add(Small(Loc.F("FlightSheetBuilder_obstacle_s_n_ont_pas", unknown)));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Donnees_collaboratives_incompletes_les_li")
            + Loc.Get("FlightSheetBuilder_L_absence_d_un_obstacle")));
    }

    // ---------- terrains ----------

    private static void AddAirport(FlowDocument doc, AirportBrief b)
    {
        var wp = b.Waypoint;
        var info = b.Info;
        string title = $"{wp.Icao} — {info?.Name ?? wp.FullName ?? wp.Name}";
        doc.Blocks.Add(Heading(title, 13, Accent, top: 8));

        if (info is not null)
        {
            var facts = new List<string>();
            if (info.ElevationFt is { } e) facts.Add(Loc.F("FlightSheetBuilder_Elevation_ft", e.ToString("N0", Fr)));
            if (info.MagneticVariation is { } v) facts.Add(Loc.F("FlightSheetBuilder_Declinaison_E", v.ToString("0.#", Fr)));
            facts.Add(info.HasChart ? Loc.Get("FlightSheetBuilder_Carte_VAC_disponible") : info.VacListed ? Loc.Get("FlightSheetBuilder_Carte_VAC_publiee_absente_du") : Loc.Get("FlightSheetBuilder_Pas_de_carte_VAC"));
            doc.Blocks.Add(Plain(string.Join("   ·   ", facts)));
            if (info.Frequencies.Count > 0)
                doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Frequences") + string.Join("   ", info.Frequencies.Select(f => $"{f.Service} {f.Mhz.ToString("0.000", Fr)}"))));
            foreach (var r in info.Runways)
            {
                string qfu = r.Qfu.Count > 0 ? " (QFU " + string.Join(" / ", r.Qfu.Select(q => q.ToString("000"))) + ")" : "";
                string size = r.LengthMetres is { } l ? $", {l} × {r.WidthMetres?.ToString(Fr) ?? "?"} m" : "";
                doc.Blocks.Add(Plain(Loc.F("FlightSheetBuilder_Piste", r.Id, qfu, size, (r.Surface is not null ? ", " + r.Surface : ""))));
            }
        }
        if (info?.Procedures is { Any: true } procedures) AddProcedures(doc, procedures);

        if (info is null && wp.ElevationFt > 0) doc.Blocks.Add(Plain(Loc.F("FlightSheetBuilder_Elevation_ft_aucune_fiche_d", wp.ElevationFt.ToString("N0", Fr))));

        AddReport(doc, b.Metar, "METAR (observation)");
        AddReport(doc, b.Taf, Loc.Get("FlightSheetBuilder_TAF_prevision"));
        if (b.Metar is null && b.Taf is null) doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Aucun_METAR_ni_TAF_pour")));
    }

    /// <summary>Points de report, circuit et consignes d'arrivée et de départ (texte du profil VFR).</summary>
    private static void AddProcedures(FlowDocument doc, VfrProcedures procedures)
    {
        doc.Blocks.Add(new Paragraph(new Run(Loc.Get("FlightSheetBuilder_Procedures_VFR_du_terrain")) { FontWeight = FontWeights.SemiBold }) { Margin = new Thickness(0, 4, 0, 1) });
        foreach (string line in procedures.ToLines())
        {
            int indent = line.Length - line.TrimStart().Length;
            bool title = indent == 0;
            doc.Blocks.Add(new Paragraph(new Run(line.TrimStart()) { FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal })
            {
                FontSize = 10,
                Margin = new Thickness(10 + indent * 6, title ? 3 : 0, 0, 0),
                Foreground = Ink
            });
        }
        doc.Blocks.Add(Small(VfrProcedures.Source));
    }

    private static void AddReport(FlowDocument doc, DecodedReport? r, string title)
    {
        if (r is null) return;
        int dash = r.Header.IndexOf('—');
        string when = dash >= 0 ? " — " + r.Header[(dash + 1)..].Trim() : "";
        var heading = new Paragraph(new Run(title + when) { FontWeight = FontWeights.SemiBold }) { Margin = new Thickness(0, 4, 0, 1) };
        doc.Blocks.Add(heading);
        foreach (string line in r.Lines.Take(r.Kind == "TAF" ? 16 : 14))
        {
            var para = Plain((line.StartsWith(' ') ? "     " + line.TrimStart() : line.EndsWith(':') ? line : "• " + line));
            para.Margin = new Thickness(10, 0, 0, 0);
            para.FontSize = 10.5;
            doc.Blocks.Add(para);
        }
        if (r.Lines.Count > 16 && r.Kind == "TAF") doc.Blocks.Add(Small(Loc.F("FlightSheetBuilder_lignes_de_plus_dans_l", r.Lines.Count - 16)));
    }

    // ---------- survol et autorisations par point ----------

    private static void AddBriefings(FlowDocument doc, IReadOnlyList<PointBriefing> briefings)
    {
        foreach (var b in briefings)
        {
            doc.Blocks.Add(Heading($"{b.Index + 1}. {b.Name} — {b.AltitudeFt.ToString("N0", Fr)} ft ({b.AltitudeSource})", 11.5, Accent, top: 6));
            var rows = b.Zones.Select(z => new[]
            {
                $"{PointBriefings.Mark(z.Severity)} {z.Zone.Name}",
                Airspace.TypeName(z.Zone.TypeCode) + (z.Zone.IcaoClass.Length > 0 ? $" · cl. {z.Zone.IcaoClass}" : "") + $"\n{z.Zone.Lower} → {z.Zone.Upper}",
                z.Verdict,
                z.Action
            }).ToList();
            if (rows.Count == 0) doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Aucune_zone_connue_a_cette")));
            else
            {
                var strict = b.Zones.Select((z, i) => (z, i)).Where(t => t.z.Status == VerticalStatus.Inside && t.z.Severity >= Severity.Clearance).Select(t => t.i).ToHashSet();
                doc.Blocks.Add(MakeTable([Loc.Get("FlightSheetBuilder_Zone"), Loc.Get("FlightSheetBuilder_Type_et_limites"), Loc.Get("FlightSheetBuilder_Ce_que_ca_implique"), Loc.Get("FlightSheetBuilder_A_faire")], rows, [2.2, 2.2, 2.4, 4.4], rightFrom: 99, highlight: strict.Contains));
            }
            if (b.Frequencies.Count > 0)
            {
                var line = Plain(Loc.Get("FlightSheetBuilder_Frequences_a_contacter") + string.Join("   ", b.Frequencies));
                line.FontWeight = FontWeights.SemiBold;
                doc.Blocks.Add(line);
            }
        }
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Fond_rose_clairance_obligatoire_ou")
            + Loc.Get("FlightSheetBuilder_verifie_l_activation_des_zones")));
    }

    // ---------- zones ----------

    private static void AddSigmets(FlowDocument doc, FlightSheetInput input)
    {
        if (input.Sigmets is not { } report)
        {
            doc.Blocks.Add(Plain(input.SigmetNote ?? Loc.Get("FlightSheetBuilder_SIGMET_non_verifies")));
            doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_Consulte_les_SIGMET_sur_une")));
            return;
        }

        if (report.OnRoute.Count == 0)
        {
            doc.Blocks.Add(Plain(Loc.F("FlightSheetBuilder_Aucun_SIGMET_en_vigueur_pendant", report.ActiveCount.ToString("N0", Fr))));
        }
        else
        {
            var rows = report.OnRoute.Select(r => new[]
            {
                $"{r.EntryNm.ToString("0", Fr)} → {r.ExitNm.ToString("0", Fr)}",
                r.Sigmet.Description,
                $"{r.Sigmet.FirId} · {r.Sigmet.SeriesId}",
                $"{SigmetLevel(r.Sigmet.BaseFt, "SFC")} → {SigmetLevel(r.Sigmet.TopFt, Loc.Get("FlightSheetBuilder_illimite"))}",
                SigmetValidity(r.Sigmet, input.Now),
                r.Status switch
                {
                    VerticalStatus.Inside => Loc.Get("FlightSheet_InZone"),
                    VerticalStatus.Above => Loc.Get("FlightSheetBuilder_au_dessus"),
                    _ => Loc.Get("FlightSheetBuilder_sous_le_plancher")
                }
            }).ToList();
            var inside = report.OnRoute.Select((r, i) => (r, i)).Where(t => t.r.Status == VerticalStatus.Inside).Select(t => t.i).ToHashSet();
            doc.Blocks.Add(MakeTable(["NM", Loc.Get("FlightSheetBuilder_Phenomene"), Loc.Get("FlightSheetBuilder_FIR_serie"), Loc.Get("FlightSheetBuilder_Niveaux"), Loc.Get("FlightSheetBuilder_Validite_UTC"), Loc.Get("FlightSheetBuilder_A_ton_altitude")], rows, [1.2, 3, 1.6, 2, 2, 1.7], rightFrom: 99,
                highlight: inside.Contains));

            foreach (var r in report.OnRoute)
                if (r.Sigmet.RawText is { Length: > 0 } raw)
                    doc.Blocks.Add(Small($"{r.Sigmet.FirId} {r.Sigmet.SeriesId} : " + SigmetText(raw)));
        }

        doc.Blocks.Add(Small(Loc.F("FlightSheetBuilder_SIGMET_consultes_le_UTC_aviationweather", report.At.UtcDateTime.ToString("dd/MM/yyyy HH:mm", Fr))
            + Loc.Get("FlightSheetBuilder_Le_vol_est_suppose_commencer")
            + Loc.Get("FlightSheetBuilder_DANS_LA_ZONE_la_route")
            + Loc.Get("FlightSheetBuilder_Un_SIGMET_n_est_publie")));
    }

    private static string SigmetLevel(double? feet, string missing) => SigmetPresentation.LevelText(feet, missing);

    private static string SigmetValidity(Sigmet s, DateTimeOffset now)
    {
        string Time(DateTimeOffset t) => t.UtcDateTime.Date == now.UtcDateTime.Date ? t.UtcDateTime.ToString("HH:mm", Fr) : t.UtcDateTime.ToString("dd/MM HH:mm", Fr);
        return $"{Time(s.ValidFrom)} → {Time(s.ValidTo)}";
    }

    private static string SigmetText(string raw) => SigmetPresentation.RawOneLine(raw, 320);

    private static void AddCrossings(FlowDocument doc, FlightSheetInput input)
    {
        if (input.Crossings.Count == 0)
        {
            doc.Blocks.Add(Plain(Loc.Get("FlightSheetBuilder_Aucune_zone_connue_sur_la")));
            return;
        }
        var rows = input.Crossings.Select(c => new[]
        {
            $"{c.EntryNm.ToString("0", Fr)} → {c.ExitNm.ToString("0", Fr)}",
            c.Zone.Name,
            Airspace.TypeName(c.Zone.TypeCode) + (c.Zone.IcaoClass.Length > 0 ? Loc.F("FlightSheet_Class_short", c.Zone.IcaoClass) : ""),
            $"{c.Zone.Lower} → {c.Zone.Upper}",
            c.Status switch
            {
                VerticalStatus.Inside when c.Zone.IcaoClass == "A" => Loc.Get("FlightSheet_InZone_ClassA"),
                VerticalStatus.Inside => "DANS LA ZONE",
                VerticalStatus.Above => Loc.Get("FlightSheetBuilder_au_dessus"),
                _ => Loc.Get("FlightSheetBuilder_sous_le_plancher")
            },
            c.Zone.Details?.Frequencies is { Count: > 0 } f ? string.Join(" ", f.Select(x => x.Value)) : ""
        }).ToList();
        var inside = input.Crossings.Select((c, i) => (c, i)).Where(t => t.c.Status == VerticalStatus.Inside).Select(t => t.i).ToHashSet();
        doc.Blocks.Add(MakeTable(["NM", Loc.Get("FlightSheetBuilder_Zone"), Loc.Get("FlightSheetBuilder_Type"), Loc.Get("FlightSheetBuilder_Limites"), Loc.Get("FlightSheetBuilder_A_ton_altitude"), Loc.Get("FlightSheetBuilder_Frequence")], rows, [1, 2.4, 2.1, 2.1, 1.9, 1.2], rightFrom: 99,
            highlight: inside.Contains));
        doc.Blocks.Add(Small(Loc.Get("FlightSheetBuilder_DANS_LA_ZONE_tu_y")
            + Loc.Get("FlightSheetBuilder_En_classe_A_le_vol")
            + Loc.Get("FlightSheetBuilder_Comparaison_faite_sans_tenir_compte")));
    }

    // ---------- éléments de mise en page ----------

    private static Paragraph Heading(string text, double size, Brush brush, double top = 0) =>
        new(new Run(text)) { FontSize = size, FontWeight = FontWeights.Bold, Foreground = brush, Margin = new Thickness(0, top, 0, 2) };

    private static Paragraph Section(string text) =>
        new(new Run(text.ToUpper(Fr))) { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Accent, Margin = new Thickness(0, 14, 0, 4), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1) };

    private static Paragraph Plain(string text) => new(new Run(text)) { Margin = new Thickness(0, 1, 0, 1), Foreground = Ink };

    private static Paragraph Small(string text) => new(new Run(text)) { FontSize = 9.5, Foreground = Grey, Margin = new Thickness(0, 2, 0, 2) };

    private static Table Facts(params (string Label, string Value)[] items)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 2) };
        for (int i = 0; i < 3; i++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int i = 0; i < items.Length; i += 3)
        {
            var row = new TableRow();
            for (int j = i; j < i + 3; j++)
            {
                var cell = new TableCell { Padding = new Thickness(0, 2, 8, 2) };
                if (j < items.Length)
                {
                    var para = new Paragraph { Margin = new Thickness(0) };
                    para.Inlines.Add(new Run(items[j].Label + " : ") { Foreground = Grey, FontSize = 10 });
                    para.Inlines.Add(new Run(items[j].Value) { FontWeight = FontWeights.SemiBold });
                    cell.Blocks.Add(para);
                }
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    /// <summary>Tableau à bordures fines ; les colonnes à partir de <paramref name="rightFrom"/> sont alignées à droite.</summary>
    private static Table MakeTable(string[] headers, List<string[]> rows, double[] widths, int rightFrom, bool boldLast = false, Func<int, bool>? highlight = null)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 2), BorderBrush = Line, BorderThickness = new Thickness(1, 1, 0, 0) };
        foreach (double w in widths) table.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        TableRow Row(string[] cells, Brush? fill, bool bold, double size)
        {
            var row = new TableRow { Background = fill };
            for (int i = 0; i < widths.Length; i++)
            {
                var para = new Paragraph(new Run(i < cells.Length ? cells[i] : "")) { Margin = new Thickness(0), FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                if (i >= rightFrom) para.TextAlignment = TextAlignment.Right;
                row.Cells.Add(new TableCell(para) { Padding = new Thickness(3, 2, 3, 2), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 1) });
            }
            return row;
        }

        group.Rows.Add(Row(headers, HeaderFill, true, 9.5));
        for (int i = 0; i < rows.Count; i++)
        {
            bool last = i == rows.Count - 1;
            group.Rows.Add(Row(rows[i], highlight is not null && highlight(i) ? WarningFill : last && boldLast ? HeaderFill : null, last && boldLast, 10.5));
        }
        return table;
    }
}
