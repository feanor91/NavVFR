using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.App;

/// <summary>Profil vertical de la route : relief, altitude de croisière, zones traversées et points de passage.</summary>
public sealed class ProfileChart : FrameworkElement
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const double Left = 62, Right = 16, Top = 26, Bottom = 36;

    private TerrainProfile? _terrain;
    private double _cruiseFt;
    private IReadOnlyList<Waypoint> _route = [];
    private IReadOnlyList<AirspaceCrossing> _crossings = [];
    private string? _message;
    private IReadOnlyList<RouteObstacle> _obstacles = [];

    /// <summary>Un obstacle est tracé quand son sommet est à moins de 1 500 ft sous la croisière (ou au-dessus).</summary>
    public const double ObstacleRelevantMarginFt = 1500;

    public void SetData(TerrainProfile? terrain, double cruiseFt, IReadOnlyList<Waypoint> route, IReadOnlyList<AirspaceCrossing> crossings, string? message = null, IReadOnlyList<RouteObstacle>? obstacles = null)
    {
        _obstacles = obstacles ?? [];
        _terrain = terrain;
        _cruiseFt = cruiseFt;
        _route = route;
        _crossings = crossings;
        _message = message;
        InvalidateVisual();
    }

    /// <summary>Image du graphique, pour l'insérer dans la feuille de route imprimée.</summary>
    public static BitmapSource Snapshot(ProfileChart chart, double width, double height)
    {
        chart.Measure(new Size(width, height));
        chart.Arrange(new Rect(0, 0, width, height));
        chart.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, Fr, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, 1.0);

    private static Brush Solid(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static (byte R, byte G, byte B) ZoneColor(AirspaceKind kind) => kind switch
    {
        AirspaceKind.Restricted => (192, 40, 40),
        AirspaceKind.Ctr => (31, 90, 200),
        AirspaceKind.Tma => (50, 60, 170),
        AirspaceKind.Radio => (142, 68, 173),
        AirspaceKind.Leisure => (215, 125, 15),
        _ => (120, 120, 120)
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w < 80 || h < 60) return;
        dc.DrawRectangle(Brushes.White, new Pen(Solid(180, 190, 205), 1), new Rect(0, 0, w, h));

        if (_terrain is null || _terrain.Samples.Count < 2)
        {
            dc.DrawText(Text(_message ?? "Aucun profil calculé. Pose une route puis clique sur « Calculer le plan de vol ».", 13, Solid(90, 100, 120)), new Point(16, h / 2 - 10));
            return;
        }

        double total = Math.Max(_terrain.TotalNm, 0.1);
        double plotW = w - Left - Right, plotH = h - Top - Bottom;
        double zoneTop = _crossings.Select(c => Math.Min(c.Zone.UpperFt, _cruiseFt + 2500)).DefaultIfEmpty(0).Max();
        var shownObstacles = _obstacles.Where(o => o.TopFt is not null && _cruiseFt - o.TopFt < ObstacleRelevantMarginFt).ToList();
        double obstacleTop = shownObstacles.Select(o => o.TopFt!.Value + 300).DefaultIfEmpty(0).Max();
        double yMax = Math.Ceiling(Math.Max(Math.Max(Math.Max(_terrain.MaxFt + 800, _cruiseFt + 1500), zoneTop), obstacleTop) / 1000) * 1000;
        double gridStep = yMax <= 6000 ? 1000 : yMax <= 12000 ? 2000 : 5000;

        double X(double nm) => Left + nm / total * plotW;
        double Y(double ft) => Top + plotH - Math.Clamp(ft, 0, yMax) / yMax * plotH;

        var gridPen = new Pen(Solid(225, 230, 238), 1);
        var gridText = Solid(110, 120, 140);
        for (double ft = 0; ft <= yMax; ft += gridStep)
        {
            dc.DrawLine(gridPen, new Point(Left, Y(ft)), new Point(w - Right, Y(ft)));
            var label = Text($"{ft.ToString("N0", Fr)} ft", 10, gridText);
            dc.DrawText(label, new Point(Left - label.Width - 6, Y(ft) - label.Height / 2));
        }
        double nmStep = total <= 40 ? 5 : total <= 120 ? 10 : total <= 300 ? 25 : 50;
        for (double nm = 0; nm <= total; nm += nmStep)
        {
            dc.DrawLine(gridPen, new Point(X(nm), Top), new Point(X(nm), h - Bottom));
            var label = Text($"{nm:0} NM", 10, gridText);
            dc.DrawText(label, new Point(X(nm) - label.Width / 2, h - Bottom + 4));
        }

        // Zones traversées : bandes entre leur plancher et leur plafond.
        var labels = new List<(AirspaceCrossing Crossing, double X0, double X1, double YTop, double YBottom)>();
        foreach (var c in _crossings)
        {
            var (r, g, b) = ZoneColor(c.Zone.Kind);
            double x0 = X(c.EntryNm), x1 = Math.Max(X(c.ExitNm), x0 + 2);
            double yTop = Y(Math.Min(c.Zone.UpperFt, yMax)), yBottom = Y(Math.Max(c.Zone.LowerFt, 0));
            if (yBottom - yTop < 1) continue;
            dc.DrawRectangle(Solid(r, g, b, 30), new Pen(Solid(r, g, b, 190), 1), new Rect(x0, yTop, x1 - x0, yBottom - yTop));
            labels.Add((c, x0, x1, yTop, yBottom));
        }

        // Noms : les petites bandes d'abord, chacun placé sans en recouvrir un autre (sinon omis ou descendu dans la bande).
        var placed = new List<Rect>();
        foreach (var (c, x0, x1, yTop, yBottom) in labels.OrderBy(l => l.YBottom - l.YTop))
        {
            if (x1 - x0 < 56) continue;
            var (r, g, b) = ZoneColor(c.Zone.Kind);
            var name = Text(c.Zone.Name, 9, Solid(r, g, b), bold: true);
            name.MaxTextWidth = x1 - x0 - 4;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            double width = Math.Min(name.Width, x1 - x0 - 4);
            for (double y = yTop + 2; y + name.Height <= yBottom - 1; y += name.Height + 1)
            {
                var box = new Rect(x0 + 3, y, width, name.Height);
                if (placed.Any(q => q.IntersectsWith(box))) continue;
                dc.DrawText(name, box.TopLeft);
                placed.Add(box);
                break;
            }
        }

        // Relief.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(X(_terrain.Samples[0].DistanceNm), Y(0)), true, true);
            foreach (var s in _terrain.Samples) ctx.LineTo(new Point(X(s.DistanceNm), Y(s.ElevationFt)), true, false);
            ctx.LineTo(new Point(X(_terrain.Samples[^1].DistanceNm), Y(0)), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(Solid(176, 146, 104, 190), new Pen(Solid(110, 80, 45), 1.2), geometry);

        // Passages où la marge sous la croisière est inférieure à 1 000 ft.
        var danger = new Pen(Solid(215, 30, 30), 3);
        for (int i = 1; i < _terrain.Samples.Count; i++)
        {
            var a = _terrain.Samples[i - 1];
            var b = _terrain.Samples[i];
            if (Math.Max(a.ElevationFt, b.ElevationFt) > _cruiseFt - 1000)
                dc.DrawLine(danger, new Point(X(a.DistanceNm), Y(a.ElevationFt)), new Point(X(b.DistanceNm), Y(b.ElevationFt)));
        }

        // Obstacles proches de la croisière : trait du sol au sommet, rouge si la marge est inférieure à 500 ft.
        foreach (var o in shownObstacles)
        {
            double margin = _cruiseFt - o.TopFt!.Value;
            var color = margin < 500 ? Solid(200, 20, 20) : Solid(220, 120, 20);
            double x = X(o.AlongNm);
            dc.DrawLine(new Pen(color, 2.2), new Point(x, Y(o.GroundFt ?? 0)), new Point(x, Y(o.TopFt.Value)));
            dc.DrawEllipse(color, null, new Point(x, Y(o.TopFt.Value)), 2.8, 2.8);
        }
        if (shownObstacles.Count > 0)
        {
            var highest = shownObstacles.MaxBy(o => o.TopFt)!;
            var label = Text($"{highest.Obstacle.KindText} {highest.TopFt!.Value.ToString("N0", Fr)} ft", 9.5, Solid(170, 40, 20), bold: true);
            double lx = Math.Clamp(X(highest.AlongNm) - label.Width / 2, Left, w - Right - label.Width);
            dc.DrawText(label, new Point(lx, Math.Max(Top, Y(highest.TopFt.Value) - label.Height - 5)));
        }

        // Altitude de croisière.
        var cruisePen = new Pen(Solid(210, 20, 120), 2) { DashStyle = DashStyles.Dash };
        dc.DrawLine(cruisePen, new Point(Left, Y(_cruiseFt)), new Point(w - Right, Y(_cruiseFt)));
        var cruiseLabel = Text($"Croisière {_cruiseFt.ToString("N0", Fr)} ft", 10.5, Solid(210, 20, 120), bold: true);
        dc.DrawText(cruiseLabel, new Point(w - Right - cruiseLabel.Width - 4, Y(_cruiseFt) - cruiseLabel.Height - 1));

        // Points de passage.
        double done = 0;
        var wpPen = new Pen(Solid(70, 80, 100), 1) { DashStyle = DashStyles.Dot };
        for (int i = 0; i < _route.Count; i++)
        {
            if (i > 0) done += NavMath.DistanceNm(_route[i - 1], _route[i]);
            dc.DrawLine(wpPen, new Point(X(done), Top), new Point(X(done), h - Bottom));
            var label = Text(string.IsNullOrEmpty(_route[i].Icao) ? _route[i].Name : _route[i].Icao!, 10, Solid(30, 40, 70), bold: true);
            double lx = Math.Clamp(X(done) - label.Width / 2, 2, w - label.Width - 2);
            dc.DrawText(label, new Point(lx, 5));
        }

        // Point le plus haut.
        if (_terrain.Highest is { } top)
        {
            dc.DrawEllipse(Solid(215, 30, 30), new Pen(Brushes.White, 1.5), new Point(X(top.DistanceNm), Y(top.ElevationFt)), 4, 4);
            var label = Text($"{top.ElevationFt.ToString("N0", Fr)} ft", 10.5, Solid(150, 20, 20), bold: true);
            double lx = Math.Clamp(X(top.DistanceNm) - label.Width / 2, Left, w - Right - label.Width);
            dc.DrawText(label, new Point(lx, Math.Max(Top, Y(top.ElevationFt) - label.Height - 6)));
        }
    }
}
