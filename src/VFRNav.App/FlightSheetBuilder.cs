using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.StormAtc;
using VFRNav.Core.Weather;

namespace VFRNav.App;

/// <summary>Un terrain de la route avec ses infos StormATC et sa météo décodée.</summary>
public sealed record AirportBrief(Waypoint Waypoint, StormAtcAerodrome? Info, DecodedReport? Metar, DecodedReport? Taf);

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
    AltitudeProfile? Altitudes = null);

/// <summary>Construit la feuille de route (A4) : tableau de navigation, relief, terrains, météo, zones et balises.</summary>
public static class FlightSheetBuilder
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(11, 42, 85));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(228, 236, 248));
    private static readonly Brush WarningFill = new SolidColorBrush(Color.FromRgb(253, 226, 226));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(95, 105, 120));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(170, 180, 195));

    public const string Disclaimer = "Document de préparation pour la simulation de vol. Il ne remplace ni l'AIP, ni les NOTAM, ni la météo officielle : "
        + "pour un vol réel, utilise les sources officielles (SIA, Météo-France) et vérifie tout.";

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

        doc.Blocks.Add(Heading($"Feuille de route — {from} → {to}", 20, Accent));
        doc.Blocks.Add(Small($"Préparée le {input.Now.UtcDateTime.ToString("dd/MM/yyyy HH:mm", Fr)} UTC ({input.Now.LocalDateTime.ToString("HH:mm", Fr)} heure locale)"));
        doc.Blocks.Add(Small("Pilote : ____________________   Avion / immat. : ____________________   Départ prévu (UTC) : ________"));

        // --- Paramètres et totaux
        doc.Blocks.Add(Section("Vol"));
        double reserveL = 0.5 * p.BurnLph;
        doc.Blocks.Add(Facts(
            ("Distance", $"{input.Log.TotalDistanceNm.ToString("0.#", Fr)} NM"),
            ("Temps estimé", NavMath.FormatTime(input.Log.TotalMinutes)),
            ("Carburant en croisière", $"{input.Log.TotalFuelL.ToString("0.#", Fr)} L"),
            ("Avec réserve 30 min (indicatif)", $"{(input.Log.TotalFuelL + reserveL).ToString("0.#", Fr)} L"),
            ("Vitesse propre (saisie)", $"{p.TasKt:0} kt"),
            ("Consommation (saisie)", $"{p.BurnLph.ToString("0.#", Fr)} L/h"),
            ("Altitude de croisière", $"{p.CruiseAltFt.ToString("N0", Fr)} ft"),
            ("Vent supposé", $"{p.WindDir:000}° / {p.WindKt:0} kt"),
            ("Déclinaison magnétique", $"{p.VariationEast.ToString("0.#", Fr)}° E")));

        doc.Blocks.Add(Small("Le carburant est calculé avec la vitesse propre et la consommation saisies dans VFRNav, pas avec celles de ton avion : "
            + "vérifie-les dans son manuel de vol. Il ne comprend ni roulage, ni montée, ni tours de piste, ni approche : prévois une marge."));

        // --- Tableau de navigation
        doc.Blocks.Add(Section("Navigation"));
        var navHeaders = new[] { "N°", "De → À", "Dist\nNM", "Rv", "Rm", "Dérive", "Cm", "GS\nkt", "Temps", "Carb\nL", "Heure\nprévue", "Heure\nréelle" };
        var navRows = new List<string[]>();
        var navStarts = new List<double>();
        for (int i = 0; i < input.Log.Legs.Count; i++)
        {
            var l = input.Log.Legs[i];
            navRows.Add([(i + 1).ToString(Fr), $"{l.From} → {l.To}", l.DistanceNm.ToString("0.0", Fr), $"{l.TrueCourse:000}°", $"{l.MagneticCourse:000}°",
                $"{l.WindCorrection:+0;-0;0}°", $"{l.MagneticHeading:000}°", $"{l.GroundSpeedKt:0}", l.Time, l.FuelL.ToString("0.0", Fr), "", ""]);
        }
        navRows.Add(["", "Total", input.Log.TotalDistanceNm.ToString("0.0", Fr), "", "", "", "", "", NavMath.FormatTime(input.Log.TotalMinutes),
            input.Log.TotalFuelL.ToString("0.0", Fr), "", ""]);
        doc.Blocks.Add(MakeTable(navHeaders, navRows, [0.4, 2.6, 0.8, 0.8, 0.8, 0.8, 0.8, 0.7, 1, 0.8, 1, 1], rightFrom: 2, boldLast: true));
        doc.Blocks.Add(Small("Rv : route vraie · Rm : route magnétique · Cm : cap magnétique à tenir (vent supposé constant)."));

        // --- Points de navigation et notes
        doc.Blocks.Add(Section("Points de navigation et notes"));
        var pointRows = input.Route.Select((w, i) => new[]
        {
            (i + 1).ToString(Fr),
            w.Icao ?? w.Name,
            $"{w.Lat.ToString("0.0000", Fr)}, {w.Lon.ToString("0.0000", Fr)}",
            w.IsAirport && w.ElevationFt > 0 ? w.ElevationFt.ToString("N0", Fr) + " ft" : "",
            input.Briefings is { } bs && i < bs.Count ? bs[i].AltitudeFt.ToString("N0", Fr) + " ft" : (w.AltitudeFt ?? p.CruiseAltFt).ToString("N0", Fr) + " ft",
            w.Notes?.Trim() ?? ""
        }).ToList();
        doc.Blocks.Add(MakeTable(["N°", "Point", "Position", "Élév.", "Altitude\nprévue", "Notes"], pointRows, [0.4, 1.3, 2.1, 0.9, 1.1, 4.6], rightFrom: 99));

        // --- Survol, autorisations et fréquences, point par point
        if (input.Briefings is { Count: > 0 } briefings)
        {
            doc.Blocks.Add(Section("Survol, autorisations et fréquences par point"));
            AddBriefings(doc, briefings);
        }

        // --- Relief
        doc.Blocks.Add(Section("Relief sous la route"));
        AddTerrain(doc, input);

        // --- Obstacles
        doc.Blocks.Add(Section("Obstacles près de la route (couloir de 2 NM de chaque côté)"));
        AddObstacles(doc, input);

        // --- Terrains
        doc.Blocks.Add(Section("Terrains"));
        foreach (var brief in input.Airports) AddAirport(doc, brief);
        if (input.WeatherNote is not null) doc.Blocks.Add(Small(input.WeatherNote));

        // --- Zones
        doc.Blocks.Add(Section("Espaces aériens traversés"));
        AddCrossings(doc, input);

        // --- Balises
        doc.Blocks.Add(Section("Balises VOR et NDB (ADF) à moins de 15 NM de la route"));
        if (input.Navaids.Count == 0) doc.Blocks.Add(Small("Aucune balise à proximité (ou balises non chargées)."));
        else
        {
            var rows = input.Navaids.Select(n => new[]
            {
                n.AlongNm.ToString("0", Fr), n.Navaid.Ident, n.Navaid.Name, n.Navaid.Type, n.Navaid.FrequencyText, n.OffsetNm.ToString("0.0", Fr)
            }).ToList();
            doc.Blocks.Add(MakeTable(["NM\ndépart", "Code", "Nom", "Type", "Fréquence", "Écart\nNM"], rows, [0.8, 0.8, 2.6, 1.2, 1.4, 0.8], rightFrom: 99));
        }

        // --- NOTAM et sources
        doc.Blocks.Add(Section("NOTAM"));
        var codes = string.Join(" ", input.Airports.Select(a => a.Waypoint.Icao).Where(c => c is not null).Distinct());
        doc.Blocks.Add(Plain("Non inclus dans ce document. Avant un vol réel, consulte les NOTAM sur SOFIA-Briefing (SIA)"
            + (codes.Length > 0 ? $" pour : {codes}." : ".")));

        doc.Blocks.Add(Section("Sources"));
        doc.Blocks.Add(Small("Terrains et balises : OurAirports (domaine public). Aérodromes, profils et cartes VAC : StormATC (cartes © SIA). "
            + "Espaces aériens : OpenAIP. Météo : aviationweather.gov. " + TerrainService.Attribution + " " + ObstacleService.Attribution));
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
            doc.Blocks.Add(Plain(input.TerrainNote ?? "Relief non calculé."));
            return;
        }
        var t = input.Terrain;
        double cruise = input.Parameters.CruiseAltFt;
        var high = t.Highest!;
        doc.Blocks.Add(Plain($"Point le plus haut : {high.ElevationFt.ToString("N0", Fr)} ft à {high.DistanceNm.ToString("0", Fr)} NM du départ. "
            + $"Altitude de croisière : {cruise.ToString("N0", Fr)} ft, soit une marge de {(cruise - high.ElevationFt).ToString("N0", Fr)} ft au-dessus du point le plus haut."));

        if (input.ProfileImage is not null)
            doc.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Image { Source = input.ProfileImage, Stretch = Stretch.Uniform, MaxWidth = 700, Margin = new Thickness(0, 4, 0, 4) }));

        var warnings = t.Warnings(cruise).ToDictionary(w => w.LegIndex);
        var rows = new List<string[]>();
        for (int i = 0; i < input.Log.Legs.Count; i++)
        {
            double? max = t.LegMaxFt(i);
            string margin = max is null ? "" : (cruise - max.Value).ToString("N0", Fr) + " ft";
            string verdict = !warnings.TryGetValue(i, out var w) ? "" : w.ClearanceFt < 0 ? "Relief plus haut que la croisière" : "Marge inférieure à 1 000 ft";
            rows.Add([input.Log.Legs[i].Route, max is null ? "" : max.Value.ToString("N0", Fr) + " ft", margin, verdict]);
        }
        doc.Blocks.Add(MakeTable(["Tronçon", "Point le plus haut", "Marge sous la croisière", "Alerte"], rows, [3, 1.5, 1.7, 2.6], rightFrom: 99,
            highlight: i => warnings.ContainsKey(i)));
        doc.Blocks.Add(Small("Le relief vient d'un modèle numérique d'environ 90 m : il ne montre ni pylônes, ni antennes, ni autres obstacles. "
            + "Garde une marge confortable au-dessus du point le plus haut ; 1 000 ft est une valeur indicative."));
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
            doc.Blocks.Add(Plain("Aucun mât, tour, cheminée ni éolienne répertoriés dans le couloir (ou données non chargées)."));
        }
        else
        {
            string highest = known.Count > 0 ? $" Sommet le plus haut : {known.Max(o => o.TopFt!.Value).ToString("N0", Fr)} ft." : "";
            doc.Blocks.Add(Plain($"{all.Count} obstacle(s) répertorié(s) dans le couloir : {known.Count} avec une hauteur connue, {unknown} sans hauteur renseignée.{highest}"));
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
            doc.Blocks.Add(MakeTable(["NM\ndépart", "Obstacle", "Hauteur", "Sommet\n(altitude)", "Écart route\nNM", "Marge sous\nla croisière"], rows,
                [0.9, 3.4, 1, 1.3, 1.2, 1.4], rightFrom: 2, highlight: i => relevant[i].MarginFt(cruise)!.Value < 500));
            doc.Blocks.Add(Small("Obstacles dont le sommet est à moins de 1 500 ft sous ton altitude de croisière (ou au-dessus). En rouge : marge inférieure à 500 ft."));
        }
        else if (known.Count > 0)
            doc.Blocks.Add(Plain("Aucun obstacle de hauteur connue à moins de 1 500 ft sous ton altitude de croisière."));

        if (unknown > 0)
            doc.Blocks.Add(Small($"{unknown} obstacle(s) n'ont pas de hauteur dans OpenStreetMap : ils ne sont pas évalués. Reste prudent près de ces points."));
        doc.Blocks.Add(Small("Données collaboratives, incomplètes : les lignes électriques, les câbles et beaucoup d'ouvrages n'y figurent pas. "
            + "L'absence d'un obstacle ici ne prouve pas qu'il n'existe pas."));
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
            if (info.ElevationFt is { } e) facts.Add($"Élévation {e.ToString("N0", Fr)} ft");
            if (info.MagneticVariation is { } v) facts.Add($"Déclinaison {v.ToString("0.#", Fr)}° E");
            facts.Add(info.HasChart ? "Carte VAC disponible" : info.VacListed ? "Carte VAC publiée (absente du dossier)" : "Pas de carte VAC");
            doc.Blocks.Add(Plain(string.Join("   ·   ", facts)));
            if (info.Frequencies.Count > 0)
                doc.Blocks.Add(Plain("Fréquences : " + string.Join("   ", info.Frequencies.Select(f => $"{f.Service} {f.Mhz.ToString("0.000", Fr)}"))));
            foreach (var r in info.Runways)
            {
                string qfu = r.Qfu.Count > 0 ? " (QFU " + string.Join(" / ", r.Qfu.Select(q => q.ToString("000"))) + ")" : "";
                string size = r.LengthMetres is { } l ? $", {l} × {r.WidthMetres?.ToString(Fr) ?? "?"} m" : "";
                doc.Blocks.Add(Plain($"Piste {r.Id}{qfu}{size}{(r.Surface is not null ? ", " + r.Surface : "")}"));
            }
        }
        if (info?.Procedures is { Any: true } procedures) AddProcedures(doc, procedures);

        if (info is null && wp.ElevationFt > 0) doc.Blocks.Add(Plain($"Élévation {wp.ElevationFt.ToString("N0", Fr)} ft (aucune fiche StormATC pour ce terrain)"));

        AddReport(doc, b.Metar, "METAR (observation)");
        AddReport(doc, b.Taf, "TAF (prévision)");
        if (b.Metar is null && b.Taf is null) doc.Blocks.Add(Small("Aucun METAR ni TAF pour ce terrain."));
    }

    /// <summary>Points de report, circuit et consignes d'arrivée et de départ (texte du profil StormATC).</summary>
    private static void AddProcedures(FlowDocument doc, VfrProcedures procedures)
    {
        doc.Blocks.Add(new Paragraph(new Run("Procédures VFR du terrain") { FontWeight = FontWeights.SemiBold }) { Margin = new Thickness(0, 4, 0, 1) });
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
        if (r.Lines.Count > 16 && r.Kind == "TAF") doc.Blocks.Add(Small($"… {r.Lines.Count - 16} lignes de plus dans l'onglet METAR / TAF."));
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
            if (rows.Count == 0) doc.Blocks.Add(Plain("Aucune zone connue à cette altitude : pas de clairance ni de radio demandées par les zones chargées."));
            else
            {
                var strict = b.Zones.Select((z, i) => (z, i)).Where(t => t.z.Status == VerticalStatus.Inside && t.z.Severity >= Severity.Clearance).Select(t => t.i).ToHashSet();
                doc.Blocks.Add(MakeTable(["Zone", "Type et limites", "Ce que ça implique", "À faire"], rows, [2.2, 2.2, 2.4, 4.4], rightFrom: 99, highlight: strict.Contains));
            }
            if (b.Frequencies.Count > 0)
            {
                var line = Plain("Fréquences à contacter : " + string.Join("   ", b.Frequencies));
                line.FontWeight = FontWeights.SemiBold;
                doc.Blocks.Add(line);
            }
        }
        doc.Blocks.Add(Small("Fond rose : clairance obligatoire ou vol à vue interdit à cette altitude. Texte général pour la simulation : pour un vol réel, "
            + "vérifie l'activation des zones (NOTAM, SIA) et les conditions d'accès publiées. Comparaison faite sans tenir compte du relief."));
    }

    // ---------- zones ----------

    private static void AddCrossings(FlowDocument doc, FlightSheetInput input)
    {
        if (input.Crossings.Count == 0)
        {
            doc.Blocks.Add(Plain("Aucune zone connue sur la route à ton altitude (ou zones non chargées : saisis une clé OpenAIP)."));
            return;
        }
        var rows = input.Crossings.Select(c => new[]
        {
            $"{c.EntryNm.ToString("0", Fr)} → {c.ExitNm.ToString("0", Fr)}",
            c.Zone.Name,
            Airspace.TypeName(c.Zone.TypeCode) + (c.Zone.IcaoClass.Length > 0 ? $" · cl. {c.Zone.IcaoClass}" : ""),
            $"{c.Zone.Lower} → {c.Zone.Upper}",
            c.Status switch
            {
                VerticalStatus.Inside when c.Zone.IcaoClass == "A" => "DANS LA ZONE · classe A : VFR interdit",
                VerticalStatus.Inside => "DANS LA ZONE",
                VerticalStatus.Above => "au-dessus",
                _ => "sous le plancher"
            },
            c.Zone.Details?.Frequencies is { Count: > 0 } f ? string.Join(" ", f.Select(x => x.Value)) : ""
        }).ToList();
        var inside = input.Crossings.Select((c, i) => (c, i)).Where(t => t.c.Status == VerticalStatus.Inside).Select(t => t.i).ToHashSet();
        doc.Blocks.Add(MakeTable(["NM", "Zone", "Type", "Limites", "À ton altitude", "Fréquence"], rows, [1, 2.4, 2.1, 2.1, 1.9, 1.2], rightFrom: 99,
            highlight: inside.Contains));
        doc.Blocks.Add(Small("« DANS LA ZONE » : tu y voles à ton altitude prévue — vérifie classe, radio et clairance avant le départ. "
            + "En classe A, le vol à vue est interdit : change d'altitude ou de route. "
            + "Comparaison faite sans tenir compte du relief."));
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
