using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Navigation;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using VFRNav.Core.Weather;

namespace VFRNav.App;

// ---------- Météo en temps réel sur la carte : radar de précipitations (RainViewer) et nuages (satellite EUMETSAT) ----------
public partial class MainWindow
{
    private ILayer? _radarLayer;
    private ILayer? _cloudLayer;
    private RadarFrame? _radarShown;
    private DateTimeOffset _cloudBuiltAt;
    private string? _radarProblem;
    private bool _weatherLayersReady;
    private bool _weatherBusy;
    private readonly System.Windows.Threading.DispatcherTimer _weatherLayerTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    /// <summary>Les images satellite sortent toutes les 10 minutes : inutile de les redemander plus souvent.</summary>
    private static readonly TimeSpan CloudRefresh = TimeSpan.FromMinutes(9);

    private void InitWeatherLayers()
    {
        RadarCheck.IsChecked = _settings.ShowRadar;
        CloudCheck.IsChecked = _settings.ShowClouds;
        RadarOpacitySlider.Value = Math.Clamp(_settings.RadarOpacity, 0.1, 1);
        CloudOpacitySlider.Value = Math.Clamp(_settings.CloudOpacity, 0.1, 1);
        _weatherLayerTimer.Tick += async (_, _) => await RefreshWeatherLayersAsync();
        Loaded += async (_, _) =>
        {
            _weatherLayersReady = true;
            await RefreshWeatherLayersAsync();
        };
    }

    private async void WeatherLayer_Click(object sender, RoutedEventArgs e) => await RefreshWeatherLayersAsync();

    private void WeatherOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_weatherLayersReady) return;
        if (_radarLayer is not null) _radarLayer.Opacity = RadarOpacitySlider.Value;
        if (_cloudLayer is not null) _cloudLayer.Opacity = CloudOpacitySlider.Value;
        MapControl.Refresh();
    }

    private void WeatherLink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>Ajoute, remplace ou retire les couches selon les cases cochées, puis relance la mise à jour toutes les 5 minutes.</summary>
    private async Task RefreshWeatherLayersAsync()
    {
        if (!_weatherLayersReady || _weatherBusy) return;
        _weatherBusy = true;
        try
        {
            bool radar = RadarCheck.IsChecked == true, clouds = CloudCheck.IsChecked == true;

            if (radar) await UpdateRadarAsync();
            else { SetWeatherLayer(ref _radarLayer, null, _airspaceLayer); _radarShown = null; _radarProblem = null; }

            if (clouds) UpdateClouds();
            else SetWeatherLayer(ref _cloudLayer, null, _airspaceLayer);

            _weatherLayerTimer.IsEnabled = radar || clouds;
            ShowWeatherInfo(radar, clouds);
            MapControl.Refresh();
        }
        finally { _weatherBusy = false; }
    }

    private async Task UpdateRadarAsync()
    {
        RadarMaps maps;
        try { maps = RadarMaps.Parse(await _http.GetStringAsync(RadarMaps.IndexUrl)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            // On garde l'image déjà affichée : une coupure réseau passagère ne doit pas vider la carte.
            _radarProblem = "Radar indisponible : " + ex.Message;
            return;
        }
        _radarProblem = null;
        _radarAge = maps.Age(DateTimeOffset.UtcNow);

        var latest = maps.Latest;
        if (_radarLayer is not null && _radarShown == latest) return;

        var source = new HttpTileSource(new GlobalSphericalMercator(0, RadarMaps.MaxZoom, "Radar"), maps.TileUrlTemplate(latest),
            name: "Radar RainViewer", attribution: new BruTile.Attribution(RadarMaps.AttributionText, RadarMaps.AttributionUrl));
        SetWeatherLayer(ref _radarLayer, new TileLayer(source) { Name = "Radar", Opacity = RadarOpacitySlider.Value }, _airspaceLayer);
        _radarShown = latest;
        _radarTime = latest.Time;
    }

    private TimeSpan _radarAge;
    private DateTimeOffset _radarTime;

    private void UpdateClouds()
    {
        if (_cloudLayer is not null && DateTimeOffset.UtcNow - _cloudBuiltAt < CloudRefresh) return;
        // Les nuages vont sous le radar : au-dessus d'eux, mais sous les zones et la route.
        SetWeatherLayer(ref _cloudLayer, new TileLayer(new CloudTileSource()) { Name = "Nuages", Opacity = CloudOpacitySlider.Value }, _radarLayer ?? _airspaceLayer);
        _cloudBuiltAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Remplace la couche d'un emplacement (la retire si <paramref name="layer"/> est nul) en la plaçant juste avant <paramref name="anchor"/>.</summary>
    private void SetWeatherLayer(ref ILayer? slot, ILayer? layer, ILayer anchor)
    {
        var layers = MapControl.Map.Layers;
        if (slot is not null)
        {
            layers.Remove(slot);
            (slot as IDisposable)?.Dispose();
        }
        slot = layer;
        if (layer is null) return;
        int index = layers.ToList().IndexOf(anchor);
        layers.Insert(index < 0 ? layers.Count() : index, layer);
    }

    private void ShowWeatherInfo(bool radar, bool clouds)
    {
        var lines = new List<string>();
        if (radar)
            lines.Add(_radarProblem ?? (_radarLayer is null ? "Radar : chargement..." : string.Create(CultureInfo.GetCultureInfo("fr-FR"),
                $"Radar : image de {_radarTime.LocalDateTime:HH:mm} (il y a {Math.Max(0, (int)_radarAge.TotalMinutes)} min), mise à jour toutes les 5 min.")));
        if (clouds)
            lines.Add("Nuages : image infrarouge la plus récente (10 à 20 min de décalage). Les nuages bas et le brouillard y sont peu visibles.");
        WeatherInfo.Text = string.Join("\n", lines);
    }
}
