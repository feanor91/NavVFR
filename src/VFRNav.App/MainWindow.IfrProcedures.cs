using System.IO;
using System.Windows;
using System.Windows.Media;
using VFRNav.Core.Ifr;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- SID/STAR du monde : lues dans MSFS une fois, enregistrées dans les données de MSFS Rhumb, utilisées sans MSFS ----------
public partial class MainWindow
{
    private const int ReadWindow = 128;
    private static string WorldButtonText => Loc.Get("Ui_Enregistrer_mettre_a_jour_les");

    private ProcedureLibrary _procedures = OpenProcedureLibrary();
    private readonly HashSet<string> _proceduresApplied = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _procedureCts;
    private bool _readingProcedures;

    private sealed record ReadProgress(int Done, int Total, string Icao, int WithProcedures);

    private sealed record ReadResult(string SimVersion, int Read, int Total, int New, int Updated, int Unchanged, int Removed, bool Cancelled);

    private static ProcedureLibrary OpenProcedureLibrary() => new(
        Directory.Exists(ProcedureCatalog.BundledDirectory) ? new ProcedureCatalog(ProcedureCatalog.BundledDirectory) : null,
        new ProcedureCatalog(ProcedureCatalog.UserDirectory));

    // ---------- utilisation sans MSFS ----------

    /// <summary>
    /// Ajoute à la base de navigation les SID/STAR enregistrées pour ces terrains (lues à la demande dans leur fichier).
    /// Un terrain n'est lu qu'une fois, jusqu'à ce que la base soit remplacée ou que ses procédures soient mises à jour.
    /// </summary>
    private void EnsureProcedures(params string[] airports)
    {
        foreach (var raw in airports)
        {
            string icao = raw.Trim().ToUpperInvariant();
            if (icao.Length != 4 || !_proceduresApplied.Add(icao)) continue;
            if (_procedures.Read(icao) is { } import) _ifrData = SimProcedures.Merge(_ifrData, icao, import);
        }
    }

    /// <summary>Ce que MSFS Rhumb sait déjà du terrain saisi : de quoi partir ou arriver d'ailleurs sans MSFS.</summary>
    private string AirportProcedureInfo(string role, string text, ProcedureKind kind)
    {
        string icao = ResolveIfrCode(text);
        if (icao.Length != 4) return "";
        string label = kind == ProcedureKind.Sid ? "SID" : "STAR";
        // Un code qui n'est pas un terrain (faute de frappe, point de navigation de même nom) : on le dit et on propose le code voisin.
        bool isAirport = _ifrData.Find(icao) is { Kind: FixKind.Airport } || _airports.Any(a => string.Equals(a.Ident, icao, StringComparison.OrdinalIgnoreCase));
        if (!isAirport)
        {
            string hint = SimilarAirportsHint(icao);
            return Loc.F("IfrProcedures_terrain_inconnu_des_donnees_ni", role, icao) + hint;
        }
        // Un terrain connu des seules données de MSFS (absent de la base mondiale OurAirports) n'est ni sur la carte ni dans la recherche : on le dit.
        string msfsOnly = _airports.Any(a => string.Equals(a.Ident, icao, StringComparison.OrdinalIgnoreCase)) ? "" : " " + Loc.F("IfrProcedures_Terrain_MSFS_seulement", icao);
        int count = _ifrData.ProceduresFor(icao, kind).Count();
        if (count > 0) return Loc.F("IfrProcedures_enregistrees_MSFS_inutile", role, icao, count, label) + msfsOnly;

        // Terrain sans procédure (petite piste, code proche d'un grand aéroport) : on propose les codes voisins qui en ont.
        string suggestion = SimilarAirportsHint(icao);
        return Loc.F("IfrProcedures_aucune_enregistree_le_terrain_se", role, icao, label) + msfsOnly + suggestion;
    }

    /// <summary>
    /// « Vouliez-vous dire EGLL (London Heathrow) ? » : terrains qui ont des procédures enregistrées et dont le code ne diffère que d'une lettre,
    /// les plus fournis en procédures d'abord. Vide s'il n'y en a pas.
    /// </summary>
    private string SimilarAirportsHint(string icao)
    {
        var counts = _procedures.ProcedureCounts();
        var nearby = AirportSuggestions.Similar(icao, counts.Keys, max: 3, weight: id => counts[id]);
        if (nearby.Count == 0) return "";
        string names = string.Join(", ", nearby.Select(id => _airports.FirstOrDefault(a => a.Ident == id) is { } a ? $"{id} ({a.Name})" : id));
        return " " + Loc.F("IfrProcedures_Vouliez_vous", names);
    }

    private void EnsureProceduresForRoute() => EnsureProcedures(IfrDepBox.Text, IfrArrBox.Text);

    /// <summary>La base de navigation vient d'être remplacée (nouveau fichier du SIA) : les procédures sont à remettre.</summary>
    private void ResetAppliedProcedures() => _proceduresApplied.Clear();

    /// <summary>Texte ajouté à l'étiquette des données IFR : ce que contient la bibliothèque de SID/STAR.</summary>
    private string ProceduresNote()
    {
        if (_procedures.Problem is { } problem) return Loc.Get("IfrProcedures_SID_STAR_enregistrees_ignorees") + problem;
        if (_procedures.AirportCount == 0)
            return Loc.Get("IfrProcedures_SID_STAR_aucune_enregistree") + WorldButtonText + Loc.Get("IfrProcedures_les_lit_une_fois_MSFS");
        var origin = _procedures.Origin;
        string from = origin is { } o ? Loc.F("IfrProcedures_lues_dans_le", (o.SimVersion.Length > 0 ? o.SimVersion : "MSFS"), o.ReadAt.LocalDateTime) : "";
        return Loc.F("IfrProcedures_SID_STAR_enregistrees_procedures_pour", _procedures.ProcedureCount, _procedures.AirportCount, from) +
               Loc.Get("IfrProcedures_Leur_cycle_AIRAC_est_celui");
    }

    private void SetProcedureStatus(string text, Brush? color = null)
    {
        IfrStatus.Foreground = color ?? Brushes.Gold;
        IfrStatus.Text = text;
    }

    // ---------- lecture du départ et de l'arrivée ----------

    /// <summary>Relit dans MSFS les SID et STAR du départ et de l'arrivée saisis (utile pour un terrain qu'on veut remettre à jour tout de suite).</summary>
    private async void IfrSim_Click(object sender, RoutedEventArgs e)
    {
        if (_readingProcedures) return;
        var airports = new[] { IfrDepBox.Text, IfrArrBox.Text }
            .Select(a => a.Trim().ToUpperInvariant()).Where(a => a.Length == 4).Distinct().ToList();
        if (airports.Count == 0) { SetProcedureStatus(Loc.Get("IfrProcedures_Saisissez_le_depart_et_l")); return; }

        using var busy = BeginBusy(Loc.Get("Busy_Lecture_terrains"));
        SetReading(true);
        SetProcedureStatus(Loc.Get("IfrProcedures_Lecture_des_SID_STAR_dans"));
        try
        {
            var lines = await Task.Run(() =>
            {
                using var session = SimFacilitySession.Open();
                var catalog = _procedures.User;
                var output = new List<string>();
                var now = DateTimeOffset.Now;
                foreach (var icao in airports)
                {
                    var import = SimProcedures.ToIfr(icao, session.ReadProcedures(icao));
                    if (import.Procedures.Count == 0) { catalog.Remove(icao); output.Add(Loc.F("IfrProcedures_MSFS_n_a_ni_SID", icao)); continue; }
                    var change = catalog.Write(icao, import, now);
                    int sids = import.Procedures.Count(p => p.Kind == ProcedureKind.Sid);
                    output.Add($"{icao} : {sids} SID, {import.Procedures.Count - sids} STAR ({ChangeText(change)})");
                }
                catalog.SaveIndex(session.SimVersion, now);
                return output;
            });

            foreach (var icao in airports) _proceduresApplied.Remove(icao);
            SetProcedureStatus(string.Join("\n", lines), Brushes.LightGreen);
        }
        catch (InvalidOperationException ex) { SetProcedureStatus(ex.Message); }
        finally { SetReading(false); }

        EnsureProceduresForRoute();
        ShowIfrDataLabel();
        RefreshIfrChoices();
    }

    private static string ChangeText(CatalogChange change) => change switch
    {
        CatalogChange.New => Loc.Get("Procedures_New"),
        CatalogChange.Updated => Loc.Get("IfrProcedures_mis_a_jour"),
        _ => Loc.Get("IfrProcedures_inchange"),
    };

    // ---------- lecture du monde entier, et mise à jour ----------

    /// <summary>
    /// Lit dans MSFS les SID et STAR de tous les terrains du simulateur et les enregistre dans les données de MSFS Rhumb.
    /// À faire une fois, puis à la main quand MSFS a mis à jour sa base : la même commande sert aux deux, et dit ce qui a changé.
    /// Un second clic pendant la lecture l'arrête (ce qui a déjà été lu reste enregistré).
    /// </summary>
    private async void IfrSimAll_Click(object sender, RoutedEventArgs e)
    {
        if (_readingProcedures)
        {
            _procedureCts?.Cancel();
            return;
        }

        // Une connexion rapide pour connaître la version de MSFS avant de proposer quoi que ce soit.
        string simVersion;
        try { simVersion = await Task.Run(() => { using var s = SimFacilitySession.Open(); return s.SimVersion; }); }
        catch (InvalidOperationException ex) { SetProcedureStatus(ex.Message); return; }

        if (MessageBox.Show(this, UpdateQuestion(simVersion), Loc.Get("IfrProcedures_SID_STAR_du_monde"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cts = _procedureCts = new CancellationTokenSource();
        using var busy = BeginBusy(Loc.Get("Busy_Lecture_SID_STAR"), () => cts.Cancel());
        SetReading(true);
        var progress = new Progress<ReadProgress>(p =>
        {
            IfrSimAllButton.Content = Loc.F("IfrProcedures_Arreter_la_lecture", p.Done, p.Total);
            SetProcedureStatus(Loc.F("IfrProcedures_Lecture_dans_MSFS_terrains_avec", p.Done, p.Total, p.Icao, p.WithProcedures));
            SetBusyDetail(Loc.F("IfrProcedures_Lecture_dans_MSFS_terrains_avec", p.Done, p.Total, p.Icao, p.WithProcedures));
        });

        ReadResult? result = null;
        string? stopped = null;
        try { result = await Task.Run(() => ReadWholeWorld(progress, cts.Token)); }
        catch (SimUnavailableException ex) { stopped = ex.Message; }
        catch (InvalidOperationException ex) { stopped = ex.Message; }
        finally
        {
            _procedureCts = null;
            SetReading(false);
        }

        _proceduresApplied.Clear();
        EnsureProceduresForRoute();
        ShowIfrDataLabel();
        RefreshIfrChoices();

        if (stopped is not null) SetProcedureStatus(Loc.Get("IfrNetwork_Lecture_interrompue") + stopped + Loc.Get("IfrProcedures_Ce_qui_a_ete_lu"));
        else if (result is not null) SetProcedureStatus(Summary(result), result.Cancelled ? Brushes.Gold : Brushes.LightGreen);
    }

    private string UpdateQuestion(string simVersion)
    {
        var origin = _procedures.Origin;
        if (_procedures.AirportCount == 0 || origin is null)
            return Loc.F("IfrProcedures_Lire_dans_les_SID_et", simVersion) +
                   Loc.Get("IfrProcedures_Les_donnees_sont_enregistrees_dans");

        string stored = origin.Value.SimVersion.Length > 0 ? origin.Value.SimVersion : Loc.Get("IfrProcedures_une_version_inconnue_de_MSFS");
        string head = origin.Value.SimVersion == simVersion
            ? Loc.F("IfrProcedures_Les_SID_STAR_enregistrees_terrains", _procedures.AirportCount, simVersion, origin.Value.ReadAt.LocalDateTime)
            : Loc.F("IfrProcedures_MSFS_a_change_de_version", stored, origin.Value.ReadAt.LocalDateTime, simVersion);
        return head + Loc.Get("IfrProcedures_Relire_tous_les_terrains_dans");
    }

    private static string Summary(ReadResult r)
    {
        string changes = r.Cancelled ? Loc.F("IfrProcedures_Lecture_arretee_apres_terrains", r.Read, r.Total) : Loc.F("Procedures_Read_in", r.Total, r.SimVersion);
        return Loc.F("IfrProcedures_nouveaux_mis_a_jour_inchanges", changes, r.New, r.Updated, r.Unchanged, r.Removed);
    }

    /// <summary>Lecture de tous les terrains, sur un thread à part : un fichier par terrain, l'index enregistré régulièrement.</summary>
    private ReadResult ReadWholeWorld(IProgress<ReadProgress> progress, CancellationToken ct)
    {
        using var session = SimFacilitySession.Open();
        var catalog = _procedures.User;
        var airports = session.ListAirports(ct);
        var inSim = new HashSet<string>(airports, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;
        int read = 0, withProcedures = 0, added = 0, updated = 0, unchanged = 0, removed = 0;
        bool cancelled = false;

        try
        {
            // 128 demandes en même temps : tout le monde se lit en quelques minutes, contre plusieurs heures une à une.
            session.ReadProceduresBatch(airports, ReadWindow, (icao, raw) =>
            {
                var import = SimProcedures.ToIfr(icao, raw);
                if (import.Procedures.Count > 0)
                {
                    withProcedures++;
                    switch (catalog.Write(icao, import, now))
                    {
                        case CatalogChange.New: added++; break;
                        case CatalogChange.Updated: updated++; break;
                        default: unchanged++; break;
                    }
                }
                else if (catalog.Remove(icao)) removed++; // il en avait : MSFS n'en a plus

                read++;
                if (read % 200 == 0 || read == airports.Count) progress.Report(new ReadProgress(read, airports.Count, icao, withProcedures));
                if (read % 5000 == 0) catalog.SaveIndex(session.SimVersion, now);
            }, ct, TimeSpan.FromSeconds(10));

            // Terrains enregistrés mais disparus de la liste de MSFS.
            foreach (var icao in catalog.Snapshot().Keys.Where(k => !inSim.Contains(k)).ToList())
                if (catalog.Remove(icao)) removed++;
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            // Même interrompue, la lecture laisse un index cohérent avec les fichiers écrits.
            catalog.SaveIndex(cancelled && catalog.Index?.SimVersion is { Length: > 0 } previous ? previous : session.SimVersion, now);
        }
        return new ReadResult(session.SimVersion, read, airports.Count, added, updated, unchanged, removed, cancelled);
    }

    private void SetReading(bool reading)
    {
        _readingProcedures = reading;
        IfrSimButton.IsEnabled = !reading;
        IfrNetworkButton.IsEnabled = !reading;
        if (!reading) IfrSimAllButton.Content = WorldButtonText;
    }
}
