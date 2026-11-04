using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.App;

// ---------- Appareils : un choix par type de navigation, gestion, paramètres du log et cercle de portée ----------
public partial class MainWindow
{
    private readonly AircraftCatalog _aircraft = AircraftCatalog.Load();
    private readonly MemoryLayer _rangeLayer = new("Portée") { Style = null };
    private FlightMode _mode = FlightMode.Vfr;
    private bool _aircraftReady;

    /// <summary>Les routes ATS françaises basses commencent vers le FL65 : en dessous, le calcul IFR ne trouverait aucune route.</summary>
    private const double MinIfrFlForAirways = 65;

    private static string IfrDefaultFl(Aircraft a) => Fmt(Math.Max(MinIfrFlForAirways, Math.Round(a.CruiseAltFt / 100)));

    private ComboBox AircraftBox(FlightMode mode) => mode == FlightMode.Vfr ? VfrAircraftBox : IfrAircraftBox;

    /// <summary>Appareil choisi pour un type de navigation.</summary>
    private Aircraft? AircraftFor(FlightMode mode) => AircraftBox(mode).SelectedItem as Aircraft;

    /// <summary>Appareil dont les performances servent au log de navigation : celui du type de navigation actif.</summary>
    private Aircraft? ActiveAircraft => AircraftFor(_mode);

    private void InitAircraft()
    {
        _mode = string.Equals(_settings.ActiveMode, "ifr", StringComparison.OrdinalIgnoreCase) ? FlightMode.Ifr : FlightMode.Vfr;
        FillAircraft(FlightMode.Vfr, _settings.VfrAircraftId);
        FillAircraft(FlightMode.Ifr, _settings.IfrAircraftId);

        VfrAltBox.Text = Fmt(_settings.CruiseAltFt);
        IfrFlBox.Text = _settings.IfrCruiseAltFt > 0 ? Fmt(Math.Round(_settings.IfrCruiseAltFt / 100))
            : AircraftFor(FlightMode.Ifr) is { } ifrAircraft ? IfrDefaultFl(ifrAircraft) : "";
        RangeCheck.IsChecked = _settings.ShowRange;

        _aircraftReady = true;
        UpdateAircraftUi();
        if (_aircraft.LoadWarning is { } warning) StatusText.Text = warning;
    }

    /// <summary>Remplit la liste d'un type de navigation et garde (ou rétablit) la sélection.</summary>
    private void FillAircraft(FlightMode mode, Guid? select)
    {
        var box = AircraftBox(mode);
        bool wasLoading = _loadingParams;
        _loadingParams = true;
        var items = _aircraft.ForMode(mode);
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(a => a.Id == select) ?? items.FirstOrDefault();
        _loadingParams = wasLoading;
    }

    // ---------- paramètres de vol pour le log ----------

    private double CruiseTas => ActiveAircraft?.CruiseKt ?? Math.Max(_settings.TasKt, 1);
    private double CruiseBurn => ActiveAircraft?.BurnLph ?? _settings.BurnLph;

    /// <summary>Altitude de croisière du type de navigation actif : champ « Croisière (ft) » en VFR, niveau de vol en IFR.</summary>
    private double CurrentCruiseAltFt()
    {
        if (_mode == FlightMode.Ifr)
        {
            if (double.TryParse(IfrFlBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double fl) && fl > 0)
                return fl * 100;
            return AircraftFor(FlightMode.Ifr)?.CruiseAltFt ?? 2500;
        }
        return Parse(VfrAltBox, 2500);
    }

    /// <summary>Change l'altitude de croisière du type de navigation actif (glisser dans le profil, ouverture d'un .pln).</summary>
    private void SetCruiseAlt(double ft)
    {
        if (_mode == FlightMode.Ifr) IfrFlBox.Text = Fmt(Math.Round(ft / 100));
        else VfrAltBox.Text = Fmt(ft);
    }

    /// <summary>Le type de navigation dont on calcule la route devient celui dont l'appareil sert au log.</summary>
    private void SetMode(FlightMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        AfterAircraftChange();
    }

    /// <summary>Altitude de croisière ou niveau de vol modifié : log, profil et vérification de la route suivent.</summary>
    private void CruiseAlt_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_aircraftReady || _loadingParams || !IsLoaded) return;
        RefreshLog();
        ScheduleLive();
        RedrawSigmet();
    }

    /// <summary>Croisière utilisée pour le profil, ex. « croisière FL350 · IFR · Airbus A320neo ».</summary>
    private string CruiseSummary()
    {
        double ft = CurrentCruiseAltFt();
        string level = ft >= 18000 ? $"FL{ft / 100:000}" : string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{ft:N0} ft");
        return $" · croisière {level} · {(_mode == FlightMode.Ifr ? "IFR" : "VFR")}" + (ActiveAircraft is { } a ? " · " + a.Name : "");
    }

    private void AfterAircraftChange()
    {
        RefreshAlternates();
        ScheduleLive();
        UpdateAircraftUi();
        if (IsLoaded) RefreshLog();
        RefreshRange();
        RedrawSigmet();
    }

    // ---------- événements ----------

    private void VfrAircraft_Changed(object sender, SelectionChangedEventArgs e) => OnAircraftPicked(FlightMode.Vfr);
    private void IfrAircraft_Changed(object sender, SelectionChangedEventArgs e) => OnAircraftPicked(FlightMode.Ifr);

    private void OnAircraftPicked(FlightMode mode)
    {
        if (!_aircraftReady || _loadingParams) return;
        _mode = mode;
        // Un nouvel appareil ramène l'altitude de croisière à sa valeur habituelle.
        if (AircraftFor(mode) is { } a)
        {
            if (mode == FlightMode.Vfr) VfrAltBox.Text = Fmt(a.CruiseAltFt);
            else IfrFlBox.Text = IfrDefaultFl(a);
        }
        AfterAircraftChange();
    }

    private void VfrManage_Click(object sender, RoutedEventArgs e) => ManageAircraft(FlightMode.Vfr);
    private void IfrManage_Click(object sender, RoutedEventArgs e) => ManageAircraft(FlightMode.Ifr);

    private void ManageAircraft(FlightMode mode)
    {
        var window = new AircraftWindow(_aircraft, mode, AircraftFor(mode)?.Id) { Owner = this };
        window.ShowDialog();
        if (!window.Changed) return;

        // Les deux listes sont relues : une fiche modifiée ou supprimée peut apparaître ou disparaître des deux.
        FillAircraft(FlightMode.Vfr, AircraftFor(FlightMode.Vfr)?.Id);
        FillAircraft(FlightMode.Ifr, AircraftFor(FlightMode.Ifr)?.Id);
        AfterAircraftChange();
    }

    private void Range_Click(object sender, RoutedEventArgs e) => RefreshRange();

    // ---------- affichage ----------

    private void UpdateAircraftUi()
    {
        VfrAircraftInfo.Text = Describe(AircraftFor(FlightMode.Vfr), _mode == FlightMode.Vfr, FlightMode.Vfr);
        IfrAircraftInfo.Text = Describe(AircraftFor(FlightMode.Ifr), _mode == FlightMode.Ifr, FlightMode.Ifr);
    }

    private static string Describe(Aircraft? a, bool active, FlightMode mode)
    {
        if (a is null) return "Aucun appareil : ouvre « Gérer… » pour en créer un.";
        string text = string.Create(CultureInfo.GetCultureInfo("fr-FR"),
            $"{a.CruiseKt:0} kt · {a.BurnLph:0.#} L/h · {a.FuelCapacityL:N0} L\nAutonomie {NavMath.FormatTime(a.EnduranceMinutes)} · portée utile {a.UsableRangeNm(mode):N0} NM");
        return active ? text + "\n▶ utilisé pour le log de navigation" : text;
    }

    /// <summary>Avertit quand le carburant nécessaire dépasse ce que l'appareil actif peut emporter.</summary>
    private string FuelWarning(NavLog log) =>
        ActiveAircraft is { } a && log.TotalFuelL > a.FuelCapacityL
            ? $"   ⚠ plus que les {a.FuelCapacityL:N0} L de {a.Name}"
            : "";

    // ---------- cercles de portée ----------

    /// <summary>Les deux cercles de l'appareil actif autour du départ, avec leurs contours (recalculés quand la route ou l'appareil change).</summary>
    private sealed record RangeCircles(
        Waypoint Origin, Aircraft Aircraft, double UsableNm, double FullNm, double ReserveMin,
        IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> UsableLines, IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> FullLines);

    private RangeCircles? _range;

    private static readonly Color RangeOrange = Color.FromArgb(255, 217, 119, 6);

    /// <summary>
    /// Deux cercles centrés sur le premier point de la route : la <b>portée utile</b> (tout le carburant moins la réserve, trait plein) et
    /// la portée <b>tout carburant</b> (trait fin tireté). Vent nul, vitesse de croisière, montée et descente ignorées.
    /// </summary>
    private void RefreshRange()
    {
        if (!_aircraftReady) return;
        var aircraft = ActiveAircraft;
        if (RangeCheck.IsChecked != true || aircraft is null || _route.Count == 0)
        {
            _range = null;
        }
        else
        {
            var origin = _route[0];
            double full = aircraft.RangeNm, usable = aircraft.UsableRangeNm(_mode);
            // 360 points : assez serré pour poser l'étiquette sur le cercle même à fort zoom.
            _range = new RangeCircles(origin, aircraft, usable, full, aircraft.ReserveFor(_mode),
                usable > 0 ? RouteGeometry.CircleLines(origin.Lat, origin.Lon, usable, 360) : [],
                RouteGeometry.CircleLines(origin.Lat, origin.Lon, full, 360));
        }
        UpdateRangeView();
    }

    /// <summary>
    /// Dessine les cercles pour la zone actuellement affichée. Un cercle plus grand que l'écran n'est pas dessiné (on ne verrait rien) :
    /// le panneau dit où se trouve la zone affichée par rapport à la portée. Les étiquettes se posent sur le point visible le plus proche
    /// du centre de l'écran. Rappelée quand la vue change.
    /// </summary>
    private void UpdateRangeView()
    {
        var features = new List<IFeature>();
        var fr = CultureInfo.GetCultureInfo("fr-FR");

        if (_range is { } r)
        {
            var view = CurrentViewBounds();
            var state = view is null
                ? RangeViewState.Crossing
                : RangeView.Classify(r.Origin.Lat, r.Origin.Lon, RangeView.Grid(view.Value.MinLat, view.Value.MinLon, view.Value.MaxLat, view.Value.MaxLon), r.UsableNm, r.FullNm);

            if (state == RangeViewState.Crossing && view is { } v)
            {
                AddRangeLines(features, r.FullLines, new Pen(Color.FromArgb(215, 217, 119, 6), 1.5) { PenStyle = PenStyle.Dash }, 3.5);
                if (r.UsableNm > 0) AddRangeLines(features, r.UsableLines, new Pen(RangeOrange, 2.5), 5.5);

                if (r.UsableNm > 0)
                    AddRangeLabel(features, r.UsableLines, v, string.Create(fr, $"Portée utile {r.UsableNm:N0} NM (réserve {r.ReserveMin:0} min) · {r.Aircraft.Name}"), -12);
                AddRangeLabel(features, r.FullLines, v, string.Create(fr, $"Tout le carburant : {r.FullNm:N0} NM"), r.UsableNm > 0 ? 12 : -12);
            }

            RangeInfo.Text = RangeText(r, state, fr);
        }
        else RangeInfo.Text = "";

        _rangeLayer.Features = features;
        _rangeLayer.DataHasChanged();
        MapControl.Refresh();
    }

    private static string RangeText(RangeCircles r, RangeViewState state, CultureInfo fr)
    {
        string numbers = r.UsableNm > 0
            ? string.Create(fr, $"Portée utile {r.UsableNm:N0} NM (réserve {r.ReserveMin:0} min), tout le carburant {r.FullNm:N0} NM, autour de {r.Origin.Name}. Sans vent.")
            : string.Create(fr, $"L'autonomie ({r.FullNm:N0} NM) ne dépasse pas la réserve de {r.ReserveMin:0} min : pas de portée utile. Tout le carburant : {r.FullNm:N0} NM autour de {r.Origin.Name}. Sans vent.");
        return state switch
        {
            RangeViewState.InsideUsable => numbers + "\nToute la zone affichée est dans la portée utile : dézoome pour voir le cercle.",
            RangeViewState.InsideReserve => numbers + "\nLa zone affichée est dans la réserve : au-delà de la portée utile, mais dans la portée tout carburant.",
            RangeViewState.Beyond => numbers + "\nLa zone affichée est hors de portée, même avec tout le carburant.",
            _ => numbers,
        };
    }

    /// <summary>Limites de la zone affichée en latitude et longitude ; null tant que la carte n'a pas de taille.</summary>
    private (double MinLat, double MinLon, double MaxLat, double MaxLon, double CenterLat, double CenterLon)? CurrentViewBounds()
    {
        var vp = MapControl.Map.Navigator.Viewport;
        if (vp.Width <= 0 || vp.Height <= 0) return null;
        var extent = vp.ToExtent();
        var (lonA, latA) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        var (lonB, latB) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
        var (cLon, cLat) = SphericalMercator.ToLonLat(vp.CenterX, vp.CenterY);
        return (Math.Min(latA, latB), Math.Min(lonA, lonB), Math.Max(latA, latB), Math.Max(lonA, lonB), cLat, cLon);
    }

    private static void AddRangeLines(List<IFeature> features, IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> lines, Pen pen, double casingWidth)
    {
        foreach (var line in lines)
        {
            var coordinates = line.Select(p =>
            {
                var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                return new Coordinate(x, y);
            }).ToArray();
            var feature = new GeometryFeature(new LineString(coordinates));
            feature.Styles.Add(new VectorStyle { Line = new Pen(Color.White, casingWidth) });
            feature.Styles.Add(new VectorStyle { Line = pen });
            features.Add(feature);
        }
    }

    private static void AddRangeLabel(List<IFeature> features, IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> lines,
        (double MinLat, double MinLon, double MaxLat, double MaxLon, double CenterLat, double CenterLon) view, string text, double offsetY)
    {
        if (RangeView.NearestVisiblePoint(lines, view.MinLat, view.MinLon, view.MaxLat, view.MaxLon, view.CenterLat, view.CenterLon) is not { } p) return;
        var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
        var label = new PointFeature(new MPoint(x, y));
        label.Styles.Add(new LabelStyle
        {
            Text = text,
            ForeColor = Color.FromArgb(255, 146, 64, 14),
            BackColor = new Brush(Color.FromArgb(225, 255, 255, 255)),
            Offset = new Offset(0, offsetY),
        });
        features.Add(label);
    }
}
