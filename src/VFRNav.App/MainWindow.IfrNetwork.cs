using System.IO;
using System.Windows;
using System.Windows.Media;
using VFRNav.Core.Ifr;

namespace VFRNav.App;

// ---------- Routes ATS hors de France : lues dans MSFS, enregistrées dans les données de VFRNav, ajoutées à celles du SIA ----------
public partial class MainWindow
{
    private const string NetworkButtonText = "Enregistrer / mettre à jour les routes ATS hors de France (MSFS)";
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
        if (_worldProblem is { } problem) return "\nRoutes ATS hors de France ignorées : " + problem;
        if (_world is not { } w)
            return "\nRoutes ATS hors de France : aucune enregistrée (la France vient du SIA). « " + NetworkButtonText + " » les lit une fois, MSFS lancé.";
        return $"\nRoutes ATS hors de France : {w.Segments.Count} tronçons sur {w.Airways} routes, {w.Fixes.Count - w.Airports} points et {w.Airports} terrains, " +
               $"lus dans {w.SimVersion} le {w.ReadAt.LocalDateTime:dd/MM/yyyy}, utilisables sans MSFS. Sens uniques et niveaux : ceux que donne MSFS, à vérifier.";
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
        if (_readingProcedures) { SetProcedureStatus("Attendez la fin de la lecture des SID/STAR."); return; }

        if (SeedPoints().Count == 0)
        {
            SetProcedureStatus("Enregistrez d'abord les SID/STAR du monde : leurs points servent de départ à l'exploration des routes.");
            return;
        }

        string simVersion;
        try { simVersion = await Task.Run(() => { using var s = SimFacilitySession.Open(); return s.SimVersion; }); }
        catch (InvalidOperationException ex) { SetProcedureStatus(ex.Message); return; }

        if (MessageBox.Show(this, NetworkQuestion(simVersion), "Routes ATS hors de France", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cts = _networkCts = new CancellationTokenSource();
        SetNetworkReading(true);
        var progress = new Progress<NetworkProgress>(p =>
        {
            IfrNetworkButton.Content = $"Arrêter la lecture ({p.Done} points)";
            SetProcedureStatus($"Exploration des routes dans MSFS... {p.Done} points lus, {p.Left} à lire, {p.Segments} tronçons");
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

        if (cancelled) { SetProcedureStatus("Lecture arrêtée : rien n'est enregistré (un réseau à moitié lu ferait de fausses routes)."); return; }
        if (problem is not null) { SetProcedureStatus("Lecture interrompue : " + problem + "\nRien n'est enregistré."); return; }
        if (network is null) return;

        try
        {
            await Task.Run(() => WorldNetworkStore.Write(WorldNetworkStore.UserPath, network));
        }
        catch (IOException ex) { SetProcedureStatus("Réseau lu mais non enregistré : " + ex.Message); return; }

        _world = network;
        _worldProblem = null;
        await ReloadNavigationDataAsync();
        SetProcedureStatus($"{network.Segments.Count} tronçons sur {network.Airways} routes enregistrés ({network.Fixes.Count - network.Airports} points, {network.Airports} terrains), lus dans {network.SimVersion}.", Brushes.LightGreen);
    }

    private string NetworkQuestion(string simVersion)
    {
        string how = "\n\nL'exploration part des points des SID/STAR enregistrées et suit les routes de proche en proche. " +
                     "Elle prend de quelques minutes à une heure ; MSFS doit rester lancé. La France reste celle du SIA.";
        if (_world is not { } w) return $"Lire dans {simVersion} les routes ATS hors de France ?" + how;
        string head = w.SimVersion == simVersion
            ? $"Le réseau enregistré ({w.Segments.Count} tronçons) a déjà été lu dans {simVersion} le {w.ReadAt.LocalDateTime:dd/MM/yyyy}. Cette version de MSFS n'a pas changé."
            : $"MSFS a changé de version : le réseau enregistré vient de {w.SimVersion} ({w.ReadAt.LocalDateTime:dd/MM/yyyy}), le simulateur est maintenant en {simVersion}.";
        return head + "\n\nRelire toutes les routes pour mettre à jour ?" + how;
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
