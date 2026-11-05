using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Attente : grise et bloque l'interface pendant un calcul ou une mise à jour longue (pas pour le temps réel) ----------
public partial class MainWindow
{
    private int _busyCount;
    private Action? _busyCancel;

    /// <summary>Fin de l'attente : <c>using var busy = BeginBusy(...)</c> la lève à la sortie du bloc.</summary>
    private sealed class BusyScope(MainWindow window) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            window.EndBusy();
        }
    }

    /// <summary>
    /// Affiche l'attente : fond grisé, tourniquet et <paramref name="message"/>, interface bloquée. Avec <paramref name="cancel"/>, un bouton permet d'arrêter.
    /// Les attentes peuvent s'emboîter : l'interface n'est libérée qu'à la fin de la dernière.
    /// </summary>
    private IDisposable BeginBusy(string message, Action? cancel = null)
    {
        _busyCount++;
        BusyMessage.Text = message;
        BusyDetail.Text = Loc.Get("Busy_Veuillez_patienter");
        _busyCancel = cancel;
        BusyCancel.IsEnabled = true;
        BusyCancel.Content = Loc.Get("Busy_Arreter");
        BusyCancel.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        if (_busyCount == 1)
        {
            MainContent.IsEnabled = false;
            BusyOverlay.Visibility = Visibility.Visible;
            BusyOverlay.Focus();
            SpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        }
        return new BusyScope(this);
    }

    private void EndBusy()
    {
        if (_busyCount == 0) return;
        if (--_busyCount > 0) return;
        SpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        BusyOverlay.Visibility = Visibility.Collapsed;
        MainContent.IsEnabled = true;
        _busyCancel = null;
        System.Windows.Input.Mouse.OverrideCursor = null;
    }

    /// <summary>Précise l'étape en cours sous le message (progression, terrain lu...). Sans effet hors attente.</summary>
    private void SetBusyDetail(string text)
    {
        if (_busyCount > 0 && !string.IsNullOrWhiteSpace(text)) BusyDetail.Text = text;
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e)
    {
        BusyCancel.IsEnabled = false;
        BusyDetail.Text = Loc.Get("Busy_Arret_en_cours");
        _busyCancel?.Invoke();
    }

    /// <summary>Laisse l'interface se redessiner (l'attente apparaît) avant un travail qui occupe le fil de l'interface.</summary>
    private Task FlushUiAsync() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;
}
