using System.Windows;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using VFRNav.Core.Models;
using VFRNav.Core.StormAtc;

namespace VFRNav.App;

// ---------- Points de report VFR (quand StormATC les connaît) ----------
public partial class MainWindow
{
    private readonly MemoryLayer _reportLayer = new("Points de report");
    private List<(string Icao, ReportingPoint Point)> _reportingPoints = [];

    private void ApplyReportingPoints()
    {
        _reportingPoints = _stormAtc is null
            ? []
            : _stormAtc.All.Where(a => a.Procedures is not null)
                .SelectMany(a => a.Procedures!.ReportingPoints.Where(p => p.Lat is not null && p.Lon is not null).Select(p => (a.Icao, p)))
                .ToList();

        _reportLayer.Features = _reportingPoints.Select((r, i) => ReportingFeature(r.Icao, r.Point, i)).ToList();
        _reportLayer.Enabled = ReportCheck.IsChecked == true;
        _reportLayer.DataHasChanged();
        MapControl.Refresh();
    }

    /// <summary>La case « Points de report » masque ou montre les triangles ; masqués, ils ne réagissent plus au clic.</summary>
    private void ReportFilter_Click(object sender, RoutedEventArgs e)
    {
        _reportLayer.Enabled = ReportCheck.IsChecked == true;
        MapControl.Refresh();
    }

    private static IFeature ReportingFeature(string icao, ReportingPoint point, int index)
    {
        var (x, y) = SphericalMercator.FromLonLat(point.Lon!.Value, point.Lat!.Value);
        var feature = new PointFeature(new MPoint(x, y));
        feature["rp"] = index;
        feature.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Triangle,
            SymbolScale = 0.45,
            Fill = new Brush(Color.FromArgb(255, 250, 200, 20)),
            Outline = new Pen(Color.FromArgb(255, 90, 60, 0), 1.5)
        });
        feature.Styles.Add(new LabelStyle
        {
            Text = $"{icao} {point.Name}",
            Offset = new Offset(0, 14),
            ForeColor = Color.FromArgb(255, 110, 75, 0),
            Halo = new Pen(Color.White, 2),
            BackColor = new Brush(Color.Transparent),
            MaxVisible = 160
        });
        return feature;
    }

    /// <summary>Un clic sur un point de report l'ajoute à la route sous le nom « LFPN-S ».</summary>
    private bool TryAddReportingPoint(MapEventArgs e)
    {
        if (ReportCheck.IsChecked != true) return false;
        var info = e.GetMapInfo([_reportLayer]);
        if (info?.Feature?["rp"] is not int index || index >= _reportingPoints.Count) return false;
        var (icao, p) = _reportingPoints[index];
        _route.Add(new Waypoint
        {
            Name = $"{icao}-{p.Name}",
            FullName = string.Join(" — ", new[] { p.Location, p.Purpose }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Lat = p.Lat!.Value,
            Lon = p.Lon!.Value
        });
        return true;
    }
}
