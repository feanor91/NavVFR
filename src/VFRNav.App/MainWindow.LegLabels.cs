using System.Globalization;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.App;

// ---------- Étiquette de chaque branche de la route : route magnétique et longueur ----------
public partial class MainWindow
{
    private readonly MemoryLayer _legLabelLayer = new("Branches") { Style = null }; // pas de symbole par défaut : seule l'étiquette est dessinée
    private static readonly CultureInfo FrCulture = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Longueur de branche, en pixels, en dessous de laquelle l'étiquette est masquée (elle recouvrirait les points voisins).</summary>
    private const double LegLabelMinPx = 110;

    /// <summary>Distance, en pixels, entre l'étiquette et le trait.</summary>
    private const double LegLabelGapPx = 15;

    private void UpdateLegLabels(IReadOnlyList<LegResult>? legs)
    {
        var features = new List<IFeature>();
        for (int i = 0; legs is not null && i < legs.Count && i + 1 < _route.Count; i++)
            features.Add(LegLabel(_route[i], _route[i + 1], legs[i]));
        _legLabelLayer.Features = features;
        _legLabelLayer.DataHasChanged();
    }

    private static IFeature LegLabel(Waypoint a, Waypoint b, LegResult leg)
    {
        // Milieu sur le grand cercle, puis projection sur la carte.
        var (midLat, midLon) = RouteGeometry.Interpolate(a.Lat, a.Lon, b.Lat, b.Lon, 0.5);
        var (mx, my) = SphericalMercator.FromLonLat(midLon, midLat);
        var (ax, ay) = SphericalMercator.FromLonLat(a.Lon, a.Lat);
        var (bx, by) = SphericalMercator.FromLonLat(b.Lon, b.Lat);

        double dx = bx - ax, dy = by - ay, length = Math.Sqrt(dx * dx + dy * dy);

        // Décalage perpendiculaire au trait, du côté du haut de l'écran (à droite pour une branche verticale).
        double nx = length > 0 ? -dy / length : 0, ny = length > 0 ? dx / length : 1;
        if (ny < -1e-6 || (Math.Abs(ny) <= 1e-6 && nx < 0)) (nx, ny) = (-nx, -ny);
        var offset = new Offset(nx * LegLabelGapPx, -ny * LegLabelGapPx);

        string text = string.Create(FrCulture, $"Rm {leg.MagneticCourse:000}° · {leg.DistanceNm:0.0} NM");
        var feature = new PointFeature(new MPoint(mx, my));
        feature.Styles.Add(new LabelStyle
        {
            Text = text,
            ForeColor = Color.FromArgb(255, 150, 20, 15),
            BackColor = new Brush(Color.FromArgb(215, 255, 255, 255)),
            Halo = new Pen(Color.White, 1.5),
            Offset = offset,
            MaxVisible = length / LegLabelMinPx // résolution (unités de carte par pixel) au-dessus de laquelle la branche est trop courte à l'écran
        });
        return feature;
    }
}
