using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using VFRNav.Core.Ifr;
using VFRNav.Core.Models;

namespace VFRNav.App;

// ---------- Route IFR : choix du départ, de l'arrivée, des procédures, puis calcul sur la base de navigation ----------
public partial class MainWindow
{
    private const string AutoChoice = "Auto";

    // Pour l'instant : données de démonstration. Un lecteur AIRAC les remplacera.
    private IfrNavData _ifrData = IfrSampleData.Create();

    private async void Ifr_Loaded(object sender, RoutedEventArgs e)
    {
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
            IfrDataLabel.Text = IfrSampleData.Label + " : ne pas utiliser pour naviguer. Chargez l'export AIXM du SIA." + ProceduresNote() + NetworkNote();
            return;
        }
        bool expired = _ifrData.IsExpired(DateTime.UtcNow);
        IfrDataLabel.Foreground = expired ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.LightGreen;
        string procedures = _ifrData.Procedures.Count == 0
            ? " Pas de SID/STAR dans ces données : les terrains se raccordent directement aux routes ATS."
            : "";
        IfrDataLabel.Text = $"AIRAC {_ifrData.AiracCycle}" + (expired ? " : PÉRIMÉ, à mettre à jour." : ".") +
                            $" {_ifrData.Fixes.Count} points, {_ifrData.Segments.Select(s => s.Airway).Distinct().Count()} routes ATS." + procedures +
                            ProceduresNote() + NetworkNote();
    }

    private async void IfrLoad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Export AIXM du SIA (*.zip;*.xml)|*.zip;*.xml", Title = "export_xml_bd_SIA_….zip" };
        if (dlg.ShowDialog(this) != true) return;
        if (await LoadIfrDataAsync(dlg.FileName))
        {
            _settings.IfrDataPath = dlg.FileName;
            SaveSettings();
        }
        RefreshIfrChoices();
    }

    private async Task<bool> LoadIfrDataAsync(string path)
    {
        IfrDataLabel.Foreground = System.Windows.Media.Brushes.Gold;
        IfrDataLabel.Text = "Lecture des données AIRAC...";
        try
        {
            var loaded = await Task.Run(() => AixmReader.LoadFile(path, GeoArea.MetropolitanFrance));
            _ifrData = loaded.Data;
            await ApplyWorldNetworkAsync();
            EnsureProceduresForRoute();
            ShowIfrDataLabel();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            ShowIfrDataLabel();
            IfrStatus.Foreground = System.Windows.Media.Brushes.Gold;
            IfrStatus.Text = "Fichier AIXM illisible : " + ex.Message;
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
            SetItems(procedures, RunwayAdvice.ForRunway(all, Chosen(runways)).Select(p => p.Ident).Order());
        }
        finally { _programmatic = outer; }
    }

    /// <summary>Ce que VFRNav sait des deux terrains saisis, avec le vent et la piste conseillée quand on les connaît.</summary>
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
        Add("Départ", IfrDepBox.Text, ProcedureKind.Sid, _depAdvice);
        Add("Arrivée", IfrArrBox.Text, ProcedureKind.Star, _arrAdvice);
        return string.Join("\n", lines);
    }

    /// <summary>Remet « Auto » en tête de liste et garde le choix précédent s'il existe encore.</summary>
    private static void SetItems(ComboBox box, IEnumerable<string> items)
    {
        string? previous = box.SelectedItem as string;
        var all = new List<string> { AutoChoice };
        all.AddRange(items);
        box.ItemsSource = all;
        box.SelectedItem = previous is not null && all.Contains(previous) ? previous : AutoChoice;
    }

    private static string? Chosen(ComboBox box) =>
        box.SelectedItem is string s && s != AutoChoice ? s : null;

    private void IfrCompute_Click(object sender, RoutedEventArgs e)
    {
        SetMode(FlightMode.Ifr);
        string dep = IfrDepBox.Text.Trim().ToUpperInvariant(), arr = IfrArrBox.Text.Trim().ToUpperInvariant();
        int? fl = null;
        if (IfrFlBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(IfrFlBox.Text.Trim(), out int value) || value is < 0 or > 600)
            {
                IfrStatus.Text = "Niveau de vol invalide (ex. 240 pour FL240), ou laisse la case vide.";
                return;
            }
            fl = value;
        }

        EnsureProcedures(dep, arr);
        var result = IfrRouter.Plan(_ifrData, dep, arr, new IfrPlanOptions
        {
            Sid = Chosen(IfrSidBox),
            Star = Chosen(IfrStarBox),
            DepartureRunway = Chosen(IfrDepRunwayBox),
            ArrivalRunway = Chosen(IfrArrRunwayBox),
            FlightLevel = fl,
        });

        if (result.Route is not { } ifr)
        {
            IfrStatus.Foreground = System.Windows.Media.Brushes.Gold;
            IfrStatus.Text = result.Error;
            return;
        }

        if (_route.Count > 0 && MessageBox.Show(this, "Remplacer la route actuelle par la route IFR ?", "Route IFR",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var waypoints = ifr.ToWaypoints().ToList();
        // Les terrains du catalogue ont leur altitude et leur nom complet ; les points des procédures restent tels quels.
        for (int i = 0; i < waypoints.Count; i++)
            if (waypoints[i].Icao is { } icao && _airports.FirstOrDefault(a => a.Ident == icao) is { } apt)
                waypoints[i] = apt.ToWaypoint();

        _route.Clear();
        foreach (var w in waypoints) _route.Add(w);
        FitRoute();

        IfrStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        string runways = new[] { Chosen(IfrDepRunwayBox) is { } d ? $"départ {d}" : null, Chosen(IfrArrRunwayBox) is { } a ? $"arrivée {a}" : null }
            .Where(t => t is not null).Aggregate("", (acc, t) => acc.Length == 0 ? t! : acc + ", " + t);
        IfrStatus.Text = $"{ifr.Description}\n{ifr.DistanceNm:0.#} NM, {ifr.Points.Count} points." + (runways.Length > 0 ? $"\nPistes : {runways}." : "");
    }
}
