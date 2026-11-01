using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Microsoft.Win32;
using NetTopologySuite.Geometries;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.StormAtc;
using VFRNav.Core.Weather;

namespace VFRNav.App;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly ObservableCollection<Waypoint> _route = [];
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MemoryLayer _airspaceLayer = new("Espaces aériens");
    private readonly MemoryLayer _airportLayer = new("Terrains");
    private readonly MemoryLayer _routeLayer = new("Route");
    private readonly MemoryLayer _waypointLayer = new("Points");
    private readonly MemoryLayer _aircraftLayer = new("Avion");
    private readonly MemoryLayer _navaidLayer = new("Balises");
    private readonly SimConnectTracker _tracker = new();
    private IReadOnlyList<Airport> _airports = [];
    private IReadOnlyList<Airspace> _airspaces = [];
    private readonly Dictionary<string, (string? Category, double? VisibilityM)> _wx = new(StringComparer.OrdinalIgnoreCase);
    private List<ProjectedZone> _projected = [];
    private readonly System.Windows.Threading.DispatcherTimer _viewTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private (double X, double Y, double Resolution) _lastView;
    private bool _wxDirty = true;
    private bool _wxBusy;
    private IReadOnlyList<Airspace> _shownAirspaces = [];
    private readonly System.Windows.Threading.DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private System.Windows.Point _mouse;
    private StormAtcRepository? _stormAtc;
    private readonly PdfPageRenderer _pdf = new();
    private StormAtcAerodrome? _shown;
    private uint _page;
    private double _zoom = 1.0;
    private bool _vacVisible;
    private bool _vacLarge;
    private double _vacWidth = 520, _vacHeight = 320, _tabsHeight = 230;
    private readonly System.Windows.Threading.DispatcherTimer _vacResizeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _loadingParams;
    private ILayer[] _baseLayers = [];

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VFRNav/0.1");

        _loadingParams = true;
        TasBox.Text = Fmt(_settings.TasKt);
        BurnBox.Text = Fmt(_settings.BurnLph);
        AltBox.Text = Fmt(_settings.CruiseAltFt);
        VarBox.Text = Fmt(_settings.VariationEast);
        WdirBox.Text = Fmt(_settings.WindDir);
        WspdBox.Text = Fmt(_settings.WindKt);
        CountryBox.Text = _settings.Country;
        StormBox.Text = _settings.StormAtcPath;
        AutoVarCheck.IsChecked = _settings.AutoVariation;
        WxDotsCheck.IsChecked = _settings.WxDots;
        NavVorCheck.IsChecked = _settings.ShowVor;
        NavNdbCheck.IsChecked = _settings.ShowNdb;
        ZoneHelpCheck.IsChecked = _settings.ZoneHelp;
        ZoneHelp.Visibility = _settings.ZoneHelp ? Visibility.Visible : Visibility.Collapsed;
        ZoneHelpText.Text = ZoneHelpIntro;
        ARestricted.IsChecked = _settings.AirspaceFilter.Restricted;
        AControlled.IsChecked = _settings.AirspaceFilter.Controlled;
        ARadio.IsChecked = _settings.AirspaceFilter.Radio;
        ALeisure.IsChecked = _settings.AirspaceFilter.Leisure;
        ASectors.IsChecked = _settings.AirspaceFilter.Sectors;
        AFloorCheck.IsChecked = _settings.AirspaceFilter.MaxFloorFt is not null;
        AFloorBox.Text = Fmt(_settings.AirspaceFilter.MaxFloorFt ?? 11500);
        BaseMapBox.SelectedIndex = Math.Clamp(Array.IndexOf(BaseMaps.Keys, _settings.BaseMap), 0, BaseMaps.Keys.Length - 1);
        FWithCode.IsChecked = _settings.Filter.WithCode;
        FWithoutCode.IsChecked = _settings.Filter.WithoutCode;
        FVacOnly.IsChecked = _settings.Filter.VacOnly;
        FLarge.IsChecked = _settings.Filter.Large;
        FMedium.IsChecked = _settings.Filter.Medium;
        FSmall.IsChecked = _settings.Filter.Small;
        KeyBox.Password = _settings.OpenAipKey;
        SetKeyLocked(_settings.OpenAipKey.Length > 0);
        CartoKeyBox.Password = _settings.CartoKey;
        SetCartoLocked(_settings.CartoKey.Length > 0);
        NotamKeyBox.Password = _settings.NotamKey;
        SetNotamLocked(_settings.NotamKey.Length > 0);
        _vacVisible = _settings.VacVisible;
        _loadingParams = false;

        foreach (var wp in _settings.Route) _route.Add(wp);
        RouteGrid.ItemsSource = _route;
        _route.CollectionChanged += OnRouteChanged;

        MapRenderer.RegisterStyleRenderer(typeof(AirspaceStyle), new AirspaceRenderer());
        var map = MapControl.Map;
        _baseLayers = BaseMaps.Create(BaseMaps.Keys[BaseMapBox.SelectedIndex], _settings.CartoKey);
        foreach (var layer in _baseLayers) map.Layers.Add(layer);
        map.Layers.Add(_airspaceLayer);
        map.Layers.Add(_navaidLayer);
        map.Layers.Add(_reportLayer);
        map.Layers.Add(_routeLayer);
        map.Layers.Add(_airportLayer);
        map.Layers.Add(_waypointLayer);
        map.Layers.Add(_aircraftLayer);
        map.Tapped += OnMapTapped;
        MapControl.MouseMove += OnMapMouseMove;
        MapControl.PreviewMouseLeftButtonDown += OnDragStart;
        MapControl.PreviewMouseMove += OnDragMove;
        MapControl.PreviewMouseLeftButtonUp += OnDragEnd;
        MapControl.MouseLeave += (_, _) => HideTip();
        MapControl.MouseDown += (_, _) => HideTip();
        MapControl.MouseWheel += (_, _) => HideTip();
        _tipTimer.Tick += (_, _) => ShowTip();
        _viewTimer.Tick += (_, _) => ViewTick();
        _viewTimer.Start();
        InitLive();
        _vacResizeTimer.Tick += (_, _) => { _vacResizeTimer.Stop(); if (_vacVisible) _ = RenderVacAsync(); };
        ApplyVacLayout(capture: false);
        if (_vacVisible) _ = RenderVacAsync();

        Loaded += async (_, _) =>
        {
            var (x, y) = SphericalMercator.FromLonLat(2.35, 48.75);
            map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), 600);
            RefreshRoute();
            await LoadDataAsync();
        };
        Closing += (_, _) => { SaveSettings(); _tracker.Dispose(); };
        SourceInitialized += (_, _) => StartTracker();
    }

    private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static double Parse(TextBox box, double fallback) =>
        double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private FlightParameters ReadParams() => new(
        Math.Max(Parse(TasBox, 100), 1), Parse(BurnBox, 0), Parse(AltBox, 2500), Parse(VarBox, 0), Parse(WdirBox, 0), Parse(WspdBox, 0));

    private void SaveSettings()
    {
        var p = ReadParams();
        _settings.Country = CountryBox.Text.Trim().ToUpperInvariant();
        _settings.OpenAipKey = KeyBox.Password.Trim();
        _settings.CartoKey = CartoKeyBox.Password.Trim();
        _settings.NotamKey = NotamKeyBox.Password.Trim();
        _settings.StormAtcPath = StormBox.Text.Trim();
        _settings.AutoVariation = AutoVarCheck.IsChecked == true;
        _settings.WxDots = WxDotsCheck.IsChecked == true;
        _settings.ShowVor = NavVorCheck.IsChecked == true;
        _settings.ShowNdb = NavNdbCheck.IsChecked == true;
        _settings.ZoneHelp = ZoneHelpCheck.IsChecked == true;
        _settings.Filter = ReadFilter();
        _settings.AirspaceFilter = ReadAirspaceFilter();
        _settings.VacVisible = _vacVisible;
        _settings.BaseMap = BaseMaps.Keys[Math.Max(BaseMapBox.SelectedIndex, 0)];
        _settings.TasKt = p.TasKt; _settings.BurnLph = p.BurnLph; _settings.CruiseAltFt = p.CruiseAltFt;
        _settings.VariationEast = p.VariationEast; _settings.WindDir = p.WindDir; _settings.WindKt = p.WindKt;
        _settings.Route = [.. _route];
        try { _settings.Save(); } catch (IOException) { /* non bloquant */ }
    }

    // ---------- Données ----------
    private async Task LoadDataAsync()
    {
        string country = CountryBox.Text.Trim().ToUpperInvariant();
        if (country.Length != 2) { StatusText.Text = "Code pays invalide."; return; }
        try
        {
            StatusText.Text = "Chargement des terrains...";
            var baseAirports = await new OurAirportsService(_http).LoadAsync(country);
            _stormAtc = await LoadStormAtcAsync();
            ApplyReportingPoints();
            _airports = AirportCatalog.Merge(baseAirports, _stormAtc);
            ApplyAirportFilter();
            await LoadNavaidsAsync(country);
            StatusText.Text = $"{_airports.Count} terrains ({country}). " + StormAtcSummary();
            RefreshRoute();

            string key = KeyBox.Password.Trim();
            if (key.Length > 0)
            {
                StatusText.Text += " Chargement des espaces aériens...";
                var spaces = await new OpenAipService(_http).LoadAsync(country, key);
                _airspaces = spaces;
                ApplyAirspaceFilter();
                StatusText.Text = $"{_airports.Count} terrains, {spaces.Count} espaces aériens ({country}). " + StormAtcSummary();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            StatusText.Text = "Erreur de chargement : " + ex.Message;
        }
    }

    private Task<StormAtcRepository?> LoadStormAtcAsync()
    {
        string configured = StormBox.Text.Trim(); // lu ici : un TextBox ne se lit que depuis le thread UI
        return Task.Run(() =>
        {
            var paths = StormAtcPaths.Locate(configured);
            try { return paths is null ? null : StormAtcRepository.Load(paths); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        });
    }

    private string StormAtcSummary() => _stormAtc is null
        ? "StormATC introuvable."
        : $"StormATC : {_stormAtc.All.Count} aérodromes, {_stormAtc.ProfileCount} profils, {_stormAtc.ChartCount} cartes VAC.";

    private IFeature AirportFeature(Airport a)
    {
        bool dots = WxDotsCheck.IsChecked == true;
        _wx.TryGetValue(a.Ident, out var wx);
        var (x, y) = SphericalMercator.FromLonLat(a.Lon, a.Lat);
        var f = new PointFeature(new MPoint(x, y));
        f["ident"] = a.Ident;
        f.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = a.Type == "large_airport" ? 0.5 : 0.4,
            Fill = new Brush(dots ? CategoryColor(wx.Category) : a.HasVac ? Color.FromArgb(255, 20, 140, 70) : Color.FromArgb(255, 31, 111, 224)),
            Outline = dots && a.HasVac ? new Pen(Color.FromArgb(255, 245, 190, 20), 2.4) : new Pen(Color.White, 1.5)
        });
        f.Styles.Add(new LabelStyle
        {
            Text = dots && wx.VisibilityM is { } vis ? $"{a.Ident}\n{WeatherDecoder.ShortVisibility(vis)}" : a.Ident,
            Offset = new Offset(0, dots && wx.VisibilityM is not null ? 20 : 14),
            ForeColor = Color.FromArgb(255, 11, 42, 85),
            BackColor = new Brush(Color.FromArgb(160, 255, 255, 255)),
            MaxVisible = 400
        });
        return f;
    }

    private static (Color Color, double Width, bool Dashed) AirspaceLook(AirspaceKind kind) => kind switch
    {
        AirspaceKind.Restricted => (Color.FromArgb(255, 192, 40, 40), 2.0, false),
        AirspaceKind.Ctr => (Color.FromArgb(255, 31, 90, 200), 2.0, true),
        AirspaceKind.Tma => (Color.FromArgb(255, 50, 60, 170), 1.5, false),
        AirspaceKind.Radio => (Color.FromArgb(255, 142, 68, 173), 1.5, true),
        AirspaceKind.Leisure => (Color.FromArgb(255, 215, 125, 15), 1.5, false),
        _ => (Color.FromArgb(255, 120, 120, 120), 1.0, false)
    };

    /// <summary>Contour seul (le fond reste visible), nom et limites écrits le long du trait.</summary>
    private static IEnumerable<IFeature> AirspaceFeatures(Airspace a)
    {
        var (color, width, dashed) = AirspaceLook(a.Kind);
        foreach (var ring in a.Rings)
        {
            if (ring.Count < 4) continue;
            var coords = ring.Select(p => { var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat); return new Coordinate(x, y); }).ToArray();
            var feature = new GeometryFeature(new LineString(coords));
            feature.Styles.Add(new AirspaceStyle
            {
                Stroke = color,
                Width = width,
                Dashed = dashed,
                Hatched = a.Kind == AirspaceKind.Restricted,
                Label = a.Label,
                ShortLabel = a.Name
            });
            yield return feature;
        }
    }

    private void ApplyAirspaceFilter()
    {
        var filter = ReadAirspaceFilter();
        // Les zones importantes d'abord : elles obtiennent leur étiquette avant les autres.
        var shown = _airspaces.Where(filter.Matches)
            .OrderBy(a => a.Kind switch { AirspaceKind.Restricted => 0, AirspaceKind.Ctr => 1, AirspaceKind.Tma => 2, AirspaceKind.Radio => 3, AirspaceKind.Leisure => 4, _ => 5 })
            .ToList();
        _shownAirspaces = shown;
        _projected = shown.Select(ProjectZone).OfType<ProjectedZone>().ToList();
        _airspaceLayer.Features = shown.SelectMany(AirspaceFeatures).ToList();
        _airspaceLayer.DataHasChanged();
        AirspaceCount.Text = _airspaces.Count == 0 ? "" : $"{shown.Count} / {_airspaces.Count} espaces affichés";
        ScheduleLive();
        MapControl.Refresh();
    }

    private AirspaceFilter ReadAirspaceFilter() => new(
        ARestricted.IsChecked == true, AControlled.IsChecked == true, ARadio.IsChecked == true,
        ALeisure.IsChecked == true, ASectors.IsChecked == true,
        AFloorCheck.IsChecked == true ? Parse(AFloorBox, 11500) : null);

    private void AirspaceFilter_Click(object sender, RoutedEventArgs e) => ApplyAirspaceFilter();

    // ---------- Carte ----------
    private void OnMapTapped(object? sender, MapEventArgs e)
    {
        if (e.GestureType != Mapsui.Manipulations.GestureType.SingleTap) return;
        if (_suppressTap) { _suppressTap = false; e.Handled = true; return; }
        // Un clic sur un point déjà dans la route le retire (les repères numérotés sont au-dessus des terrains).
        var marker = e.GetMapInfo([_waypointLayer]);
        if (marker?.Feature?["index"] is int index && index < _route.Count)
        {
            _route.RemoveAt(index);
            e.Handled = true;
            return;
        }

        var info = e.GetMapInfo([_airportLayer]);
        if (info?.Feature?["ident"] is string ident && _airports.FirstOrDefault(a => a.Ident == ident) is { } apt)
        {
            _route.Add(apt.ToWaypoint());
            ShowAerodrome(apt.Ident);
            e.Handled = true;
            return;
        }
        if (TryAddReportingPoint(e) || TryAddNavaid(e)) { e.Handled = true; return; }
        (double lon, double lat) = SphericalMercator.ToLonLat(e.WorldPosition.X, e.WorldPosition.Y);
        int n = _route.Count(w => !w.IsAirport) + 1;
        _route.Add(new Waypoint { Name = $"WP{n}", Lat = Math.Round(lat, 5), Lon = Math.Round(lon, 5) });
        e.Handled = true;
    }

    private void RefreshRoute()
    {
        var pts = _route.Select(w => SphericalMercator.FromLonLat(w.Lon, w.Lat)).ToList();

        var lineFeatures = new List<IFeature>();
        if (pts.Count > 1)
        {
            var line = new LineString(pts.Select(p => new Coordinate(p.x, p.y)).ToArray());
            var lf = new GeometryFeature(line);
            lf.Styles.Add(new VectorStyle { Line = new Pen(Color.White, 8) });
            lf.Styles.Add(new VectorStyle { Line = new Pen(Color.FromArgb(255, 228, 40, 30), 4) });
            lineFeatures.Add(lf);
        }
        _routeLayer.Features = lineFeatures;
        _routeLayer.DataHasChanged();

        var markers = new List<IFeature>();
        for (int i = 0; i < _route.Count; i++)
        {
            var f = new PointFeature(new MPoint(pts[i].x, pts[i].y));
            f["index"] = i;
            f.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = 0.7,
                Fill = new Brush(_route[i].IsAirport ? Color.FromArgb(255, 31, 111, 224) : Color.FromArgb(255, 228, 40, 30)),
                Outline = new Pen(Color.White, 2)
            });
            f.Styles.Add(new LabelStyle { Text = (i + 1).ToString(), ForeColor = Color.White, BackColor = new Brush(Color.Transparent) });
            markers.Add(f);
        }
        _waypointLayer.Features = markers;
        _waypointLayer.DataHasChanged();

        ApplyAutoVariation();
        RefreshLog();
        MapControl.Refresh();
        ScheduleLive();
    }

    private void RefreshLog()
    {
        if (_route.Count < 2) { LogGrid.ItemsSource = null; TotalText.Text = "Ajoutez au moins deux points à la route."; return; }
        var log = NavMath.ComputeLog(_route, ReadParams());
        LogGrid.ItemsSource = log.Legs;
        TotalText.Text = $"Total : {log.TotalDistanceNm:F1} NM   {NavMath.FormatTime(log.TotalMinutes)}   {log.TotalFuelL:F1} L";
    }

    private void FitRoute()
    {
        if (_route.Count < 2) return;
        var pts = _route.Select(w => SphericalMercator.FromLonLat(w.Lon, w.Lat)).ToList();
        var rect = new MRect(pts.Min(p => p.x), pts.Min(p => p.y), pts.Max(p => p.x), pts.Max(p => p.y)).Grow(5000);
        MapControl.Map.Navigator.ZoomToBox(rect);
    }

    // ---------- Événements UI ----------
    private void RouteGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshRoute);

    private void Param_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingParams && IsLoaded) RefreshLog();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int i = RouteGrid.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _route.Count) return;
        _route.Move(i, j);
        RouteGrid.SelectedIndex = j;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedIndex >= 0) _route.RemoveAt(RouteGrid.SelectedIndex);
    }

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        var items = _route.Reverse().ToList();
        _route.Clear();
        foreach (var w in items) _route.Add(w);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _route.Clear();

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Trim().Length > 0) SetKeyLocked(true);
        await LoadDataAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Route VFRNav (*.json)|*.json", FileName = "route.json" };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, RouteFile.Serialize(_route));
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Route VFRNav (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var loaded = RouteFile.Deserialize(File.ReadAllText(dlg.FileName));
            _route.Clear();
            foreach (var w in loaded) _route.Add(w);
            FitRoute();
        }
        catch (System.Text.Json.JsonException) { StatusText.Text = "Fichier de route invalide."; }
    }

    private void ExportPln_Click(object sender, RoutedEventArgs e)
    {
        if (_route.Count < 2) { StatusText.Text = "Il faut au moins deux points pour exporter."; return; }
        string a = _route[0].Icao ?? _route[0].Name, b = _route[^1].Icao ?? _route[^1].Name;
        var dlg = new SaveFileDialog { Filter = "Plan de vol MSFS (*.pln)|*.pln", FileName = $"{a}-{b}.pln" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, PlnWriter.Build(_route, ReadParams().CruiseAltFt));
        StatusText.Text = "Exporté : " + dlg.FileName;
    }

    private (string Metar, string Taf)? _lastWeather;

    private async void Weather_Click(object sender, RoutedEventArgs e)
    {
        var ids = _route.Where(w => w.IsAirport).Select(w => w.Icao!).ToList();
        if (ids.Count == 0) { WxText.Text = "Ajoutez des terrains à la route (clic sur un terrain)."; return; }
        WxText.Text = "Chargement...";
        try
        {
            _lastWeather = await new WeatherService(_http).GetAsync(ids);
            ShowWeather();
        }
        catch (HttpRequestException ex) { WxText.Text = "Erreur : " + ex.Message; }
    }

    private void WxRaw_Click(object sender, RoutedEventArgs e) => ShowWeather();

    /// <summary>METAR et TAF en clair, regroupés par terrain dans l'ordre de la route.</summary>
    private void ShowWeather()
    {
        if (_lastWeather is not { } wx) return;
        bool raw = WxRawCheck.IsChecked == true;
        var metars = WeatherDecoder.DecodeMetars(wx.Metar);
        var tafs = WeatherDecoder.DecodeTafs(wx.Taf);
        var sb = new System.Text.StringBuilder();
        foreach (string icao in _route.Where(w => w.IsAirport).Select(w => w.Icao!).Distinct())
        {
            string name = _airports.FirstOrDefault(a => a.Ident == icao)?.Name ?? "";
            sb.AppendLine($"=== {icao}  {name} ===");
            var m = metars.FirstOrDefault(r => r.Station == icao);
            var t = tafs.FirstOrDefault(r => r.Station == icao);
            AppendReport(sb, m, "METAR (observation) : aucun message disponible", raw);
            AppendReport(sb, t, "TAF (prévision) : aucun message disponible", raw);
            sb.AppendLine();
        }
        WxText.Text = sb.ToString().TrimEnd();
    }

    private static void AppendReport(System.Text.StringBuilder sb, DecodedReport? r, string missing, bool raw)
    {
        if (r is null) { sb.AppendLine(missing); return; }
        int dash = r.Header.IndexOf('—');
        string when = dash >= 0 ? " — " + r.Header[(dash + 1)..].Trim() : "";
        sb.AppendLine((r.Kind == "TAF" ? "TAF (prévision)" : "METAR (observation)") + when);
        if (raw) sb.AppendLine($"  [{r.Raw}]");
        foreach (string line in r.Lines) sb.AppendLine("  " + (line.StartsWith(' ') ? line : "• " + line));
    }
}

// ---------- StormATC : aérodromes et cartes VAC ----------
public partial class MainWindow
{
    private void ApplyAutoVariation()
    {
        if (AutoVarCheck.IsChecked != true || _stormAtc is null || _route.Count == 0 || !_route[0].IsAirport) return;
        if (_stormAtc.Find(_route[0].Icao!)?.MagneticVariation is { } v)
        {
            string text = v.ToString(CultureInfo.InvariantCulture);
            if (VarBox.Text != text) VarBox.Text = text;
        }
    }

    private AirportFilter ReadFilter() => new(
        FWithCode.IsChecked == true, FWithoutCode.IsChecked == true, FVacOnly.IsChecked == true,
        FLarge.IsChecked == true, FMedium.IsChecked == true, FSmall.IsChecked == true);

    private void ApplyAirportFilter()
    {
        var filter = ReadFilter();
        var shown = _airports.Where(filter.Matches).ToList();
        _airportLayer.Features = shown.Select(AirportFeature).ToList();
        _airportLayer.DataHasChanged();
        FilterCount.Text = $"{shown.Count} / {_airports.Count} terrains affichés";
        MapControl.Refresh();
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyAirportFilter();

    private void AutoVar_Click(object sender, RoutedEventArgs e) => RefreshRoute();

    private void BrowseStorm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier de StormATC (celui qui contient data et Cartes VAC)" };
        if (dlg.ShowDialog(this) == true) StormBox.Text = dlg.FolderName;
    }

    private void RouteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteGrid.SelectedItem is Waypoint { IsAirport: true } wp) ShowAerodrome(wp.Icao!);
        OnRouteSelectionChanged();
    }

    private void IcaoBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ShowAerodrome(IcaoBox.Text);
    }

    private void ShowAerodrome_Click(object sender, RoutedEventArgs e) => ShowAerodrome(IcaoBox.Text);

    private void ShowAerodrome(string query)
    {
        if (_stormAtc is null)
        {
            InfoText.Text = "StormATC est introuvable : indiquez son dossier dans le panneau Données.";
            return;
        }
        var apt = _stormAtc.Search(query);
        if (apt is null) { InfoText.Text = $"Aucun aérodrome pour « {query} »."; return; }
        _shown = apt;
        _page = 0;
        IcaoBox.Text = apt.Icao;
        InfoText.Text = StormAtcRepository.Describe(apt)
            + (StormAtcRepository.DescribeProcedures(apt) is { } procedures ? Environment.NewLine + Environment.NewLine + procedures : "");
        if (_vacVisible) _ = RenderVacAsync();
    }

    private void AddAerodrome_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is null) return;
        var known = _airports.FirstOrDefault(a => a.Ident == _shown.Icao);
        if (known is not null) _route.Add(known.ToWaypoint());
        else if (_shown is { Lat: { } lat, Lon: { } lon })
            _route.Add(new Waypoint { Name = _shown.Icao, Icao = _shown.Icao, FullName = _shown.Name, ElevationFt = _shown.ElevationFt ?? 0, Lat = lat, Lon = lon });
        else StatusText.Text = $"{_shown.Icao} : position inconnue, impossible de l'ajouter.";
    }

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_shown?.ChartPath is not { } path) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task RenderVacAsync()
    {
        VacMessage.Text = "";
        if (_shown is null) { VacImage.Source = null; PageText.Text = "-"; return; }
        if (_shown.ChartPath is not { } path)
        {
            VacImage.Source = null;
            PageText.Text = "-";
            VacMessage.Text = _shown.VacListed ? "Carte publiée mais absente du dossier Cartes VAC de StormATC." : "Pas de carte VAC pour cet aérodrome.";
            return;
        }
        string icao = _shown.Icao;
        if (!await _pdf.OpenAsync(path)) { VacMessage.Text = "Impossible de lire le PDF."; return; }
        if (_page >= _pdf.PageCount) _page = 0;
        var image = await _pdf.RenderAsync(_page, (uint)(FitWidth() * _zoom));
        if (_shown?.Icao != icao) return; // l'utilisateur a changé d'aérodrome entre-temps
        VacImage.Source = image;
        PageText.Text = $"Page {_page + 1}/{_pdf.PageCount}";
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; _ = RenderVacAsync(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (_page + 1 < _pdf.PageCount) { _page++; _ = RenderVacAsync(); } }
    private void ZoomFit_Click(object sender, RoutedEventArgs e) { _zoom = 1.0; _ = RenderVacAsync(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoom = Math.Min(_zoom * 1.25, 3.5); _ = RenderVacAsync(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoom = Math.Max(_zoom / 1.25, 0.5); _ = RenderVacAsync(); }
}

// ---------- Fond de carte et filtres d'altitude ----------
public partial class MainWindow
{
    private void BaseMap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ReloadBaseMap();
    }

    /// <summary>Remplace les couches du fond ; un fond CARTO sans clé retombe sur Esri avec un message.</summary>
    private void ReloadBaseMap()
    {
        string key = BaseMaps.Keys[Math.Max(BaseMapBox.SelectedIndex, 0)];
        string cartoKey = CartoKeyBox.Password.Trim();
        var map = MapControl.Map;
        foreach (var old in _baseLayers) map.Layers.Remove(old);
        _baseLayers = BaseMaps.Create(key, cartoKey);
        for (int i = 0; i < _baseLayers.Length; i++) map.Layers.Insert(i, _baseLayers[i]);
        _settings.BaseMap = key;
        if (BaseMaps.NeedsCartoKey(key) && cartoKey.Length == 0)
            StatusText.Text = "Fond CARTO : saisissez la clé CARTO (panneau Données). Fond Esri affiché en attendant.";
        MapControl.Refresh();
    }

    private void AirspaceFloor_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyAirspaceFilter();
    }
}

// ---------- Infobulle des espaces aériens ----------
public partial class MainWindow
{
    private const int MaxZonesInTip = 6;

    private void OnMapMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouse = e.GetPosition(MapControl);
        // La fiche d'un terrain reste affichée tant que le pointeur est dessus (pour pouvoir la lire).
        if (_airportTip is { } shown && MapTip.Visibility == Visibility.Visible
            && Distance(_mouse, ToScreen(shown.Lon, shown.Lat)) <= AirportHoverPx) return;
        HideTip();
        // Pas d'infobulle pendant un déplacement de la carte.
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Released && (_shownAirspaces.Count > 0 || _airports.Count > 0)) _tipTimer.Start();
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        _airportTip = null;
        MapTip.Visibility = Visibility.Collapsed;
        TipLeader.Visibility = Visibility.Collapsed;
    }

    private void ShowTip()
    {
        _tipTimer.Stop();
        if (ZoneHelpCheck.IsChecked == true) UpdateZoneHelp();
        if (TryShowWaypointNote()) return;
        if (AirportAt(_mouse) is { } airport) { ShowAirportTip(airport); return; }
        var world = MapControl.Map.Navigator.Viewport.ScreenToWorld(_mouse.X, _mouse.Y);
        (double lon, double lat) = SphericalMercator.ToLonLat(world.X, world.Y);
        var hits = _shownAirspaces.Where(a => a.Contains(lon, lat)).OrderBy(a => a.LowerFt).ThenBy(a => a.UpperFt).ToList();
        if (hits.Count == 0) return;

        TipText.Inlines.Clear();
        foreach (var a in hits.Take(MaxZonesInTip))
        {
            if (TipText.Inlines.Count > 0) TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
            TipText.Inlines.Add(new System.Windows.Documents.Run(a.Name) { FontWeight = FontWeights.Bold });
            foreach (string line in a.DescribeBody())
            {
                TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
                TipText.Inlines.Add(new System.Windows.Documents.Run(line));
            }
            TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
        if (hits.Count > MaxZonesInTip)
            TipText.Inlines.Add(new System.Windows.Documents.Run($"+ {hits.Count - MaxZonesInTip} autres zones") { FontStyle = FontStyles.Italic });

        PlaceTip();
    }

    /// <summary>
    /// Place la bulle. Avec un point d'ancrage (un terrain), elle se pose à côté de ce point et un trait la relie à lui ;
    /// sans ancrage, elle suit le curseur.
    /// </summary>
    private void PlaceTip(System.Windows.Point? anchor = null)
    {
        // La marge posée la fois précédente fausserait la mesure : on repart de zéro.
        MapTip.Margin = new Thickness(0);
        MapTip.Visibility = Visibility.Visible;
        MapTip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = MapTip.DesiredSize.Width, h = MapTip.DesiredSize.Height;
        double maxX = MapControl.ActualWidth, maxY = MapControl.ActualHeight;

        var at = anchor ?? _mouse;
        double gap = anchor is null ? 16 : 22;
        double x = at.X + gap, y = anchor is null ? at.Y + gap : at.Y - h - gap;
        if (x + w > maxX) x = Math.Max(0, at.X - gap - w);
        if (y + h > maxY) y = Math.Max(0, at.Y - h - gap);
        if (y < 0) y = Math.Min(at.Y + gap, Math.Max(0, maxY - h));
        MapTip.Margin = new Thickness(x, y, 0, 0);

        if (anchor is null) { TipLeader.Visibility = Visibility.Collapsed; return; }
        // Trait du terrain jusqu'au point le plus proche de la fiche.
        TipLeader.X1 = at.X;
        TipLeader.Y1 = at.Y;
        TipLeader.X2 = Math.Clamp(at.X, x, x + w);
        TipLeader.Y2 = Math.Clamp(at.Y, y, y + h);
        TipLeader.Visibility = Visibility.Visible;
    }
}

// ---------- Clé OpenAIP verrouillée ----------
public partial class MainWindow
{
    private bool _keyLocked;

    /// <summary>Une clé saisie est grisée pour qu'on ne la modifie pas par erreur ; « Modifier » la déverrouille.</summary>
    private void SetKeyLocked(bool locked)
    {
        _keyLocked = locked;
        KeyBox.IsEnabled = !locked;
        KeyEditButton.Content = locked ? "Modifier" : "Valider";
    }

    private async void KeyEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_keyLocked)
        {
            SetKeyLocked(false);
            KeyBox.Focus();
            KeyBox.SelectAll();
            return;
        }
        if (KeyBox.Password.Trim().Length == 0) return;
        SetKeyLocked(true);
        await LoadDataAsync();
    }
}

// ---------- Lecteur de cartes VAC : à droite ou en bas, masquable, agrandissable ----------
public partial class MainWindow
{
    private void ToggleVac_Click(object sender, RoutedEventArgs e)
    {
        _vacVisible = !_vacVisible;
        ApplyVacLayout(capture: true, wasVisible: !_vacVisible);
        if (_vacVisible) _ = RenderVacAsync();
    }

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        _settings.VacDock = _settings.VacDock == "right" ? "bottom" : "right";
        _vacLarge = false;
        ApplyVacLayout(capture: false);
        _ = RenderVacAsync();
    }

    private void Large_Click(object sender, RoutedEventArgs e)
    {
        _vacLarge = !_vacLarge;
        if (_settings.VacDock == "right") _vacWidth = _vacLarge ? Math.Max(Workspace.ActualWidth * 0.62, 600) : 520;
        else _vacHeight = _vacLarge ? Math.Max(Workspace.ActualHeight * 0.68, 420) : 320;
        ApplyVacLayout(capture: false);
    }

    private void VacScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_vacVisible || e.PreviousSize.Width == 0) return;
        _vacResizeTimer.Stop();
        _vacResizeTimer.Start(); // un redessin à la fin du glissement, pas à chaque pixel
    }

    private double FitWidth() => Math.Max(VacScroll.ActualWidth - 24, 300);

    /// <summary>Range la carte, les onglets, le lecteur VAC et les séparateurs dans la grille selon le mode.</summary>
    private void ApplyVacLayout(bool capture, bool wasVisible = true)
    {
        var g = Workspace;
        if (capture && wasVisible && VacPanel.IsVisible)
        {
            if (_settings.VacDock == "right" && VacPanel.ActualWidth > 200) _vacWidth = VacPanel.ActualWidth;
            if (_settings.VacDock != "right" && VacPanel.ActualHeight > 150) _vacHeight = VacPanel.ActualHeight;
        }
        if (BottomTabs.ActualHeight > 60) _tabsHeight = BottomTabs.ActualHeight;

        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });

        static void Place(UIElement e, int row, int col, int rowSpan = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetRowSpan(e, rowSpan);
            Grid.SetColumnSpan(e, 1);
        }

        bool right = _settings.VacDock == "right";
        SplitB.Visibility = SplitV.Visibility = Visibility.Collapsed;
        VacPanel.Visibility = _vacVisible ? Visibility.Visible : Visibility.Collapsed;
        DockButton.Content = right ? "Placer en bas" : "Placer à droite";
        LargeButton.Content = _vacLarge ? "Réduire" : "Agrandir";
        VacToggle.Content = _vacVisible ? "Masquer les VAC" : "Cartes VAC";

        if (_vacVisible && !right)
        {
            // carte / séparateur / VAC / séparateur / onglets
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_vacHeight), MinHeight = 200 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Min(_tabsHeight, 200)), MinHeight = 80 });
            Place(MapHost, 0, 0);
            Place(SplitA, 1, 0);
            Place(VacPanel, 2, 0);
            Place(SplitB, 3, 0);
            Place(BottomTabs, 4, 0);
            SplitB.Visibility = Visibility.Visible;
            return;
        }

        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_tabsHeight), MinHeight = 80 });
        Place(MapHost, 0, 0);
        Place(SplitA, 1, 0);
        Place(BottomTabs, 2, 0);
        if (_vacVisible)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_vacWidth), MinWidth = 320 });
            Place(SplitV, 0, 1, 3);
            Place(VacPanel, 0, 2, 3);
            SplitV.Visibility = Visibility.Visible;
        }
    }
}

// ---------- Clé CARTO verrouillée ----------
public partial class MainWindow
{
    private bool _cartoLocked;

    private void SetCartoLocked(bool locked)
    {
        _cartoLocked = locked;
        CartoKeyBox.IsEnabled = !locked;
        CartoEditButton.Content = locked ? "Modifier" : "Valider";
    }

    private void CartoEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_cartoLocked)
        {
            SetCartoLocked(false);
            CartoKeyBox.Focus();
            CartoKeyBox.SelectAll();
            return;
        }
        if (CartoKeyBox.Password.Trim().Length == 0) return;
        SetCartoLocked(true);
        ReloadBaseMap();
    }
}

// ---------- Déplacer un point de la route à la souris ----------
public partial class MainWindow
{
    private const double GrabRadiusPx = 14;
    private int _dragCandidate = -1;
    private int _dragIndex = -1;
    private System.Windows.Point _dragDown;
    private bool _suppressTap;

    private System.Windows.Point ToScreen(double lon, double lat)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var sp = MapControl.Map.Navigator.Viewport.WorldToScreen(new MPoint(x, y));
        return new System.Windows.Point(sp.X, sp.Y);
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void OnDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _suppressTap = false;
        _dragIndex = -1;
        _dragCandidate = -1;
        var pos = e.GetPosition(MapControl);
        double best = GrabRadiusPx;
        for (int i = 0; i < _route.Count; i++)
        {
            double d = Distance(pos, ToScreen(_route[i].Lon, _route[i].Lat));
            if (d <= best) { best = d; _dragCandidate = i; }
        }
        _dragDown = pos;
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCandidate < 0 || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var pos = e.GetPosition(MapControl);
        if (_dragIndex < 0)
        {
            if (Distance(pos, _dragDown) < 5) return; // un simple clic reste un clic
            _dragIndex = _dragCandidate;
            HideTip();
        }

        e.Handled = true; // la carte ne se déplace pas pendant qu'on tient un point
        var world = MapControl.Map.Navigator.Viewport.ScreenToWorld(pos.X, pos.Y);
        (double lon, double lat) = SphericalMercator.ToLonLat(world.X, world.Y);
        if (RouteGrid.SelectedIndex != _dragIndex) RouteGrid.SelectedIndex = _dragIndex;
        var wp = _route[_dragIndex];
        wp.Lat = Math.Round(lat, 5);
        wp.Lon = Math.Round(lon, 5);
        if (wp.IsAirport)
        {
            wp.Icao = null;
            wp.FullName = null;
            wp.ElevationFt = 0;
            wp.Name = $"WP{_dragIndex + 1}";
        }
        RefreshRoute();
        RouteGrid.Items.Refresh();
    }

    private void OnDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragIndex >= 0)
        {
            _suppressTap = true;
            SnapToAirport(_dragIndex, e.GetPosition(MapControl));
            RefreshRoute();
            RouteGrid.Items.Refresh();
        }
        _dragIndex = -1;
        _dragCandidate = -1;
    }

    /// <summary>Un point lâché sur un terrain devient ce terrain.</summary>
    private void SnapToAirport(int index, System.Windows.Point drop)
    {
        var filter = ReadFilter();
        Airport? nearest = null;
        double best = GrabRadiusPx;
        foreach (var a in _airports.Where(filter.Matches))
        {
            double d = Distance(drop, ToScreen(a.Lon, a.Lat));
            if (d <= best) { best = d; nearest = a; }
        }
        if (nearest is not null) _route[index] = nearest.ToWaypoint();
    }
}

// ---------- NOTAM ----------
public partial class MainWindow
{
    private NotamService? _notamService;
    private bool _notamLocked;

    private void SetNotamLocked(bool locked)
    {
        _notamLocked = locked;
        NotamKeyBox.IsEnabled = !locked;
        NotamEditButton.Content = locked ? "Modifier" : "Valider";
    }

    private void NotamEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_notamLocked)
        {
            SetNotamLocked(false);
            NotamKeyBox.Focus();
            NotamKeyBox.SelectAll();
        }
        else if (NotamKeyBox.Password.Trim().Length > 0) SetNotamLocked(true);
    }

    private const string SofiaUrl = "https://sofia-briefing.aviation-civile.gouv.fr/";

    /// <summary>NOTAM officiels français : le SIA les propose dans SOFIA-Briefing, ouvert dans le navigateur.</summary>
    private void OpenSofia_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SofiaUrl) { UseShellExecute = true });

    private void CopyCodes_Click(object sender, RoutedEventArgs e)
    {
        var codes = _route.Where(w => w.IsAirport).Select(w => w.Icao!).Distinct().ToList();
        if (codes.Count == 0) { NotamText.Text = "Ajoutez des terrains à la route (clic sur un terrain)."; return; }
        Clipboard.SetText(string.Join(" ", codes));
        StatusText.Text = "Codes copiés : " + string.Join(" ", codes);
    }

    private async void Notam_Click(object sender, RoutedEventArgs e)
    {
        string key = NotamKeyBox.Password.Trim();
        if (key.Length == 0) { NotamText.Text = "Saisissez d'abord la clé ICAO API Data Service (panneau Données)."; return; }
        var ids = _route.Where(w => w.IsAirport).Select(w => w.Icao!).Distinct().ToList();
        if (ids.Count == 0) { NotamText.Text = "Ajoutez des terrains à la route (clic sur un terrain)."; return; }

        _notamService ??= new NotamService(_http);
        NotamText.Text = "Chargement...";
        var sb = new System.Text.StringBuilder();
        foreach (string icao in ids)
        {
            string name = _airports.FirstOrDefault(a => a.Ident == icao)?.Name ?? "";
            sb.AppendLine($"=== {icao}  {name} ===");
            try
            {
                var list = await _notamService.GetAsync(icao, key);
                if (list.Count == 0) sb.AppendLine("Aucun NOTAM en vigueur.");
                foreach (var n in list)
                {
                    string validity = n.Start is not null || n.End is not null ? $"  [{n.Start ?? "?"} → {n.End ?? "?"}]" : "";
                    sb.AppendLine($"• {n.Id}{validity}");
                    sb.AppendLine("  " + n.Text.Replace("\n", "\n  "));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                sb.AppendLine("Erreur : " + ex.Message);
            }
            sb.AppendLine();
        }
        NotamText.Text = sb.ToString().TrimEnd();
    }
}

// ---------- Avion en direct (SimConnect) ----------
public partial class MainWindow
{
    private void StartTracker()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (_tracker.HandleMessage(msg)) handled = true;
            return IntPtr.Zero;
        });
        _tracker.StatusChanged += text => Dispatcher.Invoke(() => SimStatus.Text = text);
        _tracker.Updated += OnAircraft;
        _tracker.Start(handle);
    }

    private void OnAircraft(AircraftState a)
    {
        var (x, y) = SphericalMercator.FromLonLat(a.Lon, a.Lat);
        var plane = new PointFeature(new MPoint(x, y));
        plane.Styles.Add(new ImageStyle
        {
            Image = new Mapsui.Styles.Image { Source = PlaneIcon.Source },
            SymbolScale = 0.6,
            SymbolRotation = a.HeadingTrue
        });
        _aircraftLayer.Features = [plane];
        _aircraftLayer.DataHasChanged();

        double magnetic = NavMath.Norm360(a.HeadingTrue - Parse(VarBox, 0));
        SimInfo.Text = $"{(a.OnGround ? "Au sol" : "En vol")}\n" +
                       $"{a.AltitudeFt:0} ft  {a.GroundSpeedKt:0} kt\n" +
                       $"Cap {magnetic:000}° magnétique\n" +
                       $"{a.Lat:0.0000}  {a.Lon:0.0000}";

        if (FollowCheck.IsChecked == true) MapControl.Map.Navigator.CenterOn(new MPoint(x, y));
        MapControl.Refresh();
    }
}

// ---------- Fiche d'un terrain au survol : infos StormATC + météo en clair ----------
public partial class MainWindow
{
    private const double AirportHoverPx = 12;
    private const int MaxTafLinesInTip = 12;
    private WeatherService? _weatherService;
    private Airport? _airportTip;

    private Airport? AirportAt(System.Windows.Point pos)
    {
        var filter = ReadFilter();
        Airport? best = null;
        double bestDistance = AirportHoverPx;
        foreach (var a in _airports.Where(filter.Matches))
        {
            double d = Distance(pos, ToScreen(a.Lon, a.Lat));
            if (d <= bestDistance) { bestDistance = d; best = a; }
        }
        return best;
    }

    private async void ShowAirportTip(Airport airport)
    {
        _airportTip = airport;
        FillAirportTip(airport, "Météo : chargement...", null, null);
        PlaceTip(ToScreen(airport.Lon, airport.Lat));

        string metarRaw = "", tafRaw = "";
        string? error = null;
        try
        {
            _weatherService ??= new WeatherService(_http);
            (metarRaw, tafRaw) = await _weatherService.GetCachedAsync(airport.Ident);
        }
        catch (HttpRequestException ex) { error = "Météo indisponible : " + ex.Message; }
        catch (TaskCanceledException) { error = "Météo indisponible (délai dépassé)."; }

        if (_airportTip?.Ident != airport.Ident || MapTip.Visibility != Visibility.Visible) return; // le pointeur est parti
        var metar = metarRaw.Length > 0 ? WeatherDecoder.DecodeMetars(metarRaw).FirstOrDefault(r => r.Station == airport.Ident) : null;
        var taf = tafRaw.Length > 0 ? WeatherDecoder.DecodeTafs(tafRaw).FirstOrDefault(r => r.Station == airport.Ident) : null;
        FillAirportTip(airport, error ?? (metar is null && taf is null ? "Pas de METAR ni de TAF pour ce terrain." : null), metar, taf);
        PlaceTip(ToScreen(airport.Lon, airport.Lat));
    }

    private static System.Windows.Media.Brush CategoryBrush(string? category) => category switch
    {
        "VFR" => System.Windows.Media.Brushes.ForestGreen,
        "MVFR" => System.Windows.Media.Brushes.RoyalBlue,
        "IFR" => System.Windows.Media.Brushes.Firebrick,
        "LIFR" => System.Windows.Media.Brushes.MediumVioletRed,
        _ => System.Windows.Media.Brushes.Gray
    };

    private void FillAirportTip(Airport airport, string? note, DecodedReport? metar, DecodedReport? taf)
    {
        var inl = TipText.Inlines;
        inl.Clear();
        void Line(string text, bool bold = false, System.Windows.Media.Brush? brush = null)
        {
            if (inl.Count > 0) inl.Add(new System.Windows.Documents.LineBreak());
            var run = new System.Windows.Documents.Run(text);
            if (bold) run.FontWeight = FontWeights.Bold;
            if (brush is not null) run.Foreground = brush;
            inl.Add(run);
        }

        var details = _stormAtc?.Find(airport.Ident);
        Line($"{(details?.Name ?? airport.Name)} ({airport.Ident})", bold: true);
        foreach (string line in StormAtcRepository.Describe(details ?? new StormAtcAerodrome(airport.Ident, airport.Name, null, null, false,
                     airport.Lat, airport.Lon, airport.ElevationFt, null, null, [], [], null)).Split(Environment.NewLine).Skip(1))
            Line(line);

        if (details?.Procedures?.Summary is { } summary) Line(summary);
        if (metar is not null)
        {
            Line("");
            Line("Observation (METAR)" + MetarWhen(metar), bold: true);
            foreach (string l in metar.Lines.Where(l => !l.StartsWith("Catégorie", StringComparison.Ordinal))) Line("• " + l);
            if (metar.Category is not null) Line("Catégorie de vol : " + metar.Category, bold: true, brush: CategoryBrush(metar.Category));
        }
        if (taf is not null)
        {
            Line("");
            Line("Prévision (TAF)" + MetarWhen(taf), bold: true);
            var lines = taf.Lines;
            foreach (string l in lines.Take(MaxTafLinesInTip)) Line(l.StartsWith(' ') ? "   " + l.TrimStart() : (l.EndsWith(':') ? l : "• " + l));
            if (lines.Count > MaxTafLinesInTip) Line($"… {lines.Count - MaxTafLinesInTip} lignes de plus : onglet METAR / TAF");
        }
        if (note is not null) { Line(""); Line(note); }
    }

    private static string MetarWhen(DecodedReport r)
    {
        int dash = r.Header.IndexOf('—');
        return dash >= 0 ? " — " + r.Header[(dash + 1)..].Trim() : "";
    }
}

// ---------- Pastilles météo des terrains ----------
public partial class MainWindow
{
    private static Color CategoryColor(string? category) => category switch
    {
        "VFR" => Color.FromArgb(255, 34, 160, 70),
        "MVFR" => Color.FromArgb(255, 30, 100, 230),
        "IFR" => Color.FromArgb(255, 215, 40, 40),
        "LIFR" => Color.FromArgb(255, 190, 30, 160),
        _ => Color.FromArgb(255, 150, 150, 150)
    };

    private void WxDots_Click(object sender, RoutedEventArgs e)
    {
        _wxDirty = true;
        ApplyAirportFilter();
    }

    /// <summary>Récupère la météo une fois la carte immobile : un seul appel pour toute la région visible.</summary>
    private void ViewTick()
    {
        var vp = MapControl.Map.Navigator.Viewport;
        var view = (Math.Round(vp.CenterX), Math.Round(vp.CenterY), Math.Round(vp.Resolution, 2));
        if (view != _lastView) { _lastView = view; _wxDirty = true; return; }
        if (_wxDirty && !_wxBusy && WxDotsCheck.IsChecked == true && _airports.Count > 0)
        {
            _wxDirty = false;
            _ = RefreshWeatherDotsAsync();
        }
    }

    private async Task RefreshWeatherDotsAsync()
    {
        var extent = MapControl.Map.Navigator.Viewport.ToExtent();
        if (extent is null) return;
        (double minLon, double minLat) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        (double maxLon, double maxLat) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
        if (maxLat - minLat > 9 || maxLon - minLon > 14) return; // trop dézoomé : une région trop grande pour un seul appel

        _wxBusy = true;
        try
        {
            _weatherService ??= new WeatherService(_http);
            string raw = await _weatherService.GetMetarsInBoxAsync(minLat, minLon, maxLat, maxLon);
            foreach (var report in WeatherDecoder.DecodeMetars(raw))
                if (report.Station.Length > 0) _wx[report.Station] = (report.Category, report.VisibilityM);
            ApplyAirportFilter();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = "Météo des pastilles indisponible : " + ex.Message;
        }
        finally { _wxBusy = false; }
    }
}

// ---------- Aide des zones pour novices, au survol d'une frontière ----------
public partial class MainWindow
{
    private const string ZoneHelpIntro =
        "Passez la souris sur le contour d'une zone pour voir ici ce qu'elle est : son type, sa classe, ses limites en altitude et ce qu'il faut faire en vol à vue.";
    private const double BoundaryPx = 8;

    private sealed record ProjectedZone(Airspace Zone, double[][] Xs, double[][] Ys, double MinX, double MinY, double MaxX, double MaxY);

    private static ProjectedZone? ProjectZone(Airspace zone)
    {
        var xs = new List<double[]>();
        var ys = new List<double[]>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in zone.Rings)
        {
            if (ring.Count < 3) continue;
            var rx = new double[ring.Count];
            var ry = new double[ring.Count];
            for (int i = 0; i < ring.Count; i++)
            {
                (rx[i], ry[i]) = SphericalMercator.FromLonLat(ring[i].Lon, ring[i].Lat);
                minX = Math.Min(minX, rx[i]); maxX = Math.Max(maxX, rx[i]);
                minY = Math.Min(minY, ry[i]); maxY = Math.Max(maxY, ry[i]);
            }
            xs.Add(rx);
            ys.Add(ry);
        }
        return xs.Count == 0 ? null : new ProjectedZone(zone, [.. xs], [.. ys], minX, minY, maxX, maxY);
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        double cx = ax + t * dx - px, cy = ay + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private void UpdateZoneHelp()
    {
        if (_projected.Count == 0) return;
        var vp = MapControl.Map.Navigator.Viewport;
        var w = vp.ScreenToWorld(_mouse.X, _mouse.Y);
        double tolerance = BoundaryPx * vp.Resolution;

        ProjectedZone? nearest = null;
        double best = tolerance;
        foreach (var z in _projected)
        {
            if (w.X < z.MinX - tolerance || w.X > z.MaxX + tolerance || w.Y < z.MinY - tolerance || w.Y > z.MaxY + tolerance) continue;
            for (int r = 0; r < z.Xs.Length; r++)
            {
                var xs = z.Xs[r];
                var ys = z.Ys[r];
                for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
                {
                    double d = DistanceToSegment(w.X, w.Y, xs[j], ys[j], xs[i], ys[i]);
                    if (d < best) { best = d; nearest = z; }
                }
            }
        }
        if (nearest is not null) ShowZoneHelp(nearest.Zone);
    }

    private void ShowZoneHelp(Airspace zone)
    {
        ZoneHelp.Visibility = Visibility.Visible;
        var inl = ZoneHelpText.Inlines;
        inl.Clear();
        inl.Add(new System.Windows.Documents.Run(zone.Name) { FontWeight = FontWeights.Bold, FontSize = 15 });
        inl.Add(new System.Windows.Documents.Run($"   {zone.Lower} → {zone.Upper}") { Foreground = System.Windows.Media.Brushes.DimGray });
        foreach (var section in AirspaceGuide.Explain(zone, Parse(AltBox, 2500)))
        {
            inl.Add(new System.Windows.Documents.LineBreak());
            inl.Add(new System.Windows.Documents.Run(section.Heading + " : ") { FontWeight = FontWeights.SemiBold });
            inl.Add(new System.Windows.Documents.Run(section.Text));
        }
        inl.Add(new System.Windows.Documents.LineBreak());
        inl.Add(new System.Windows.Documents.Run(AirspaceGuide.Disclaimer) { FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11 });
    }

    private void ZoneHelp_Click(object sender, RoutedEventArgs e) =>
        ZoneHelp.Visibility = ZoneHelpCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Le ✕ masque la fiche en cours ; elle revient au prochain contour survolé. Seule la case la désactive pour de bon.</summary>
    private void ZoneHelpClose_Click(object sender, RoutedEventArgs e) => ZoneHelp.Visibility = Visibility.Collapsed;
}
