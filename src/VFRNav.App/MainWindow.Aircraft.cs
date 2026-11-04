using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mapsui;
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
        VfrAircraftInfo.Text = Describe(AircraftFor(FlightMode.Vfr), _mode == FlightMode.Vfr);
        IfrAircraftInfo.Text = Describe(AircraftFor(FlightMode.Ifr), _mode == FlightMode.Ifr);
    }

    private static string Describe(Aircraft? a, bool active)
    {
        if (a is null) return "Aucun appareil : ouvre « Gérer… » pour en créer un.";
        string text = string.Create(CultureInfo.GetCultureInfo("fr-FR"),
            $"{a.CruiseKt:0} kt · {a.BurnLph:0.#} L/h · {a.FuelCapacityL:N0} L\nAutonomie {NavMath.FormatTime(a.EnduranceMinutes)} · portée {a.RangeNm:N0} NM");
        return active ? text + "\n▶ utilisé pour le log de navigation" : text;
    }

    /// <summary>Avertit quand le carburant nécessaire dépasse ce que l'appareil actif peut emporter.</summary>
    private string FuelWarning(NavLog log) =>
        ActiveAircraft is { } a && log.TotalFuelL > a.FuelCapacityL
            ? $"   ⚠ plus que les {a.FuelCapacityL:N0} L de {a.Name}"
            : "";

    // ---------- cercle de portée ----------

    /// <summary>
    /// Cercle centré sur le premier point de la route : distance parcourue avec tout le carburant
    /// à la vitesse de croisière, sans vent et sans réserve.
    /// </summary>
    private void RefreshRange()
    {
        if (!_aircraftReady) return;
        var features = new List<IFeature>();
        var aircraft = ActiveAircraft;
        bool show = RangeCheck.IsChecked == true && aircraft is not null && _route.Count > 0;

        if (show)
        {
            var origin = _route[0];
            double range = aircraft!.RangeNm;
            foreach (var line in RouteGeometry.CircleLines(origin.Lat, origin.Lon, range))
            {
                var coordinates = line.Select(p =>
                {
                    var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                    return new Coordinate(x, y);
                }).ToArray();
                var feature = new GeometryFeature(new LineString(coordinates));
                feature.Styles.Add(new VectorStyle { Line = new Pen(Color.White, 5) });
                feature.Styles.Add(new VectorStyle { Line = new Pen(Color.FromArgb(255, 217, 119, 6), 2.5) { PenStyle = PenStyle.Dash } });
                features.Add(feature);
            }

            // Étiquette au nord du cercle quand il est visible dans la projection.
            var (lat, lon) = RouteGeometry.Destination(origin.Lat, origin.Lon, 0, range);
            if (Math.Abs(lat) < 84)
            {
                var (lx, ly) = SphericalMercator.FromLonLat(lon, lat);
                var label = new PointFeature(new MPoint(lx, ly));
                label.Styles.Add(new LabelStyle
                {
                    Text = string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"Portée {range:N0} NM · {aircraft.Name}"),
                    ForeColor = Color.FromArgb(255, 146, 64, 14),
                    BackColor = new Brush(Color.FromArgb(220, 255, 255, 255)),
                    Offset = new Offset(0, -12),
                });
                features.Add(label);
            }
        }

        _rangeLayer.Features = features;
        _rangeLayer.DataHasChanged();
        RangeInfo.Text = show
            ? string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{aircraft!.RangeNm:N0} NM autour de {_route[0].Name}, sans vent ni réserve.")
            : "";
        MapControl.Refresh();
    }
}
