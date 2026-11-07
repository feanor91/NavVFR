using System.Windows;
using System.Windows.Controls;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using VFRNav.Core.Ifr;
using VFRNav.Core.Models;

namespace VFRNav.App;

// ---------- SID et STAR dessinées sur la carte pour aider à les choisir ----------
public partial class MainWindow
{
    private readonly MemoryLayer _procedureLayer = new("SID/STAR") { Style = null };

    // SID en turquoise, STAR en orange : la procédure choisie dans la liste est épaisse, les autres en filigrane.
    private static readonly Color SidColor = Color.FromArgb(255, 0, 150, 150);
    private static readonly Color StarColor = Color.FromArgb(255, 214, 120, 0);

    // Procédure survolée dans une liste déroulante : on la montre avant même qu'elle soit choisie.
    private string? _hoverSid, _hoverStar;
    private readonly List<(double X, double Y)> _chosenPoints = [];

    private void IfrProcedure_Changed(object sender, RoutedEventArgs e)
    {
        // Choisir une procédure dans la liste zoome la carte dessus (pas pendant un remplissage automatique des listes).
        bool fit = sender is ComboBox box && IsLoaded && !_programmatic && Chosen(box) is not null;
        RefreshProcedureMap(fit);
    }

    private void IfrProcedureItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ComboBoxItem item || ItemsControl.ItemsControlFromItemContainer(item) is not ComboBox box) return;
        string? text = item.Content as string;
        string? ident = text is null || text == AutoChoice || text == NoneChoice ? null : VFRNav.Core.Ifr.ProcedureLabel.Ident(text);
        if (ReferenceEquals(box, IfrSidBox)) _hoverSid = ident; else if (ReferenceEquals(box, IfrStarBox)) _hoverStar = ident;
        RefreshProcedureMap();
    }

    private void IfrProcedureDropDown_Closed(object sender, EventArgs e)
    {
        _hoverSid = _hoverStar = null;
        RefreshProcedureMap();
    }

    private void IfrPanel_Toggled(object sender, RoutedEventArgs e) => RefreshProcedureMap();

    /// <summary>
    /// Redessine les SID du terrain de départ et les STAR du terrain d'arrivée : celles de la piste choisie, ou toutes si « Auto ».
    /// Une procédure choisie dans la liste est tracée en gras, les autres en pâle. Rien n'est dessiné quand la case est décochée
    /// ou que le panneau IFR est replié.
    /// </summary>
    private void RefreshProcedureMap(bool fit = false)
    {
        if (!IsLoaded) return;
        var features = new List<IFeature>();
        _chosenPoints.Clear();
        if (IfrProcedureMapCheck.IsChecked == true && IfrPanel.IsExpanded)
        {
            Draw(features, ResolveIfrCode(IfrDepBox.Text), ProcedureKind.Sid, Chosen(IfrDepRunwayBox), _hoverSid ?? Chosen(IfrSidBox));
            Draw(features, ResolveIfrCode(IfrArrBox.Text), ProcedureKind.Star, Chosen(IfrArrRunwayBox), _hoverStar ?? Chosen(IfrStarBox));
        }
        _procedureLayer.Features = features;
        _procedureLayer.DataHasChanged();
        if (fit && _chosenPoints.Count > 1)
        {
            var rect = new MRect(_chosenPoints.Min(p => p.X), _chosenPoints.Min(p => p.Y), _chosenPoints.Max(p => p.X), _chosenPoints.Max(p => p.Y));
            MapControl.Map.Navigator.ZoomToBox(rect.Grow(Math.Max(rect.Width, rect.Height) * 0.15 + 2000));
        }
        MapControl.Refresh();
    }

    private void Draw(List<IFeature> features, string icao, ProcedureKind kind, string? runway, string? chosen)
    {
        if (icao.Length != 4 || _ifrData.Find(icao) is not { } airport) return;
        bool sid = kind == ProcedureKind.Sid;
        var color = sid ? SidColor : StarColor;

        foreach (var procedure in _ifrData.ProceduresFor(icao, kind))
        {
            bool isChosen = chosen is not null && string.Equals(procedure.Ident, chosen, StringComparison.OrdinalIgnoreCase);
            bool faint = chosen is not null && !isChosen;
            (double x, double y)? label = null;

            foreach (var variant in procedure.Variants)
            {
                if (!variant.AppliesTo(runway) || variant.Legs.Count == 0) continue;
                var points = new List<IfrFix>();
                if (sid) points.Add(airport);
                foreach (var leg in variant.Legs)
                    if (_ifrData.Find(leg.Fix) is { } fix) points.Add(fix);
                if (!sid) points.Add(airport);
                if (points.Count < 2) continue;

                var coordinates = points.Select(p =>
                {
                    var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                    return new Coordinate(x, y);
                }).ToArray();
                var feature = new GeometryFeature(new LineString(coordinates));
                int alpha = faint ? 70 : isChosen ? 255 : 150;
                feature.Styles.Add(new VectorStyle { Line = new Pen(Color.FromArgb((byte)Math.Min(alpha, 200), 255, 255, 255), isChosen ? 7 : 3) });
                feature.Styles.Add(new VectorStyle { Line = new Pen(Color.FromArgb((byte)alpha, color.R, color.G, color.B), isChosen ? 4 : faint ? 1.2 : 2) });
                features.Add(feature);
                if (isChosen) _chosenPoints.AddRange(coordinates.Select(c => (c.X, c.Y)));

                // Nom au bout qui s'éloigne du terrain : sortie d'une SID, entrée d'une STAR.
                var far = sid ? points[^1] : points[0];
                label ??= SphericalMercator.FromLonLat(far.Lon, far.Lat);
            }

            if (label is { } at && !faint)
            {
                var pf = new PointFeature(new MPoint(at.x, at.y));
                pf.Styles.Add(new LabelStyle
                {
                    Text = procedure.Ident,
                    ForeColor = isChosen ? Color.White : color,
                    BackColor = new Brush(isChosen ? color : Color.FromArgb(200, 255, 255, 255)),
                    Halo = new Pen(Color.White, 1),
                    Offset = new Offset(0, -12),
                    MaxVisible = isChosen ? double.MaxValue : 900,
                });
                features.Add(pf);
            }
        }
    }
}
