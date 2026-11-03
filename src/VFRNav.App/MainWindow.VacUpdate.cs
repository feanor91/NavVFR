using System.IO;
using System.Windows;
using VFRNav.Core.Aerodromes;

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
            $"Mettre à jour les cartes VAC depuis le site du SIA (cycle AIRAC du {cycle:dd/MM/yyyy}) ?\n\nDossier : {dir}\n\n" +
            $"Oui : mettre à jour les {present.Count} cartes du dossier et télécharger les {missing.Count} qui manquent (plusieurs minutes, une carte à la fois).\n" +
            "Non : seulement mettre à jour celles que tu as déjà.\nAnnuler : ne rien faire.\n\n" +
            "Une carte n'est remplacée que si le SIA en publie une différente. Cartes © SIA, pour la simulation uniquement.",
            "Cartes VAC", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        var icaos = VacChartUpdater.Wanted(dir, answer == MessageBoxResult.Yes ? missing : null);
        if (icaos.Count == 0)
        {
            StatusText.Text = "Aucune carte à mettre à jour : le dossier n'en contient pas et rien n'est demandé en téléchargement.";
            return;
        }

        using var cts = _vacUpdateCts = new CancellationTokenSource();
        VacUpdateButton.Content = "Arrêter la mise à jour des cartes";
        var progress = new Progress<string>(text => StatusText.Text = text);
        VacUpdateResult? result = null;
        string? failure = null;
        try { result = await new VacChartUpdater(_http).UpdateAsync(dir, icaos, cycle, false, progress, cts.Token); }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = "Écriture impossible dans le dossier des cartes : " + ex.Message; }
        finally
        {
            _vacUpdateCts = null;
            VacUpdateButton.Content = "Mettre à jour les cartes VAC (SIA)";
        }

        if (failure is not null) { StatusText.Text = failure; return; }
        string note = result!.Changed + result.Added > 0
            ? "\nLes profils VFR de MSFS Rhumb ont été relevés sur les cartes d'un cycle précédent : à vérifier sur les cartes modifiées."
            : "";
        await LoadDataAsync(); // relit la base : les cartes nouvelles apparaissent
        StatusText.Text += "\n" + result.Summary + note;
    }
}
