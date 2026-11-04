using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

namespace VFRNav.App;

// ---------- SIGMET sur la carte : zones de phénomènes dangereux en vigueur, couleur par phénomène, survol, mise en évidence sur la route ----------
public partial class MainWindow
{
    private readonly MemoryLayer _sigmetLayer = new("SIGMET") { Style = null };
    private IReadOnlyList<Sigmet> _sigmets = [];
    private IReadOnlyList<Sigmet> _sigmetsShown = [];
    private string? _sigmetProblem;
    private bool _sigmetReady;
    private bool _sigmetBusy;
    private bool _sigmetRerun;
    private readonly System.Windows.Threading.DispatcherTimer _sigmetTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    private const int MaxSigmetsInTip = 3;
    private static readonly CultureInfo FrSigmet = CultureInfo.GetCultureInfo("fr-FR");

    private void InitSigmet()
    {
        SigmetCheck.IsChecked = _settings.ShowSigmet;
        SigmetAltCheck.IsChecked = _settings.SigmetAltitudeOnly;
        SigmetOpacitySlider.Value = Math.Clamp(_settings.SigmetOpacity, 0.1, 1);
        _sigmetLayer.Opacity = SigmetOpacitySlider.Value;
        BuildSigmetLegend();
        _sigmetTimer.Tick += async (_, _) => await RefreshSigmetAsync();
        Loaded += async (_, _) =>
        {
            _sigmetReady = true;
            await RefreshSigmetAsync();
        };
    }

    private async void Sigmet_Click(object sender, RoutedEventArgs e) => await RefreshSigmetAsync();

    private void SigmetOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sigmetReady) return;
        _sigmetLayer.Opacity = SigmetOpacitySlider.Value;
        MapControl.Refresh();
    }

    /// <summary>Télécharge les SIGMET (le service les garde 10 minutes) puis redessine ; un clic pendant un chargement est repris juste après.</summary>
    private async Task RefreshSigmetAsync()
    {
        if (!_sigmetReady) return;
        if (_sigmetBusy) { _sigmetRerun = true; return; }
        _sigmetBusy = true;
        try
        {
            do
            {
                _sigmetRerun = false;
                bool on = SigmetCheck.IsChecked == true;
                _sigmetTimer.IsEnabled = on;
                if (on)
                {
                    try
                    {
                        _sigmets = await (_sigmetService ??= new SigmetService(_http)).GetAsync();
                        _sigmetProblem = null;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                    {
                        // On garde les messages déjà affichés : une coupure passagère ne doit pas vider la carte.
                        _sigmetProblem = "SIGMET indisponibles : " + ex.Message;
                    }
                }
                RedrawSigmet();
            } while (_sigmetRerun);
        }
        finally { _sigmetBusy = false; }
    }

    /// <summary>
    /// Dessine les SIGMET valides maintenant (et à l'altitude de croisière si la case est cochée). Ceux qui traversent la route
    /// ont un contour plein et épais ; les autres un contour en tirets.
    /// </summary>
    private void RedrawSigmet()
    {
        if (!_sigmetReady) return;
        bool on = SigmetCheck.IsChecked == true;
        SigmetLegendBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        var now = DateTimeOffset.UtcNow;
        double? altitude = SigmetAltCheck.IsChecked == true ? CurrentCruiseAltFt() : null;
        _sigmetsShown = on ? SigmetPresentation.Select(_sigmets, now, altitude) : [];
        var onRoute = on ? SigmetsOnRoute(now).Select(r => r.Sigmet).ToHashSet() : [];

        var features = new List<IFeature>();
        foreach (var s in _sigmetsShown)
        {
            bool crossing = onRoute.Contains(s);
            var stroke = ColorFromHex(SigmetPresentation.HazardColorHex(s.Hazard), 255);
            var fill = ColorFromHex(SigmetPresentation.HazardColorHex(s.Hazard), (byte)(crossing ? 70 : 38));

            foreach (var area in s.Areas)
            {
                var ring = SigmetPresentation.DrawRing(area);
                if (ring.Count < 4) continue;
                var coordinates = ring.Select(p =>
                {
                    var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                    return new Coordinate(x, y);
                }).ToArray();

                var feature = new GeometryFeature(new Polygon(new LinearRing(coordinates)));
                if (crossing) feature.Styles.Add(new VectorStyle { Fill = new Brush(Color.Transparent), Outline = new Pen(Color.White, 7) });
                feature.Styles.Add(new VectorStyle
                {
                    Fill = new Brush(fill),
                    Outline = crossing ? new Pen(stroke, 4) : new Pen(stroke, 2) { PenStyle = PenStyle.Dash },
                });
                features.Add(feature);
            }

            // Une étiquette par message, posée au centre de sa première zone.
            var (lat, lon) = SigmetPresentation.LabelPoint(s.Areas[0]);
            var (lx, ly) = SphericalMercator.FromLonLat(lon, lat);
            var label = new PointFeature(new MPoint(lx, ly));
            label.Styles.Add(new LabelStyle
            {
                Text = SigmetPresentation.LabelText(s),
                ForeColor = stroke,
                BackColor = new Brush(Color.FromArgb(215, 255, 255, 255)),
                Halo = new Pen(Color.White, 1),
            });
            features.Add(label);
        }

        _sigmetLayer.Features = features;
        _sigmetLayer.DataHasChanged();
        ShowSigmetInfo(on, onRoute.Count);
        MapControl.Refresh();
    }

    private IReadOnlyList<SigmetOnRoute> SigmetsOnRoute(DateTimeOffset now)
    {
        if (_route.Count < 2 || _sigmetsShown.Count == 0) return [];
        var route = _route.ToList();
        var p = ReadParams();
        var log = NavMath.ComputeLog(route, p);
        return SigmetAnalysis.AlongRoute(route, NewAltitudeProfile(route, p.CruiseAltFt), _sigmetsShown, now, TimeSpan.FromMinutes(log.TotalMinutes));
    }

    private void ShowSigmetInfo(bool on, int onRouteCount)
    {
        if (!on) { SigmetInfo.Text = ""; return; }
        var lines = new List<string>();
        if (_sigmetProblem is not null) lines.Add(_sigmetProblem);
        else if (_sigmets.Count == 0) lines.Add("SIGMET : chargement...");
        else
        {
            string route = onRouteCount > 0 ? $" ; {onRouteCount} sur ta route (contour plein)" : "";
            lines.Add(string.Create(FrSigmet, $"{_sigmetsShown.Count} SIGMET affichés sur {SigmetPresentation.Select(_sigmets, DateTimeOffset.UtcNow).Count} en vigueur dans le monde{route}."));
        }
        lines.Add("Source : aviationweather.gov, mis à jour toutes les 10 min. L'absence de zone ne garantit pas une météo calme.");
        SigmetInfo.Text = string.Join("\n", lines);
    }

    // ---------- infobulle au survol ----------

    /// <summary>Infobulle des SIGMET qui couvrent le point survolé ; faux s'il n'y en a pas.</summary>
    private bool TryShowSigmetTip(double lat, double lon)
    {
        var hits = SigmetPresentation.At(_sigmetsShown, lat, lon);
        if (hits.Count == 0) return false;

        var now = DateTimeOffset.UtcNow;
        TipText.Inlines.Clear();
        foreach (var s in hits.Take(MaxSigmetsInTip))
        {
            if (TipText.Inlines.Count > 0) TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
            var (title, lines) = SigmetPresentation.TipText(s, now);
            TipText.Inlines.Add(new System.Windows.Documents.Run("SIGMET · " + title) { FontWeight = FontWeights.Bold });
            foreach (string line in lines)
            {
                TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
                TipText.Inlines.Add(new System.Windows.Documents.Run(line));
            }
            TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
        if (hits.Count > MaxSigmetsInTip)
            TipText.Inlines.Add(new System.Windows.Documents.Run($"+ {hits.Count - MaxSigmetsInTip} autres SIGMET") { FontStyle = FontStyles.Italic });

        PlaceTip();
        return true;
    }

    // ---------- vérification de la route ----------

    /// <summary>
    /// Lignes du panneau « Vérification de la route » pour les SIGMET qui traversent la route (même case décochée : un danger sur la route
    /// se signale toujours). Un échec réseau n'ajoute rien.
    /// </summary>
    private async Task<IReadOnlyList<string>> SigmetCheckLinesAsync(List<Waypoint> route, AltitudeProfile altitudes, FlightParameters p, CancellationToken ct)
    {
        try
        {
            var all = await (_sigmetService ??= new SigmetService(_http)).GetAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var log = NavMath.ComputeLog(route, p);
            return SigmetAnalysis.AlongRoute(route, altitudes, all, now, TimeSpan.FromMinutes(log.TotalMinutes))
                .Select(r => SigmetCheckLine(r, now))
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            return [];
        }
    }

    private static string SigmetCheckLine(SigmetOnRoute r, DateTimeOffset now)
    {
        string where = r.Status switch
        {
            VerticalStatus.Inside => "tu y es à ton altitude",
            VerticalStatus.Above => "tu passes au-dessus",
            _ => "tu passes en dessous",
        };
        string warning = r.Status == VerticalStatus.Inside ? "⚠ " : "";
        return string.Create(FrSigmet,
            $"{warning}SIGMET {r.Sigmet.Description} ({r.Sigmet.FirId} {r.Sigmet.SeriesId}) : {r.EntryNm:0} → {r.ExitNm:0} NM, {SigmetPresentation.LevelRange(r.Sigmet)}, {where}, {SigmetPresentation.ValidityText(r.Sigmet, now)}");
    }

    // ---------- légende ----------

    private void BuildSigmetLegend()
    {
        foreach (var entry in SigmetPresentation.Legend)
        {
            var item = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 1) };
            item.Children.Add(new System.Windows.Controls.Border
            {
                Width = 12,
                Height = 10,
                Margin = new Thickness(0, 1, 4, 0),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new System.Windows.Media.SolidColorBrush(WpfColor(entry.Hex)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, WpfColor(entry.Hex).R, WpfColor(entry.Hex).G, WpfColor(entry.Hex).B)),
            });
            item.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = entry.Label,
                FontSize = 9.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x2A, 0x55)),
            });
            SigmetLegendItems.Children.Add(item);
        }
    }

    private static System.Windows.Media.Color WpfColor(string hex) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static Color ColorFromHex(string hex, byte alpha)
    {
        var c = WpfColor(hex);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }
}
