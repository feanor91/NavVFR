using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Navigation;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using VFRNav.Core.Weather;

namespace VFRNav.App;

// ---------- Météo en temps réel sur la carte : radar de précipitations (RainViewer, animable) et nuages (satellite EUMETSAT) ----------
public partial class MainWindow
{
    /// <summary>Images de l'animation du radar : les 4 dernières, soit 30 minutes (une image toutes les 10 minutes).</summary>
    private const int RadarAnimationFrames = 4;

    /// <summary>Les images satellite sortent toutes les 10 minutes : inutile de les redemander plus souvent.</summary>
    private static readonly TimeSpan CloudRefresh = TimeSpan.FromMinutes(9);

    private readonly List<ILayer> _radarLayers = [];
    private IReadOnlyList<RadarFrame> _radarFrames = [];
    private ILayer? _cloudLayer;
    private DateTimeOffset _cloudBuiltAt;
    private TimeSpan _radarAge;
    private string? _radarProblem;
    private bool _weatherLayersReady;
    private bool _weatherBusy;
    private bool _weatherRerun;
    private int _animIndex;
    private bool _animFirstLoop;

    private readonly System.Windows.Threading.DispatcherTimer _weatherLayerTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly System.Windows.Threading.DispatcherTimer _radarAnimTimer = new();

    private void InitWeatherLayers()
    {
        RadarCheck.IsChecked = _settings.ShowRadar;
        CloudCheck.IsChecked = _settings.ShowClouds;
        RadarOpacitySlider.Value = Math.Clamp(_settings.RadarOpacity, 0.1, 1);
        CloudOpacitySlider.Value = Math.Clamp(_settings.CloudOpacity, 0.1, 1);
        _weatherLayerTimer.Tick += async (_, _) => await RefreshWeatherLayersAsync();
        _radarAnimTimer.Tick += (_, _) => StepRadarAnimation();
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
        foreach (var layer in _radarLayers) layer.Opacity = RadarOpacitySlider.Value;
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
        if (!_weatherLayersReady) return;
        if (_weatherBusy) { _weatherRerun = true; return; }   // un clic pendant un chargement : on recommence juste après
        _weatherBusy = true;
        try
        {
            do
            {
                _weatherRerun = false;
                await ApplyWeatherLayersAsync();
            } while (_weatherRerun);
        }
        finally { _weatherBusy = false; }
    }

    private async Task ApplyWeatherLayersAsync()
    {
        bool radar = RadarCheck.IsChecked == true, clouds = CloudCheck.IsChecked == true;

        if (radar) await UpdateRadarAsync();
        else { ClearRadar(); _radarProblem = null; }

        if (clouds) UpdateClouds();
        else SetWeatherLayer(ref _cloudLayer, null, _airspaceLayer);

        _weatherLayerTimer.IsEnabled = radar || clouds;
        RadarAnimCheck.IsEnabled = radar;
        ShowWeatherInfo(radar, clouds);
        MapControl.Refresh();
    }

    private async Task UpdateRadarAsync()
    {
        RadarMaps maps;
        try { maps = RadarMaps.Parse(await _http.GetStringAsync(RadarMaps.IndexUrl)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            // On garde les images déjà affichées : une coupure réseau passagère ne doit pas vider la carte.
            _radarProblem = "Radar indisponible : " + ex.Message;
            return;
        }
        _radarProblem = null;
        _radarAge = maps.Age(DateTimeOffset.UtcNow);

        bool animate = RadarAnimCheck.IsChecked == true;
        var frames = animate ? maps.LastFrames(RadarAnimationFrames) : [maps.Latest];
        if (_radarLayers.Count > 0 && _radarFrames.SequenceEqual(frames)) return;   // rien de nouveau depuis la dernière fois

        ClearRadar();
        for (int i = 0; i < frames.Count; i++)
        {
            var source = new HttpTileSource(new GlobalSphericalMercator(0, RadarMaps.MaxZoom, "Radar"), maps.TileUrlTemplate(frames[i]),
                name: "Radar RainViewer", attribution: new BruTile.Attribution(RadarMaps.AttributionText, RadarMaps.AttributionUrl));
            var layer = new TileLayer(source) { Name = "Radar", Opacity = RadarOpacitySlider.Value, Enabled = i == 0 };
            // Au-dessus des nuages et sous les zones : on insère avant les espaces aériens.
            int index = MapControl.Map.Layers.ToList().IndexOf(_airspaceLayer);
            MapControl.Map.Layers.Insert(index < 0 ? MapControl.Map.Layers.Count() : index, layer);
            _radarLayers.Add(layer);
        }
        _radarFrames = frames;

        if (animate) StartRadarAnimation(); else StopRadarAnimation(showLatest: true);
    }

    // ---------- animation du radar ----------

    /// <summary>
    /// Une seule image est affichée à la fois : les autres couches sont désactivées et ne demandent aucune tuile.
    /// La première boucle est plus lente pour étaler les téléchargements (RainViewer limite le nombre de requêtes) ;
    /// ensuite les images sont en mémoire et la boucle va à vitesse normale.
    /// </summary>
    private void StartRadarAnimation()
    {
        _animIndex = 0;
        _animFirstLoop = true;
        _radarAnimTimer.Interval = TimeSpan.FromMilliseconds(1600);
        _radarAnimTimer.Start();
    }

    private void StopRadarAnimation(bool showLatest)
    {
        _radarAnimTimer.Stop();
        if (showLatest && _radarLayers.Count > 0)
            for (int i = 0; i < _radarLayers.Count; i++) _radarLayers[i].Enabled = i == _radarLayers.Count - 1;
    }

    private void StepRadarAnimation()
    {
        if (_radarLayers.Count < 2) { _radarAnimTimer.Stop(); return; }
        _radarLayers[_animIndex].Enabled = false;
        _animIndex = (_animIndex + 1) % _radarLayers.Count;
        _radarLayers[_animIndex].Enabled = true;

        bool last = _animIndex == _radarLayers.Count - 1;
        if (last) _animFirstLoop = false;
        // Pause sur l'image la plus récente ; première boucle plus lente que les suivantes.
        _radarAnimTimer.Interval = TimeSpan.FromMilliseconds(last ? 2000 : _animFirstLoop ? 1600 : 700);

        ShowWeatherInfo(RadarCheck.IsChecked == true, CloudCheck.IsChecked == true);
        MapControl.Refresh();
    }

    private void RadarAnim_Click(object sender, RoutedEventArgs e) => _ = RefreshWeatherLayersAsync(rebuildRadar: true);

    private async Task RefreshWeatherLayersAsync(bool rebuildRadar)
    {
        if (rebuildRadar) { StopRadarAnimation(showLatest: false); ClearRadar(); }
        await RefreshWeatherLayersAsync();
    }

    private void ClearRadar()
    {
        _radarAnimTimer.Stop();
        foreach (var layer in _radarLayers)
        {
            MapControl.Map.Layers.Remove(layer);
            (layer as IDisposable)?.Dispose();
        }
        _radarLayers.Clear();
        _radarFrames = [];
    }

    // ---------- nuages ----------

    private void UpdateClouds()
    {
        if (_cloudLayer is not null && DateTimeOffset.UtcNow - _cloudBuiltAt < CloudRefresh) return;
        // Les nuages vont sous le radar : au-dessus du fond, mais sous les zones et la route.
        ILayer anchor = _radarLayers.Count > 0 ? _radarLayers[0] : _airspaceLayer;
        SetWeatherLayer(ref _cloudLayer, new TileLayer(new CloudTileSource()) { Name = "Nuages", Opacity = CloudOpacitySlider.Value }, anchor);
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
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var lines = new List<string>();
        if (radar)
        {
            if (_radarProblem is not null) lines.Add(_radarProblem);
            else if (_radarLayers.Count == 0) lines.Add("Radar : chargement...");
            else if (_radarLayers.Count > 1)
            {
                string Time(RadarFrame f) => f.Time.LocalDateTime.ToString("HH:mm", fr);
                lines.Add($"Radar animé : {Time(_radarFrames[0])} → {Time(_radarFrames[^1])}, image de {Time(_radarFrames[_animIndex])}.");
            }
            else lines.Add($"Radar : image de {_radarFrames[0].Time.LocalDateTime:HH:mm} (il y a {Math.Max(0, (int)_radarAge.TotalMinutes)} min), mise à jour toutes les 5 min.");
        }
        if (clouds)
            lines.Add("Nuages : image infrarouge la plus récente (10 à 20 min de décalage). Les nuages bas et le brouillard y sont peu visibles.");
        WeatherInfo.Text = string.Join("\n", lines);
    }
}
