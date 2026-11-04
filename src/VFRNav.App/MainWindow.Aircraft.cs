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
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Appareils : un choix par type de navigation, gestion, paramètres du log et cercle de portée ----------
public partial class MainWindow
{
    private readonly AircraftCatalog _aircraft = AircraftCatalog.Load();
    private readonly MemoryLayer _rangeLayer = new(Loc.Get("Aircraft_Portee")) { Style = null };
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
        string level = ft >= 18000 ? $"FL{ft / 100:000}" : string.Create(VFRNav.Core.Localization.Loc.FormatCulture, $"{ft:N0} ft");
        return Loc.F("Aircraft_croisiere", level, (_mode == FlightMode.Ifr ? "IFR" : "VFR")) + (ActiveAircraft is { } a ? " · " + a.Name : "");
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
        if (a is null) return Loc.Get("Aircraft_Aucun_appareil_ouvre_Gerer_pour");
        string text = Loc.F(VFRNav.Core.Localization.Loc.FormatCulture, "Aircraft_kt_L_h_L_Autonomie", a.CruiseKt, a.BurnLph, a.FuelCapacityL, NavMath.FormatTime(a.EnduranceMinutes), a.UsableRangeNm(mode));
        return active ? text + Loc.Get("Aircraft_utilise_pour_le_log_de") : text;
    }

    /// <summary>Avertit quand le carburant nécessaire dépasse ce que l'appareil actif peut emporter.</summary>
    private string FuelWarning(NavLog log) =>
        ActiveAircraft is { } a && FuelCheck.Message(a, _mode, log) is { } message ? "   " + message : "";

    // ---------- cercles de portée ----------

    /// <summary>Les deux cercles de l'appareil actif autour du départ, avec leurs contours (recalculés quand la route ou l'appareil change).</summary>
    private sealed record RangeCircles(
        Waypoint Origin, Aircraft Aircraft, double UsableNm, double FullNm, double ReserveMin,
        IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> UsableLines, IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> FullLines,
        IReadOnlyList<(double Lat, double Lon)>? OutsideRing, IReadOnlyList<(double Lat, double Lon)>? BandRing, bool UsableEnclosesPole);

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
            // Un cercle qui enferme un pôle ne peut pas être fermé sur la carte : on grise la zone hors de portée à la place du trait.
            char? fullPole = RangeShading.EnclosedPole(origin.Lat, full);
            char? usablePole = usable > 0 ? RangeShading.EnclosedPole(origin.Lat, usable) : null;
            _range = new RangeCircles(origin, aircraft, usable, full, aircraft.ReserveFor(_mode),
                usable > 0 ? RouteGeometry.CircleLines(origin.Lat, origin.Lon, usable, 360) : [],
                RouteGeometry.CircleLines(origin.Lat, origin.Lon, full, 360),
                fullPole is null ? null : RangeShading.OutsideRing(origin.Lat, origin.Lon, full),
                fullPole is not null && fullPole == usablePole ? RangeShading.BandRing(origin.Lat, origin.Lon, usable, full) : null,
                usablePole is not null);
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
        var fr = VFRNav.Core.Localization.Loc.FormatCulture;

        if (_range is { } r)
        {
            var view = CurrentViewBounds();
            var state = view is null
                ? RangeViewState.Crossing
                : RangeView.Classify(r.Origin.Lat, r.Origin.Lon, RangeView.Grid(view.Value.MinLat, view.Value.MinLon, view.Value.MaxLat, view.Value.MaxLon), r.UsableNm, r.FullNm);

            if (state == RangeViewState.Crossing && view is { } v)
            {
                if (r.OutsideRing is not null)
                {
                    // Cercle qui enferme un pôle : gris = hors de portée, orange clair = réserve (entre la portée utile et tout le carburant).
                    AddRangeShade(features, r.OutsideRing, Color.FromArgb(105, 60, 70, 85));
                    if (r.BandRing is not null) AddRangeShade(features, r.BandRing, Color.FromArgb(90, 217, 119, 6));
                    if (r.UsableNm > 0 && !r.UsableEnclosesPole) AddRangeLines(features, r.UsableLines, new Pen(RangeOrange, 2.5), 5.5);
                }
                else
                {
                    AddRangeLines(features, r.FullLines, new Pen(Color.FromArgb(215, 217, 119, 6), 1.5) { PenStyle = PenStyle.Dash }, 3.5);
                    if (r.UsableNm > 0) AddRangeLines(features, r.UsableLines, new Pen(RangeOrange, 2.5), 5.5);
                }

                if (r.UsableNm > 0)
                    AddRangeLabel(features, r.UsableLines, v, Loc.F(fr, "Aircraft_Portee_utile_NM_reserve_min", r.UsableNm, r.ReserveMin, r.Aircraft.Name), -12);
                AddRangeLabel(features, r.FullLines, v, Loc.F(fr, "Aircraft_Tout_le_carburant_NM", r.FullNm), r.UsableNm > 0 ? 12 : -12);
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
            ? Loc.F(fr, "Aircraft_Portee_utile_NM_reserve_min_2", r.UsableNm, r.ReserveMin, r.FullNm, r.Origin.Name)
            : Loc.F(fr, "Aircraft_L_autonomie_NM_ne_depasse", r.FullNm, r.ReserveMin, r.FullNm, r.Origin.Name);
        // Quand le cercle enferme un pôle, il n'y a pas de trait : on explique le grisé.
        if (r.OutsideRing is not null && state == RangeViewState.Crossing)
            numbers += Loc.Get("Aircraft_Le_cercle_enferme_un_pole") + (r.BandRing is not null ? Loc.Get("Aircraft_la_bande_orange_est_la") : ".");
        return state switch
        {
            RangeViewState.InsideUsable => numbers + Loc.Get("Aircraft_Toute_la_zone_affichee_est"),
            RangeViewState.InsideReserve => numbers + Loc.Get("Aircraft_La_zone_affichee_est_dans"),
            RangeViewState.Beyond => numbers + Loc.Get("Aircraft_La_zone_affichee_est_hors"),
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

    /// <summary>Surface teintée (sans contour) à partir d'un contour en latitude/longitude.</summary>
    private static void AddRangeShade(List<IFeature> features, IReadOnlyList<(double Lat, double Lon)> ring, Color fill)
    {
        if (ring.Count < 4) return;
        var coordinates = ring.Select(p =>
        {
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            return new Coordinate(x, y);
        }).ToList();
        if (!coordinates[0].Equals2D(coordinates[^1])) coordinates.Add(coordinates[0]);
        var feature = new GeometryFeature(new Polygon(new LinearRing(coordinates.ToArray())));
        feature.Styles.Add(new VectorStyle { Fill = new Brush(fill), Outline = null, Line = null });
        features.Add(feature);
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
