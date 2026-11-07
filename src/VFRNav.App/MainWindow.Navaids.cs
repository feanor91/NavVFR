using System.Net.Http;
using System.Windows;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Balises VOR et NDB (ADF) ----------
public partial class MainWindow
{
    private IReadOnlyList<Navaid> _navaids = [];

    private async Task LoadNavaidsAsync(IReadOnlyList<string> countries)
    {
        try { _navaids = await new OurAirportsService(_http).LoadNavaidsAsync(countries); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _navaids = [];
            StatusText.Text = Loc.Get("Navaids_Balises_indisponibles") + ex.Message;
        }
        ApplyNavaidFilter();
    }

    private void ApplyNavaidFilter()
    {
        bool vor = NavVorCheck.IsChecked == true, ndb = NavNdbCheck.IsChecked == true;
        var shown = _navaids.Select((n, i) => (n, i)).Where(t => t.n.Kind == NavaidKind.Vor ? vor : ndb).ToList();
        _navaidLayer.Features = shown.Select(t => NavaidFeature(t.n, t.i)).ToList();
        _navaidLayer.DataHasChanged();
        NavCount.Text = _navaids.Count == 0 ? "" : Loc.F("Navaids_balises_affichees_sur", shown.Count, _navaids.Count);
        MapControl.Refresh();
    }

    private void NavFilter_Click(object sender, RoutedEventArgs e) => ApplyNavaidFilter();

    private static IFeature NavaidFeature(Navaid n, int index)
    {
        var (x, y) = SphericalMercator.FromLonLat(n.Lon, n.Lat);
        var feature = new PointFeature(new MPoint(x, y));
        feature["nav"] = index;
        bool vor = n.Kind == NavaidKind.Vor;
        feature.Styles.Add(new SymbolStyle
        {
            // VOR : losange violet ; NDB (ADF) : rond orange.
            SymbolType = vor ? SymbolType.Rectangle : SymbolType.Ellipse,
            SymbolRotation = vor ? 45 : 0,
            SymbolScale = vor ? 0.42 : 0.45,
            Fill = new Brush(vor ? Color.FromArgb(255, 120, 60, 200) : Color.FromArgb(255, 235, 125, 20)),
            Outline = new Pen(Color.White, 1.5)
        });
        feature.Styles.Add(new LabelStyle
        {
            Text = $"{n.Ident} {n.FrequencyText}",
            Offset = new Offset(0, -16),
            ForeColor = vor ? Color.FromArgb(255, 90, 40, 160) : Color.FromArgb(255, 170, 85, 10),
            Halo = new Pen(Color.White, 2),
            BackColor = new Brush(Color.Transparent),
            MaxVisible = 220
        });
        return feature;
    }

    /// <summary>Un clic sur une balise l'ajoute à la route sous son nom.</summary>
    private bool TryAddNavaid(MapEventArgs e)
    {
        var info = e.GetMapInfo([_navaidLayer]);
        if (info?.Feature?["nav"] is not int index || index >= _navaids.Count) return false;
        _route.Add(_navaids[index].ToWaypoint());
        return true;
    }
}
