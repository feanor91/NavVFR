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

    private static readonly TimeSpan AirspaceCacheAge = TimeSpan.FromDays(7);

    /// <summary>Pays saisis dans le champ « Pays » (plusieurs possibles : « FR GB »).</summary>
    private IReadOnlyList<string> TypedCountries() => RouteCountries.Parse(CountryBox.Text);

    /// <summary>Pays à charger : ceux saisis, puis ceux que traverse la route.</summary>
    private IReadOnlyList<string> WantedCountries() =>
        RouteCountries.Wanted(TypedCountries(), RouteCountries.Along(_route.ToList(), _airports));

    /// <summary>
    /// Charge les balises et les espaces aériens des pays voulus. Un pays déjà vu n'est pas redemandé (sauf <paramref name="refresh"/>) ;
    /// les zones viennent du cache sur disque tant qu'il a moins de sept jours. Sans clé OpenAIP, seules les balises sont chargées.
    /// </summary>
    private async Task LoadCountryDataAsync(bool refresh)
    {
        if (_countriesBusy) return;
        var wanted = WantedCountries();
        if (wanted.Count == 0) return;
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
            foreach (string country in wanted.Where(c => !_airspacesByCountry.ContainsKey(c)))
            {
                StatusText.Text = Loc.F("Countries_Chargement_des_espaces_aeriens", country);
                _airspacesByCountry[country] = await service.LoadCachedAsync(country, key, AirspaceCacheFolder, AirspaceCacheAge, refresh);
            }
            _loadedCountries = wanted;
            _airspaces = [.. wanted.SelectMany(c => _airspacesByCountry.TryGetValue(c, out var zones) ? zones : [])];
            ApplyAirspaceFilter();
            StatusText.Text = Loc.F("xaml_terrains_espaces_aeriens", _airports.Count, _airspaces.Count, string.Join(" ", wanted)) + AerodromeSummary();
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
        if (_airports.Count == 0 || _countriesBusy) return;
        var wanted = WantedCountries();
        if (wanted.Count == 0 || wanted.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_loadedCountries)) return;
        await LoadCountryDataAsync(refresh: false);
    }
}
