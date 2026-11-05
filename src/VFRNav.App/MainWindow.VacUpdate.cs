using System.IO;
using System.Windows;
using VFRNav.Core.Aerodromes;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Mise à jour des cartes VAC depuis le site du SIA (à la demande, jamais automatique) ----------
public partial class MainWindow
{
    private CancellationTokenSource? _vacUpdateCts;

    private async void VacUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_vacUpdateCts is not null)
        {
            _vacUpdateCts.Cancel(); // second clic : arrêt
            return;
        }

        string dir = AerodromePaths.ResolveChartsDir(VacBox.Text);
        var cycle = VacChartUpdater.CycleFor(DateTime.UtcNow);
        var present = VacChartUpdater.Wanted(dir, null);
        var missing = _aerodromes?.All.Where(a => a.VacListed && !a.HasChart).Select(a => a.Icao).ToList() ?? [];

        var answer = MessageBox.Show(this,
            Loc.F("VacUpdate_Mettre_a_jour_les_cartes", cycle, dir) +
            Loc.F("VacUpdate_Oui_mettre_a_jour_les", present.Count, missing.Count) +
            Loc.Get("VacUpdate_Non_seulement_mettre_a_jour") +
            Loc.Get("VacUpdate_Une_carte_n_est_remplacee"),
            Loc.Get("Ui_Cartes_VAC"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        var icaos = VacChartUpdater.Wanted(dir, answer == MessageBoxResult.Yes ? missing : null);
        if (icaos.Count == 0)
        {
            StatusText.Text = Loc.Get("VacUpdate_Aucune_carte_a_mettre_a");
            return;
        }

        using var cts = _vacUpdateCts = new CancellationTokenSource();
        using var busy = BeginBusy(Loc.Get("Busy_Mise_a_jour_cartes"), () => cts.Cancel());
        VacUpdateButton.Content = Loc.Get("VacUpdate_Arreter_la_mise_a_jour");
        var progress = new Progress<string>(text => { StatusText.Text = text; SetBusyDetail(text); });
        VacUpdateResult? result = null;
        string? failure = null;
        try { result = await new VacChartUpdater(_http).UpdateAsync(dir, icaos, cycle, false, progress, cts.Token); }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = Loc.Get("VacUpdate_Ecriture_impossible_dans_le_dossier") + ex.Message; }
        finally
        {
            _vacUpdateCts = null;
            VacUpdateButton.Content = Loc.Get("Ui_Mettre_a_jour_les_cartes");
        }

        if (failure is not null) { StatusText.Text = failure; return; }
        string note = result!.Changed + result.Added > 0
            ? Loc.Get("VacUpdate_Les_profils_VFR_de_MSFS")
            : "";
        await LoadDataAsync(); // relit la base : les cartes nouvelles apparaissent
        StatusText.Text += "\n" + result.Summary + note;
    }
}
