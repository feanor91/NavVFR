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
using VFRNav.Core.Localization;

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
    private ILayer? _coverLayer;
    private ILayer? _topsLayer;
    private DateTimeOffset _coverBuiltAt;
    private DateTimeOffset _topsBuiltAt;
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
        // L'ancienne case unique affichait les deux couches fusionnées : on reprend ce choix une fois, puis on l'oublie.
        bool legacy = _settings.ShowClouds;
        _settings.ShowClouds = false;
        CloudCoverCheck.IsChecked = _settings.ShowCloudCover || legacy;
        CloudTopsCheck.IsChecked = _settings.ShowCloudTops || legacy;
        CloudCoverSwatch.Background = Brush(SatelliteClouds.TintHex(CloudLayerKind.Cover));
        CloudTopsSwatch.Background = Brush(SatelliteClouds.TintHex(CloudLayerKind.Tops));
        RadarOpacitySlider.Value = Math.Clamp(_settings.RadarOpacity, 0.1, 1);
        CloudCoverOpacitySlider.Value = Math.Clamp(_settings.CloudCoverOpacity, 0.1, 1);
        CloudTopsOpacitySlider.Value = Math.Clamp(_settings.CloudTopsOpacity, 0.1, 1);
        BuildRadarLegend();
        _weatherLayerTimer.Tick += async (_, _) => await RefreshWeatherLayersAsync();
        _radarAnimTimer.Tick += (_, _) => StepRadarAnimation();
        Loaded += async (_, _) =>
        {
            _weatherLayersReady = true;
            await RefreshWeatherLayersAsync();
        };
    }

    private async void WeatherLayer_Click(object sender, RoutedEventArgs e) => await RefreshWeatherLayersAsync();

    /// <summary>Remplit la légende : une pastille de couleur par cran de la palette, avec l'intensité en mm/h dessous.</summary>
    private void BuildRadarLegend()
    {
        var fr = VFRNav.Core.Localization.Loc.FormatCulture;
        foreach (var entry in RadarLegend.Rain)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(entry.Hex);
            var swatch = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 1, 0), MinWidth = 25 };
            swatch.Children.Add(new System.Windows.Controls.Border
            {
                Height = 12,
                Background = new System.Windows.Media.SolidColorBrush(color),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0, 0, 0)),
                BorderThickness = new Thickness(0.5),
            });
            swatch.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = entry.MmPerHour < 1 ? entry.MmPerHour.ToString("0.0", fr) : Math.Round(entry.MmPerHour).ToString("0", fr),
                FontSize = 9.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x2A, 0x55)),
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = $"{entry.Intensity} : environ {entry.MmPerHour.ToString("0.#", fr)} mm/h ({entry.Dbz} dBZ)",
            });
            swatch.ToolTip = $"{entry.Intensity} : environ {entry.MmPerHour.ToString("0.#", fr)} mm/h ({entry.Dbz} dBZ)";
            RadarLegendSwatches.Children.Add(swatch);
        }
        RadarLegendWeak.Text = RadarLegend.Rain[0].Intensity;
        RadarLegendStrong.Text = RadarLegend.Rain[^1].Intensity;

        var (light, dark) = RadarLegend.Snow;
        RadarSnowBar.Background = new System.Windows.Media.LinearGradientBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light),
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark), 0);
    }

    private void WeatherOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_weatherLayersReady) return;
        foreach (var layer in _radarLayers) layer.Opacity = RadarOpacitySlider.Value;
        if (_coverLayer is not null) _coverLayer.Opacity = CloudCoverOpacitySlider.Value;
        if (_topsLayer is not null) _topsLayer.Opacity = CloudTopsOpacitySlider.Value;
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
        bool radar = RadarCheck.IsChecked == true, cover = CloudCoverCheck.IsChecked == true, tops = CloudTopsCheck.IsChecked == true;
        bool clouds = cover || tops;

        if (radar) await UpdateRadarAsync();
        else { ClearRadar(); _radarProblem = null; }

        UpdateCloudLayer(CloudLayerKind.Cover, cover);
        UpdateCloudLayer(CloudLayerKind.Tops, tops);

        _weatherLayerTimer.IsEnabled = radar || clouds;
        RadarAnimCheck.IsEnabled = radar;
        RadarLegendBox.Visibility = radar ? Visibility.Visible : Visibility.Collapsed;
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
            _radarProblem = Loc.Get("Weather_Radar_indisponible") + ex.Message;
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
            var source = new HttpTileSource(new GlobalSphericalMercator(0, RadarMaps.MaxZoom, Loc.Get("Weather_Radar")), maps.TileUrlTemplate(frames[i]),
                name: Loc.Get("Weather_Radar_RainViewer"), attribution: new BruTile.Attribution(RadarMaps.AttributionText, RadarMaps.AttributionUrl));
            var layer = new TileLayer(source) { Name = Loc.Get("Weather_Radar"), Opacity = RadarOpacitySlider.Value, Enabled = i == 0 };
            // Au-dessus des nuages et sous les zones : on insère avant les espaces aériens.
            int index = MapControl.Map.Layers.ToList().IndexOf(_sigmetLayer);
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

        ShowWeatherInfo(RadarCheck.IsChecked == true, CloudCoverCheck.IsChecked == true || CloudTopsCheck.IsChecked == true);
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

    private static System.Windows.Media.SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// Ajoute, renouvelle ou retire une couche de nuages. Ordre sur la carte, du bas vers le haut : fond, couverture, sommets froids, radar,
    /// puis zones et route : la couche la plus informative reste au-dessus.
    /// </summary>
    private void UpdateCloudLayer(CloudLayerKind kind, bool on)
    {
        bool cover = kind == CloudLayerKind.Cover;
        if (!on)
        {
            if (cover) SetWeatherLayer(ref _coverLayer, null, _sigmetLayer);
            else SetWeatherLayer(ref _topsLayer, null, _sigmetLayer);
            return;
        }

        var existing = cover ? _coverLayer : _topsLayer;
        var builtAt = cover ? _coverBuiltAt : _topsBuiltAt;
        if (existing is not null && DateTimeOffset.UtcNow - builtAt < CloudRefresh) return;

        ILayer aboveClouds = _radarLayers.Count > 0 ? _radarLayers[0] : _sigmetLayer;
        var layer = new TileLayer(new CloudTileSource(kind))
        {
            Name = cover ? Loc.Get("Weather_Couverture_nuageuse") : Loc.Get("Weather_Sommets_de_nuages"),
            Opacity = cover ? CloudCoverOpacitySlider.Value : CloudTopsOpacitySlider.Value,
        };
        if (cover) { SetWeatherLayer(ref _coverLayer, layer, _topsLayer ?? aboveClouds); _coverBuiltAt = DateTimeOffset.UtcNow; }
        else { SetWeatherLayer(ref _topsLayer, layer, aboveClouds); _topsBuiltAt = DateTimeOffset.UtcNow; }
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
        var fr = VFRNav.Core.Localization.Loc.FormatCulture;
        var lines = new List<string>();
        if (radar)
        {
            if (_radarProblem is not null) lines.Add(_radarProblem);
            else if (_radarLayers.Count == 0) lines.Add(Loc.Get("Weather_Radar_chargement"));
            else if (_radarLayers.Count > 1)
            {
                string Time(RadarFrame f) => f.Time.LocalDateTime.ToString("HH:mm", fr);
                lines.Add(Loc.F("Weather_Radar_anime_image_de", Time(_radarFrames[0]), Time(_radarFrames[^1]), Time(_radarFrames[_animIndex])));
            }
            else lines.Add(Loc.F("Weather_Radar_image_de_il_y", _radarFrames[0].Time.LocalDateTime, Math.Max(0, (int)_radarAge.TotalMinutes)));
        }
        if (CloudCoverCheck.IsChecked == true)
            lines.Add(Loc.Get("Weather_Couverture_gris_bleu_ou_il"));
        if (CloudTopsCheck.IsChecked == true)
            lines.Add(Loc.Get("Weather_Sommets_froids_violet_nuages_epais"));
        if (clouds)
            lines.Add(Loc.Get("Weather_Images_satellite_la_plus_recente"));
        WeatherInfo.Text = string.Join("\n", lines);
    }
}
