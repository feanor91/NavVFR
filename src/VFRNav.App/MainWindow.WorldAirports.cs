using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mapsui.Extensions;
using Mapsui.Projections;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Terrains du monde : filtres précis, affichage réparti selon le zoom, recherche ----------
public partial class MainWindow
{
    private static readonly string[] Continents = ["", "EU", "NA", "SA", "AF", "AS", "OC", "AN"];
    private static readonly string[] ContinentNames =
        [Loc.Get("WorldAirports_Tous_les_continents"), Loc.Get("WorldAirports_Europe"), Loc.Get("WorldAirports_Amerique_du_Nord"), Loc.Get("WorldAirports_Amerique_du_Sud"), Loc.Get("WorldAirports_Afrique"), Loc.Get("WorldAirports_Asie"), Loc.Get("WorldAirports_Oceanie"), Loc.Get("WorldAirports_Antarctique")];

    private readonly AirportDataStore _airportStore;
    private AirportSearchIndex _airportIndex = new([]);
    private IReadOnlyList<Airport> _shownAirports = [];
    private IReadOnlyList<Airport> _searchResults = [];

    private void RestoreWorldFilterControls()
    {
        var f = _settings.Filter;
        FScheduled.IsChecked = f.ScheduledOnly;
        FIata.IsChecked = f.IataOnly;
        FPaved.IsChecked = f.PavedOnly;
        FMinRunway.Text = f.MinRunwayFt > 0 ? f.MinRunwayFt.ToString(CultureInfo.InvariantCulture) : "";
        FCountries.Text = string.Join(" ", f.Countries ?? []);
        FContinent.ItemsSource = ContinentNames;
        FContinent.SelectedIndex = Math.Max(0, Array.IndexOf(Continents, f.Continent ?? ""));
        FNearRoute.IsChecked = _settings.FilterNearRoute;
        FNearRouteNm.Text = Fmt(_settings.FilterNearRouteNm);
    }

    private AirportFilter ReadFilter() => new(
        FWithCode.IsChecked == true, FWithoutCode.IsChecked == true, FVacOnly.IsChecked == true,
        FLarge.IsChecked == true, FMedium.IsChecked == true, FSmall.IsChecked == true,
        ScheduledOnly: FScheduled.IsChecked == true,
        IataOnly: FIata.IsChecked == true,
        MinRunwayFt: int.TryParse(FMinRunway.Text.Trim(), out int ft) && ft > 0 ? ft : 0,
        PavedOnly: FPaved.IsChecked == true,
        Countries: AirportFilter.ParseCountries(FCountries.Text),
        Continent: FContinent.SelectedIndex > 0 ? Continents[FContinent.SelectedIndex] : null);

    private double NearRouteNm =>
        double.TryParse(FNearRouteNm.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var nm) && nm > 0 ? nm : 30;

    /// <summary>Zone visible de la carte, en degrés ; le monde entier si la carte n'est pas encore prête.</summary>
    private GeoBounds VisibleBounds()
    {
        var extent = MapControl.Map.Navigator.Viewport.ToExtent();
        if (extent is null) return new GeoBounds(-90, -180, 90, 180);
        (double minLon, double minLat) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        (double maxLon, double maxLat) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
        return new GeoBounds(minLat, minLon, maxLat, maxLon);
    }

    private void ApplyAirportFilter()
    {
        var filter = ReadFilter();
        IEnumerable<Airport> candidates = _airports.Where(filter.Matches);
        if (FNearRoute.IsChecked == true && _route.Count > 0)
            candidates = AirportDisplay.NearRoute(candidates, _route.ToList(), NearRouteNm);

        // Les terrains de la route restent toujours dessinés, même hors filtres.
        var routeCodes = _route.Where(w => w.IsAirport).Select(w => w.Icao!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinned = routeCodes.Count == 0 ? [] : _airports.Where(a => routeCodes.Contains(a.Ident)).ToList();

        double cell = AirportDisplay.CellPixels * MapControl.Map.Navigator.Viewport.Resolution;
        var selection = AirportDisplay.Select(candidates, VisibleBounds(), cell, AirportDisplay.DefaultCap, pinned);
        _shownAirports = selection.Shown;

        _airportLayer.Features = selection.Shown.Select(AirportFeature).ToList();
        _airportLayer.DataHasChanged();
        FilterCount.Text = selection.Hidden > 0
            ? Loc.F("WorldAirports_terrains_affiches_sur_dans_cette", selection.Shown.Count, selection.InView, selection.Hidden, _airports.Count)
            : Loc.F("WorldAirports_terrains_affiches_dans_cette_zone", selection.Shown.Count, _airports.Count);
        MapControl.Refresh();
    }

    private void FilterText_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && _airports.Count > 0) ApplyAirportFilter();
    }

    private void FilterChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _airports.Count > 0) ApplyAirportFilter();
    }

    private void ShowWorldAirportsInfo(WorldAirportData world)
    {
        string date = world.DownloadedAt.LocalDateTime.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
        WorldAirportsInfo.Text = Loc.F("WorldAirports_terrains_OurAirports_du", world.Airports.Count, (world.FromCache ? "cache" : Loc.Get("WorldAirports_telecharges")), date) +
                                 (world.Note is null ? "" : "\n" + world.Note);
    }

    private async void RefreshWorldAirports_Click(object sender, RoutedEventArgs e) => await LoadDataAsync(refreshWorld: true);

    // ---------- Recherche ----------
    private void AptSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _searchResults = _airportIndex.Find(AptSearchBox.Text);
        AptSearchList.ItemsSource = _searchResults.Select(a =>
            $"{a.Ident}{(a.Iata is null ? "" : "/" + a.Iata)} · {a.Name}{(a.City is null ? "" : ", " + a.City)} ({a.Country})").ToList();
        AptSearchAdd.IsEnabled = false;
    }

    private Airport? SelectedSearchResult =>
        AptSearchList.SelectedIndex is >= 0 and var i && i < _searchResults.Count ? _searchResults[i] : null;

    /// <summary>Choisir un résultat centre la carte dessus, assez près pour que le terrain soit affiché.</summary>
    private void AptSearchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedSearchResult is not { } apt) { AptSearchAdd.IsEnabled = false; return; }
        AptSearchAdd.IsEnabled = true;
        var (x, y) = SphericalMercator.FromLonLat(apt.Lon, apt.Lat);
        var nav = MapControl.Map.Navigator;
        double resolution = Math.Min(nav.Viewport.Resolution, 300); // environ 1:1 000 000 : le terrain ressort de la répartition
        nav.CenterOnAndZoomTo(new Mapsui.MPoint(x, y), resolution);
        ApplyAirportFilter();
    }

    private void AptSearchAdd_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSearchResult is not { } apt) return;
        _route.Add(apt.ToWaypoint());
        StatusText.Text = Loc.F("WorldAirports_ajoute_a_la_route", apt.Ident);
    }
}

// ---------- Langue de l'interface ----------
public partial class MainWindow
{
    private bool _restoringLanguage;

    private void RestoreLanguageBox()
    {
        _restoringLanguage = true;
        try
        {
            LanguageBox.ItemsSource = Loc.Languages.Select(l => l.Name).ToList();
            LanguageBox.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == Loc.Resolve(_settings.Language, System.Globalization.CultureInfo.InstalledUICulture)));
        }
        finally { _restoringLanguage = false; }
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringLanguage || !IsLoaded || LanguageBox.SelectedIndex < 0) return;
        _settings.Language = Loc.Languages[LanguageBox.SelectedIndex].Code;
        SaveSettings();
        // Message dans les deux langues : celle qui vient d'être choisie ne s'applique qu'au prochain lancement.
        MessageBox.Show(this, "Redémarrez MSFS Rhumb pour changer la langue.\nRestart MSFS Rhumb to change the language.", "MSFS Rhumb");
    }
}
