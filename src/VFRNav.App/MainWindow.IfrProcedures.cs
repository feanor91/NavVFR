using System.IO;
using System.Windows;
using System.Windows.Media;
using VFRNav.Core.Ifr;

namespace VFRNav.App;

// ---------- SID/STAR du monde : lues dans MSFS une fois, enregistrées dans les données de VFRNav, utilisées sans MSFS ----------
public partial class MainWindow
{
    private const int ReadWindow = 128;
    private const string WorldButtonText = "Enregistrer / mettre à jour les SID/STAR du monde (MSFS)";

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

    /// <summary>Ce que VFRNav sait déjà du terrain saisi : de quoi partir ou arriver d'ailleurs sans MSFS.</summary>
    private string AirportProcedureInfo(string role, string text, ProcedureKind kind)
    {
        string icao = text.Trim().ToUpperInvariant();
        if (icao.Length != 4) return "";
        string label = kind == ProcedureKind.Sid ? "SID" : "STAR";
        if (_ifrData.Find(icao) is null) return $"{role} {icao} : terrain inconnu des données (ni le SIA ni MSFS).";
        int count = _ifrData.ProceduresFor(icao, kind).Count();
        return count > 0
            ? $"{role} {icao} : {count} {label} enregistrées, MSFS inutile."
            : $"{role} {icao} : aucune {label} enregistrée, le terrain se raccorde directement au réseau.";
    }

    private void EnsureProceduresForRoute() => EnsureProcedures(IfrDepBox.Text, IfrArrBox.Text);

    /// <summary>La base de navigation vient d'être remplacée (nouveau fichier du SIA) : les procédures sont à remettre.</summary>
    private void ResetAppliedProcedures() => _proceduresApplied.Clear();

    /// <summary>Texte ajouté à l'étiquette des données IFR : ce que contient la bibliothèque de SID/STAR.</summary>
    private string ProceduresNote()
    {
        if (_procedures.Problem is { } problem) return "\nSID/STAR enregistrées ignorées : " + problem;
        if (_procedures.AirportCount == 0)
            return "\nSID/STAR : aucune enregistrée. « " + WorldButtonText + " » les lit une fois, MSFS lancé ; ensuite il n'est plus nécessaire.";
        var origin = _procedures.Origin;
        string from = origin is { } o ? $", lues dans {(o.SimVersion.Length > 0 ? o.SimVersion : "MSFS")} le {o.ReadAt.LocalDateTime:dd/MM/yyyy}" : "";
        return $"\nSID/STAR enregistrées : {_procedures.ProcedureCount} procédures pour {_procedures.AirportCount} terrains{from}, utilisables sans MSFS. " +
               "Leur cycle AIRAC est celui du simulateur, qui peut différer du SIA.";
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
        if (airports.Count == 0) { SetProcedureStatus("Saisissez le départ et l'arrivée (codes OACI de quatre lettres)."); return; }

        SetReading(true);
        SetProcedureStatus("Lecture des SID/STAR dans MSFS...");
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
                    if (import.Procedures.Count == 0) { catalog.Remove(icao); output.Add($"{icao} : MSFS n'a ni SID ni STAR pour ce terrain."); continue; }
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
        CatalogChange.New => "nouveau",
        CatalogChange.Updated => "mis à jour",
        _ => "inchangé",
    };

    // ---------- lecture du monde entier, et mise à jour ----------

    /// <summary>
    /// Lit dans MSFS les SID et STAR de tous les terrains du simulateur et les enregistre dans les données de VFRNav.
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

        if (MessageBox.Show(this, UpdateQuestion(simVersion), "SID/STAR du monde", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var cts = _procedureCts = new CancellationTokenSource();
        SetReading(true);
        var progress = new Progress<ReadProgress>(p =>
        {
            IfrSimAllButton.Content = $"Arrêter la lecture ({p.Done}/{p.Total})";
            SetProcedureStatus($"Lecture dans MSFS... {p.Done}/{p.Total} ({p.Icao}) : {p.WithProcedures} terrains avec SID/STAR");
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

        if (stopped is not null) SetProcedureStatus("Lecture interrompue : " + stopped + "\nCe qui a été lu avant reste enregistré.");
        else if (result is not null) SetProcedureStatus(Summary(result), result.Cancelled ? Brushes.Gold : Brushes.LightGreen);
    }

    private string UpdateQuestion(string simVersion)
    {
        var origin = _procedures.Origin;
        if (_procedures.AirportCount == 0 || origin is null)
            return $"Lire dans {simVersion} les SID et STAR de tous les terrains du monde ?\n\nCela prend plusieurs minutes ; MSFS doit rester lancé. " +
                   "Les données sont enregistrées dans VFRNav : ensuite le simulateur n'est plus nécessaire.";

        string stored = origin.Value.SimVersion.Length > 0 ? origin.Value.SimVersion : "une version inconnue de MSFS";
        string head = origin.Value.SimVersion == simVersion
            ? $"Les SID/STAR enregistrées ({_procedures.AirportCount} terrains) ont déjà été lues dans {simVersion} le {origin.Value.ReadAt.LocalDateTime:dd/MM/yyyy}. Cette version de MSFS n'a pas changé."
            : $"MSFS a changé de version : les SID/STAR enregistrées viennent de {stored} ({origin.Value.ReadAt.LocalDateTime:dd/MM/yyyy}), le simulateur est maintenant en {simVersion}.";
        return head + "\n\nRelire tous les terrains dans MSFS pour mettre à jour ? Seuls les terrains qui ont changé sont réécrits.";
    }

    private static string Summary(ReadResult r)
    {
        string changes = r.Cancelled ? $"Lecture arrêtée après {r.Read}/{r.Total} terrains." : $"{r.Total} terrains lus dans {r.SimVersion}.";
        return $"{changes}\n{r.New} nouveaux, {r.Updated} mis à jour, {r.Unchanged} inchangés, {r.Removed} retirés (plus de procédures dans MSFS).";
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
