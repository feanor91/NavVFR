using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using VFRNav.Core.Ifr;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Route IFR : choix du départ, de l'arrivée, des procédures, puis calcul sur la base de navigation ----------
public partial class MainWindow
{
    private static string AutoChoice => Loc.Get("Ifr_Auto");
    private static string NoneChoice => Loc.Get("Ifr_Aucune");

    // Pour l'instant : données de démonstration. Un lecteur AIRAC les remplacera.
    private IfrNavData _ifrData = IfrSampleData.Create();

    /// <summary>ILS de l'export du SIA chargé (France) : localizer, pente et seuil de chaque piste.</summary>
    private IlsCatalog _ils = IlsCatalog.Empty;

    private async void Ifr_Loaded(object sender, RoutedEventArgs e)
    {
        IfrIlsCheck.IsChecked = _settings.IfrIlsApproach;
        await LoadWorldNetworkAsync();
        // Données AIRAC déjà choisies lors d'une session précédente : rechargées sans rien demander.
        if (_settings.IfrDataPath.Length > 0 && File.Exists(_settings.IfrDataPath))
            await LoadIfrDataAsync(_settings.IfrDataPath);
        else
        {
            await ApplyWorldNetworkAsync();
            ShowIfrDataLabel();
        }
        RefreshIfrChoices();
    }

    private void ShowIfrDataLabel()
    {
        if (_ifrData.AiracCycle == "TEST")
        {
            IfrDataLabel.Text = IfrSampleData.Label + Loc.Get("Ifr_ne_pas_utiliser_pour_naviguer") + ProceduresNote() + NetworkNote();
            return;
        }
        bool expired = _ifrData.IsExpired(DateTime.UtcNow);
        IfrDataLabel.Foreground = expired ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.LightGreen;
        string procedures = _ifrData.Procedures.Count == 0
            ? Loc.Get("Ifr_Pas_de_SID_STAR_dans")
            : "";
        IfrDataLabel.Text = $"AIRAC {_ifrData.AiracCycle}" + (expired ? Loc.Get("Ifr_PERIME_a_mettre_a_jour") : ".") +
                            Loc.F("Ifr_Fixes_routes", _ifrData.Fixes.Count, _ifrData.Segments.Select(s => s.Airway).Distinct().Count()) + procedures +
                            ProceduresNote() + NetworkNote();
    }

    /// <summary>
    /// Un terrain connu de la carte mais absent des données IFR est hors de France (le SIA ne couvre que la France) :
    /// il faut le réseau mondial lu dans MSFS. Le dire évite de croire à une faute de saisie.
    /// </summary>
    private string MissingDataHint(string dep, string arr)
    {
        var missing = new[] { dep, arr }.Distinct().Where(c => _ifrData.Find(c) is null && _airports.Any(a => a.Ident == c)).ToList();
        if (missing.Count == 0) return "";
        return Loc.F("Ifr_existe_sur_la_carte_mais", string.Join(Loc.Get("Common_And"), missing)) +
               Loc.Get("Ifr_Pour_voler_hors_de_France");
    }

    private async void IfrLoad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Get("Ifr_Export_AIXM_du_SIA_zip"), Title = "export_xml_bd_SIA_….zip" };
        if (dlg.ShowDialog(this) != true) return;
        bool loaded;
        using (BeginBusy(Loc.Get("Busy_Lecture_donnees_AIRAC"))) loaded = await LoadIfrDataAsync(dlg.FileName);
        if (loaded)
        {
            _settings.IfrDataPath = dlg.FileName;
            SaveSettings();
        }
        RefreshIfrChoices();
    }

    private async Task<bool> LoadIfrDataAsync(string path)
    {
        IfrDataLabel.Foreground = System.Windows.Media.Brushes.Gold;
        IfrDataLabel.Text = Loc.Get("Ifr_Lecture_des_donnees_AIRAC");
        try
        {
            var loaded = await Task.Run(() => AixmReader.LoadFile(path, GeoArea.MetropolitanFrance));
            _ifrData = loaded.Data;
            _ils = loaded.Ils;
            await ApplyWorldNetworkAsync();
            EnsureProceduresForRoute();
            ShowIfrDataLabel();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            ShowIfrDataLabel();
            IfrStatus.Foreground = System.Windows.Media.Brushes.Gold;
            IfrStatus.Text = Loc.Get("Ifr_Fichier_AIXM_illisible") + ex.Message;
            return false;
        }
    }

    /// <summary>Remplit les listes de pistes, de SID et de STAR avec ce que la base propose pour les deux terrains.</summary>
    private void RefreshIfrChoices()
    {
        EnsureProceduresForRoute();
        _programmatic = true;
        try
        {
            FillProcedures(IfrSidBox, IfrDepRunwayBox, IfrDepBox.Text, ProcedureKind.Sid, keepRunways: false);
            FillProcedures(IfrStarBox, IfrArrRunwayBox, IfrArrBox.Text, ProcedureKind.Star, keepRunways: false);
        }
        finally { _programmatic = false; }
        IfrAirportInfo.Text = BuildAirportInfo();
        ApplyAdvice();
        ScheduleRunwayAdvice();
    }

    /// <summary>
    /// Liste des pistes du terrain puis des procédures qui desservent la piste choisie (toutes si « Auto »).
    /// <paramref name="keepRunways"/> garde la liste de pistes telle quelle, quand c'est elle qui vient de changer.
    /// </summary>
    private void FillProcedures(ComboBox procedures, ComboBox runways, string icao, ProcedureKind kind, bool keepRunways)
    {
        bool outer = _programmatic;
        _programmatic = true;
        try
        {
            var all = _ifrData.ProceduresFor(icao.Trim(), kind).ToList();
            if (!keepRunways) SetItems(runways, RunwayAdvice.RunwaysOf(all));
            string? runway = Chosen(runways);
            SetItems(procedures, RunwayAdvice.ForRunway(all, runway).OrderBy(p => p.Ident, StringComparer.Ordinal).Select(p => ProcedureLabel.For(p, runway)), withNone: true);
        }
        finally { _programmatic = outer; }
    }

    /// <summary>Ce que MSFS Rhumb sait des deux terrains saisis, avec le vent et la piste conseillée quand on les connaît.</summary>
    private string BuildAirportInfo()
    {
        var lines = new List<string>();
        void Add(string role, string text, ProcedureKind kind, RunwayAdviceState? advice)
        {
            string info = AirportProcedureInfo(role, text, kind);
            if (info.Length == 0) return;
            lines.Add(info);
            if (AdviceText(advice) is { Length: > 0 } wind) lines.Add(wind);
        }
        Add(Loc.Get("Ifr_Depart"), IfrDepBox.Text, ProcedureKind.Sid, _depAdvice);
        Add(Loc.Get("Ifr_Arrivee"), IfrArrBox.Text, ProcedureKind.Star, _arrAdvice);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Remet « Auto » en tête de liste (puis « Aucune » pour les procédures) et garde le choix précédent s'il existe encore,
    /// reconnu à son identifiant : le texte d'une SID change avec la piste.
    /// </summary>
    private static void SetItems(ComboBox box, IEnumerable<string> items, bool withNone = false)
    {
        string? previous = box.SelectedItem is string p ? ProcedureLabel.Ident(p) : null;
        var all = new List<string> { AutoChoice };
        if (withNone) all.Add(NoneChoice);
        all.AddRange(items);
        box.ItemsSource = all;
        box.SelectedItem = (previous is null ? null : all.FirstOrDefault(x => ProcedureLabel.Ident(x) == previous)) ?? AutoChoice;
    }

    private static string? Chosen(ComboBox box) =>
        box.SelectedItem is string s && s != AutoChoice && s != NoneChoice ? ProcedureLabel.Ident(s) : null;

    private static bool IsNone(ComboBox box) => box.SelectedItem as string == NoneChoice;

    private async void IfrCompute_Click(object sender, RoutedEventArgs e)
    {
        SetMode(FlightMode.Ifr);
        string dep = ResolveIfrCode(IfrDepBox.Text), arr = ResolveIfrCode(IfrArrBox.Text);
        int? fl = null;
        if (IfrFlBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(IfrFlBox.Text.Trim(), out int value) || value is < 0 or > 600)
            {
                IfrStatus.Text = Loc.Get("Ifr_Niveau_de_vol_invalide_ex");
                return;
            }
            fl = value;
        }

        var options = new IfrPlanOptions
        {
            Sid = Chosen(IfrSidBox),
            Star = Chosen(IfrStarBox),
            NoSid = IsNone(IfrSidBox),
            NoStar = IsNone(IfrStarBox),
            DepartureRunway = Chosen(IfrDepRunwayBox),
            ArrivalRunway = Chosen(IfrArrRunwayBox),
            FlightLevel = fl,
        };
        IfrPlanResult result;
        using (BeginBusy(Loc.Get("Busy_Calcul_de_la_route_IFR")))
        {
            await FlushUiAsync();
            EnsureProcedures(dep, arr);
            var data = _ifrData;
            result = await Task.Run(() => IfrRouter.Plan(data, dep, arr, options));
        }

        if (result.Route is not { } ifr)
        {
            IfrStatus.Foreground = System.Windows.Media.Brushes.Gold;
            IfrStatus.Text = result.Error + MissingDataHint(dep, arr);
            return;
        }

        // Altitude atteignable : sur une route courte, la montée et la descente ne laissent pas le temps d'atteindre le niveau demandé.
        VerticalPlan? vertical = null;
        if (AircraftFor(FlightMode.Ifr) is { } ifrAircraft)
        {
            double depFt = AirportByCode(dep)?.ElevationFt ?? 0, arrFt = AirportByCode(arr)?.ElevationFt ?? 0;
            var first = new Waypoint { Lat = ifr.Points[0].Lat, Lon = ifr.Points[0].Lon };
            var last = new Waypoint { Lat = ifr.Points[^1].Lat, Lon = ifr.Points[^1].Lon };
            double track = MagneticTrack(first, last);
            double requested = fl is { } asked ? asked * 100.0 : ifrAircraft.CruiseAltFt;
            vertical = VerticalPlanner.Compute(ifr.DistanceNm, depFt, arrFt, requested, ifrAircraft, track);
            if (vertical.Limited)
            {
                // Le niveau demandé est trop haut : on retient le plus haut atteignable et on refait le trajet à ce niveau (les routes ATS en dépendent).
                options.FlightLevel = vertical.CruiseFl;
                IfrPlanResult again;
                using (BeginBusy(Loc.Get("Busy_Calcul_de_la_route_IFR")))
                {
                    var data = _ifrData;
                    again = await Task.Run(() => IfrRouter.Plan(data, dep, arr, options));
                }
                if (again.Route is { } lower) ifr = lower;
                vertical = VerticalPlanner.Compute(ifr.DistanceNm, depFt, arrFt, vertical.CruiseFt, ifrAircraft, track);
                IfrFlBox.Text = vertical.CruiseFl.ToString();
            }
        }

        // Finale ILS indicative : ajoutée avant l'arrivée si la piste (choisie, ou la plus alignée avec l'arrivée) a un ILS dans les données.
        IlsFinal? ilsFinal = null;
        string? ilsNote = null;
        if (IfrIlsCheck.IsChecked == true)
        {
            double field = AirportByCode(arr)?.ElevationFt ?? 0;
            (ifr, ilsFinal, ilsNote) = IlsPlanner.Apply(ifr, _ils, Chosen(IfrArrRunwayBox), field);
        }

        if (_route.Count > 0 && MessageBox.Show(this, Loc.Get("Ifr_Remplacer_la_route_actuelle_par"), Loc.Get("Ifr_Route_IFR"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var waypoints = ifr.ToWaypoints().ToList();
        // Les terrains du catalogue ont leur altitude et leur nom complet ; les points des procédures restent tels quels.
        for (int i = 0; i < waypoints.Count; i++)
            if (waypoints[i].Icao is { } icao && _airports.FirstOrDefault(a => a.Ident == icao) is { } apt)
                waypoints[i] = apt.ToWaypoint();

        // Les points de la finale portent l'ILS en note : elle sort sur la feuille de route, dans « Points de navigation et notes ».
        bool firstIls = true;
        for (int i = 0; i < waypoints.Count && i < ifr.Points.Count; i++)
        {
            if (ifr.Points[i].Source != IfrLegSource.Ils || ilsFinal is null) continue;
            waypoints[i].Notes = firstIls
                ? ilsFinal.Summary
                : Loc.F("Ifr_Finale_ILS_ft_sur_la", ilsFinal.Ils.Runway, ifr.Points[i].MinAltFt);
            firstIls = false;
        }

        if (vertical is not null) InsertVerticalPoints(waypoints, vertical);

        _route.Clear();
        foreach (var w in waypoints) _route.Add(w);
        FitRoute();

        IfrStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        string runways = new[] { Chosen(IfrDepRunwayBox) is { } d ? Loc.F("Ifr_Piste_depart", d) : null, Chosen(IfrArrRunwayBox) is { } a ? Loc.F("Ifr_Piste_arrivee", a) : null }
            .Where(t => t is not null).Aggregate("", (acc, t) => acc.Length == 0 ? t! : acc + ", " + t);
        IfrStatus.Text = Loc.F("Ifr_Route_summary", ifr.Description, ifr.DistanceNm, ifr.Points.Count) + (runways.Length > 0 ? Loc.F("Ifr_Runways_line", runways) : "")
            + (ilsFinal is not null && ilsNote is null ? "\n" + ilsFinal.Summary : "") + (ilsNote is not null ? "\n" + ilsNote : "");
        RefreshVerticalInfo();
    }
}
