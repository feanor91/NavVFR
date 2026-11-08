using System.IO;
using System.Net.Http;
using VFRNav.Core.Localization;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.App;

// ---------- Espaces aériens et balises de plusieurs pays : ceux saisis et ceux que traverse la route ----------
public partial class MainWindow
{
    /// <summary>Zones de chaque pays déjà chargé (mémoire) : un pays n'est demandé qu'une fois, puis relu du cache sur disque.</summary>
    private readonly Dictionary<string, IReadOnlyList<Airspace>> _airspacesByCountry = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> _loadedCountries = [];
    private bool _countriesBusy;

    private static string AirspaceCacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFRNav", "cache", "airspaces");

    private static readonly TimeSpan AirspaceCacheAge = TimeSpan.FromDays(28);

    /// <summary>Pays saisis dans le champ « Pays » (plusieurs possibles : « FR GB »).</summary>
    private IReadOnlyList<string> TypedCountries() => RouteCountries.Parse(CountryBox.Text);

    /// <summary>Pays à charger : ceux saisis, puis ceux que traverse la route.</summary>
    private IReadOnlyList<string> WantedCountries() =>
        RouteCountries.Wanted(TypedCountries(), RouteCountries.Along(_route.ToList(), _airports));

    private readonly System.Windows.Threading.DispatcherTimer _countryTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private bool _countryTimerHooked;

    /// <summary>Un pays saisi dans le champ « Pays » des Données : on recharge zones et balises peu après la dernière frappe.</summary>
    private void CountryText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _airports.Count == 0) return;
        if (!_countryTimerHooked)
        {
            _countryTimerHooked = true;
            _countryTimer.Tick += async (_, _) => { _countryTimer.Stop(); await LoadCountriesIfRouteChangedAsync(); };
        }
        _countryTimer.Stop();
        _countryTimer.Start();
    }

    /// <summary>
    /// Charge les balises et les espaces aériens des pays voulus. Un pays déjà vu n'est pas redemandé (sauf <paramref name="refresh"/>) ;
    /// les zones viennent du cache sur disque tant qu'il a moins de 28 jours. Sans clé OpenAIP, seules les balises sont chargées.
    /// </summary>
    private async Task LoadCountryDataAsync(bool refresh)
    {
        if (_countriesBusy) { _countryTimer.Stop(); _countryTimer.Start(); return; }
        var wanted = WantedCountries();
        if (wanted.Count == 0)
        {
            // Champ vide et route hors de tout pays : plus aucune zone ni balise à montrer.
            if (_loadedCountries.Count == 0 && _airspaces.Count == 0) return;
            _loadedCountries = []; _navaidCountries = []; _navaids = []; _airspaces = [];
            ApplyNavaidFilter();
            ApplyAirspaceFilter();
            StatusText.Text = Loc.F("xaml_terrains_espaces_aeriens", _airports.Count, 0, "") + AerodromeSummary();
            return;
        }
        _countriesBusy = true;
        try
        {
            if (refresh) _airspacesByCountry.Clear();

            if (refresh || !wanted.SequenceEqual(_navaidCountries, StringComparer.OrdinalIgnoreCase))
            {
                await LoadNavaidsAsync(wanted);
                _navaidCountries = wanted;
            }

            string key = KeyBox.Password.Trim();
            if (key.Length == 0) { _loadedCountries = wanted; return; }

            var service = new OpenAipService(_http);
            var failed = new List<string>();
            bool stale = false;
            var missing = wanted.Where(c => !_airspacesByCountry.ContainsKey(c)).ToList();
            for (int i = 0; i < missing.Count; i++)
            {
                string country = missing[i];
                if (!WantedCountries().SequenceEqual(wanted)) { stale = true; break; }   // saisie modifiée pendant le chargement : on repart
                StatusText.Text = Loc.F("Countries_Chargement_des_espaces_aeriens_n", country, i + 1, missing.Count);
                try { _airspacesByCountry[country] = await service.LoadCachedAsync(country, key, AirspaceCacheFolder, AirspaceCacheAge, refresh); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException) { failed.Add(country); }
            }
            if (stale) { _countryTimer.Stop(); _countryTimer.Start(); return; }
            _loadedCountries = wanted;
            _airspaces = [.. wanted.SelectMany(c => _airspacesByCountry.TryGetValue(c, out var zones) ? zones : [])];
            ApplyAirspaceFilter();
            StatusText.Text = Loc.F("xaml_terrains_espaces_aeriens", _airports.Count, _airspaces.Count, string.Join(" ", wanted.Count > 12 ? [.. wanted.Take(12), "…"] : wanted)) + AerodromeSummary()
                + (failed.Count > 0 ? " " + Loc.F("Countries_Echec", failed.Count, string.Join(" ", failed)) : "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            StatusText.Text = Loc.Get("xaml_Erreur_de_chargement") + ex.Message;
        }
        finally { _countriesBusy = false; }
    }

    private IReadOnlyList<string> _navaidCountries = [];

    /// <summary>Quand la route sort des pays chargés, charge ceux qui manquent (zones et balises), sans bloquer l'interface.</summary>
    private async Task LoadCountriesIfRouteChangedAsync()
    {
        if (_airports.Count == 0) return;
        var wanted = WantedCountries();
        if (wanted.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_loadedCountries)) return;
        await LoadCountryDataAsync(refresh: false);
    }
}
