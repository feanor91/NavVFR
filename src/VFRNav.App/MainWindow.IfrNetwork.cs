using System.IO;
using System.Windows;
using System.Windows.Media;
using VFRNav.Core.Ifr;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Routes ATS hors de France : lues dans MSFS, enregistrées dans les données de MSFS Rhumb, ajoutées à celles du SIA ----------
public partial class MainWindow
{
    private static string NetworkButtonText => Loc.Get("Ui_Enregistrer_mettre_a_jour_les_2");
    private const int CrawlWindow = 128;

    private WorldNetwork? _world;
    private string? _worldProblem;
    private CancellationTokenSource? _networkCts;
    private bool _readingNetwork;

    private sealed record NetworkProgress(int Done, int Left, int Segments);

    // ---------- utilisation sans MSFS ----------

    /// <summary>Lit le fichier du réseau mondial (celui de l'utilisateur, sinon celui livré avec l'application).</summary>
    private async Task LoadWorldNetworkAsync()
    {
        string? path = File.Exists(WorldNetworkStore.UserPath) ? WorldNetworkStore.UserPath
            : File.Exists(WorldNetworkStore.BundledPath) ? WorldNetworkStore.BundledPath : null;
        if (path is null) { _world = null; _worldProblem = null; return; }
        var (network, problem) = await Task.Run(() => (WorldNetworkStore.Read(path, out var p), p));
        _world = network;
        _worldProblem = problem;
        await ApplyMsfsAliasesAsync();
    }

    /// <summary>Ajoute le réseau mondial à la base de navigation en cours (la France reste celle du SIA).</summary>
    private async Task ApplyWorldNetworkAsync()
    {
        if (_world is { } network)
        {
            var data = _ifrData;
            _ifrData = await Task.Run(() => WorldNetworkMerge.Apply(data, network));
        }
        ResetAppliedProcedures();
    }

    /// <summary>Texte ajouté à l'étiquette des données IFR : ce que contient le réseau hors de France.</summary>
    private string NetworkNote()
    {
        if (_worldProblem is { } problem) return Loc.Get("IfrNetwork_Routes_ATS_hors_de_France") + problem;
        if (_world is not { } w)
            return Loc.Get("IfrNetwork_Routes_ATS_hors_de_France_2") + NetworkButtonText + Loc.Get("IfrNetwork_les_lit_une_fois_MSFS");
        return Loc.F("IfrNetwork_Routes_ATS_hors_de_France_3", w.Segments.Count, w.Airways, w.Fixes.Count - w.Airports, w.Airports) +
               Loc.F("IfrNetwork_lus_dans_le_utilisables_sans", w.SimVersion, w.ReadAt.LocalDateTime);
    }

    // ---------- lecture dans MSFS ----------

    /// <summary>
    /// Explore dans MSFS les routes ATS de proche en proche, à partir des points des SID/STAR enregistrées, et enregistre le réseau.
    /// Long (de quelques minutes à une heure) : un second clic l'arrête, et rien n'est alors enregistré (un réseau à moitié lu ferait de fausses routes).
    /// </summary>
    private async void IfrNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (_readingNetwork)
        {
            _networkCts?.Cancel();
            return;
        }
        if (_readingProcedures) { SetProcedureStatus(Loc.Get("IfrNetwork_Attendez_la_fin_de_la")); return; }

        if (SeedPoints().Count == 0)
        {
            SetProcedureStatus(Loc.Get("IfrNetwork_Enregistrez_d_abord_les_SID"));
            return;
        }

        string simVersion;
        try { simVersion = await Task.Run(() => { using var s = SimFacilitySession.Open(); return s.SimVersion; }); }
        catch (InvalidOperationException ex) { SetProcedureStatus(ex.Message); return; }

        if (MessageBox.Show(this, NetworkQuestion(simVersion), Loc.Get("IfrNetwork_Routes_ATS_hors_de_France_4"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cts = _networkCts = new CancellationTokenSource();
        using var busy = BeginBusy(Loc.Get("Busy_Exploration_routes_ATS"), () => cts.Cancel());
        SetNetworkReading(true);
        var progress = new Progress<NetworkProgress>(p =>
        {
            IfrNetworkButton.Content = Loc.F("IfrNetwork_Arreter_la_lecture_points", p.Done);
            SetProcedureStatus(Loc.F("IfrNetwork_Exploration_des_routes_dans_MSFS", p.Done, p.Left, p.Segments));
            SetBusyDetail(Loc.F("IfrNetwork_Exploration_des_routes_dans_MSFS", p.Done, p.Left, p.Segments));
        });

        WorldNetwork? network = null;
        string? problem = null;
        bool cancelled = false;
        try { network = await Task.Run(() => ReadWorldNetwork(progress, cts.Token)); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (InvalidOperationException ex) { problem = ex.Message; }
        finally
        {
            _networkCts = null;
            SetNetworkReading(false);
        }

        if (cancelled) { SetProcedureStatus(Loc.Get("IfrNetwork_Lecture_arretee_rien_n_est")); return; }
        if (problem is not null) { SetProcedureStatus(Loc.Get("IfrNetwork_Lecture_interrompue") + problem + Loc.Get("IfrNetwork_Rien_n_est_enregistre")); return; }
        if (network is null) return;

        try
        {
            await Task.Run(() => WorldNetworkStore.Write(WorldNetworkStore.UserPath, network));
        }
        catch (IOException ex) { SetProcedureStatus(Loc.Get("IfrNetwork_Reseau_lu_mais_non_enregistre") + ex.Message); return; }

        _world = network;
        _worldProblem = null;
        await ReloadNavigationDataAsync();
        SetProcedureStatus(Loc.F("IfrNetwork_troncons_sur_routes_enregistres_points", network.Segments.Count, network.Airways, network.Fixes.Count - network.Airports, network.Airports, network.SimVersion), Brushes.LightGreen);
    }

    private string NetworkQuestion(string simVersion)
    {
        string how = Loc.Get("IfrNetwork_L_exploration_part_des_points") +
                     Loc.Get("IfrNetwork_Elle_prend_de_quelques_minutes");
        if (_world is not { } w) return Loc.F("IfrNetwork_Lire_dans_les_routes_ATS", simVersion) + how;
        string head = w.SimVersion == simVersion
            ? Loc.F("IfrNetwork_Le_reseau_enregistre_troncons_a", w.Segments.Count, simVersion, w.ReadAt.LocalDateTime)
            : Loc.F("IfrNetwork_MSFS_a_change_de_version", w.SimVersion, w.ReadAt.LocalDateTime, simVersion);
        return head + Loc.Get("IfrNetwork_Relire_toutes_les_routes_pour") + how;
    }

    /// <summary>Points de départ de l'exploration : les points des SID/STAR enregistrées qui ne sont pas en France.</summary>
    private List<SimPoint> SeedPoints()
    {
        var seeds = new Dictionary<string, SimPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var icao in _procedures.User.Snapshot().Keys)
            if (_procedures.Read(icao) is { } import)
                foreach (var fix in import.Fixes)
                    if (fix.Ident.Contains('/') && !seeds.ContainsKey(fix.Ident))
                        seeds[fix.Ident] = new SimPoint(fix.Kind switch { FixKind.Vor => SimPointKind.Vor, FixKind.Ndb => SimPointKind.Ndb, _ => SimPointKind.Waypoint },
                            FixKey.Display(fix.Ident), FixKey.Region(fix.Ident), fix.Lat, fix.Lon, 0, 0);
        return [.. seeds.Values];
    }

    /// <summary>Lecture sur un thread à part : les terrains du monde, puis les routes de proche en proche.</summary>
    private WorldNetwork ReadWorldNetwork(IProgress<NetworkProgress> progress, CancellationToken ct)
    {
        using var session = SimFacilitySession.Open();
        var builder = new WorldNetworkBuilder();
        foreach (var airport in session.ListAirportPoints(ct)) builder.AddAirport(airport.Icao, airport.Lat, airport.Lon);

        var seeds = builder.Seed(SeedPoints());
        session.CrawlRoutes(seeds, builder.AddLinks, CrawlWindow, ct, TimeSpan.FromSeconds(10),
            (done, left) => progress.Report(new NetworkProgress(done, left, builder.SegmentCount)));
        return builder.Build(session.SimVersion, DateTimeOffset.Now);
    }

    private void SetNetworkReading(bool reading)
    {
        _readingNetwork = reading;
        IfrSimButton.IsEnabled = IfrSimAllButton.IsEnabled = !reading;
        if (!reading) IfrNetworkButton.Content = NetworkButtonText;
    }

    /// <summary>Refait la base de navigation : fichier du SIA, réseau mondial, puis procédures du départ et de l'arrivée.</summary>
    private async Task ReloadNavigationDataAsync()
    {
        if (_settings.IfrDataPath.Length > 0 && File.Exists(_settings.IfrDataPath)) await LoadIfrDataAsync(_settings.IfrDataPath);
        else
        {
            _ifrData = IfrSampleData.Create();
            await ApplyWorldNetworkAsync();
            EnsureProceduresForRoute();
            ShowIfrDataLabel();
        }
        RefreshIfrChoices();
    }
}
