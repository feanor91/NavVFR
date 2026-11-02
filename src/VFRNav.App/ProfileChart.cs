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
    private AltitudeProfile? _profile;

    // Glisser-déposer de l'altitude : un point de la route, ou le trait de croisière entier.
    private enum DragKind { None, Waypoint, Cruise }
    private DragKind _drag;
    private int _dragIndex;
    private AltitudeProfile? _dragProfile;
    private double? _dragCruise, _frozenYMax;
    private double _gTotal = 1, _gPlotW = 1, _gPlotH = 1, _gYMax = 1;
    private const double MarkerHitPx = 11, LineHitPx = 6, SnapFt = 100;

    /// <summary>Altitude modifiée à la souris : l'indice du point (null : la croisière de toute la route) et la valeur en pieds (null : retour à la croisière).</summary>
    public event Action<int?, double?>? AltitudeEdited;

    /// <summary>Un obstacle est tracé quand son sommet est à moins de 1 500 ft sous la croisière (ou au-dessus).</summary>
    public const double ObstacleRelevantMarginFt = 1500;

    public void SetData(TerrainProfile? terrain, double cruiseFt, IReadOnlyList<Waypoint> route, IReadOnlyList<AirspaceCrossing> crossings, string? message = null, IReadOnlyList<RouteObstacle>? obstacles = null, AltitudeProfile? altitudes = null)
    {
        _obstacles = obstacles ?? [];
        _profile = altitudes ?? new AltitudeProfile(route, cruiseFt);
        if (_drag == DragKind.None) { _dragProfile = null; _dragCruise = null; _frozenYMax = null; }
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
        var prof = _dragProfile ?? _profile;
        double cruise = _dragCruise ?? _cruiseFt;
        bool sloped = prof is { IsFlat: false };
        double flightTop = prof?.MaxFt ?? cruise;
        double AltAt(double nm) => prof?.AtDistance(nm) ?? cruise;
        double zoneTop = _crossings.Select(c => Math.Min(c.Zone.UpperFt, flightTop + 2500)).DefaultIfEmpty(0).Max();
        var shownObstacles = _obstacles.Where(o => o.TopFt is not null && AltAt(o.AlongNm) - o.TopFt < ObstacleRelevantMarginFt).ToList();
        double obstacleTop = shownObstacles.Select(o => o.TopFt!.Value + 300).DefaultIfEmpty(0).Max();
        double yMax = _frozenYMax ?? Math.Ceiling(Math.Max(Math.Max(Math.Max(_terrain.MaxFt + 800, flightTop + 1500), zoneTop), obstacleTop) / 1000) * 1000;
        (_gTotal, _gPlotW, _gPlotH, _gYMax) = (total, plotW, plotH, yMax); // pour retrouver l'altitude sous la souris
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
            if (Math.Max(a.ElevationFt, b.ElevationFt) > AltAt((a.DistanceNm + b.DistanceNm) / 2) - 1000)
                dc.DrawLine(danger, new Point(X(a.DistanceNm), Y(a.ElevationFt)), new Point(X(b.DistanceNm), Y(b.ElevationFt)));
        }

        // Obstacles proches de la croisière : trait du sol au sommet, rouge si la marge est inférieure à 500 ft.
        foreach (var o in shownObstacles)
        {
            double margin = AltAt(o.AlongNm) - o.TopFt!.Value;
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
        var cruisePen = new Pen(Solid(210, 20, 120), _drag == DragKind.Cruise ? 3.5 : 2) { DashStyle = DashStyles.Dash };
        var pink = Solid(210, 20, 120);
        if (!sloped)
        {
            dc.DrawLine(cruisePen, new Point(Left, Y(cruise)), new Point(w - Right, Y(cruise)));
            var cruiseLabel = Text($"Croisière {cruise.ToString("N0", Fr)} ft", 10.5, pink, bold: true);
            dc.DrawText(cruiseLabel, new Point(w - Right - cruiseLabel.Width - 4, Y(cruise) - cruiseLabel.Height - 1));
        }
        else if (prof is not null)
        {
            // Altitudes différentes selon les points : trait brisé qui passe par l'altitude de chaque point, repérée par une valeur.
            for (int i = 1; i < prof.Count; i++)
                dc.DrawLine(cruisePen, new Point(X(prof.DistanceAt(i - 1)), Y(prof.AtWaypoint(i - 1))), new Point(X(prof.DistanceAt(i)), Y(prof.AtWaypoint(i))));
            for (int i = 0; i < prof.Count; i++)
            {
                var label = Text(prof.AtWaypoint(i).ToString("N0", Fr), 10, pink, bold: true);
                var at = new Point(X(prof.DistanceAt(i)), Y(prof.AtWaypoint(i)));
                dc.DrawText(label, new Point(Math.Clamp(at.X - label.Width / 2, Left, w - Right - label.Width), Math.Max(Top, at.Y - label.Height - 8)));
            }
        }
        // Poignées : chaque point de la route peut être attrapé et monté ou descendu.
        if (prof is not null)
            for (int i = 0; i < prof.Count; i++)
            {
                bool held = _drag == DragKind.Waypoint && _dragIndex == i;
                bool entered = _route[i].AltitudeFt is not null || held;
                var at = new Point(X(prof.DistanceAt(i)), Y(prof.AtWaypoint(i)));
                dc.DrawEllipse(entered ? pink : Brushes.White, new Pen(entered ? Brushes.White : pink, 1.6), at, held ? 6 : 4.5, held ? 6 : 4.5);
            }
        if (_drag != DragKind.None)
        {
            var hint = Text("Relâche pour fixer l'altitude (arrondie à 100 ft)", 10, Solid(110, 120, 140));
            dc.DrawText(hint, new Point(Left + 6, h - Bottom - hint.Height - 4));
        }

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

    // ---------- glisser-déposer de l'altitude ----------

    private double YOf(double ft) => Top + _gPlotH - Math.Clamp(ft, 0, _gYMax) / _gYMax * _gPlotH;
    private double XOf(double nm) => Left + nm / _gTotal * _gPlotW;
    private double FtAt(double y) => Math.Clamp((Top + _gPlotH - y) / _gPlotH * _gYMax, 0, _gYMax);

    /// <summary>Point de la route dont la poignée est sous la souris (le plus proche), ou null.</summary>
    private int? MarkerAt(Point p)
    {
        var prof = _dragProfile ?? _profile;
        if (prof is null) return null;
        int? best = null;
        double bestDistance = MarkerHitPx;
        for (int i = 0; i < prof.Count; i++)
        {
            double d = Math.Sqrt(Math.Pow(p.X - XOf(prof.DistanceAt(i)), 2) + Math.Pow(p.Y - YOf(prof.AtWaypoint(i)), 2));
            if (d <= bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>Vrai si la souris est sur le trait de croisière (quand l'altitude est la même partout).</summary>
    private bool OnCruiseLine(Point p)
    {
        var prof = _dragProfile ?? _profile;
        return prof is { IsFlat: true } && p.X >= Left && p.X <= Left + _gPlotW && Math.Abs(p.Y - YOf(_dragCruise ?? _cruiseFt)) <= LineHitPx;
    }

    private bool Interactive => _terrain is { Samples.Count: >= 2 } && _profile is not null && _route.Count >= 2;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Interactive) return;
        var p = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            bool over = MarkerAt(p) is not null || OnCruiseLine(p);
            Cursor = over ? System.Windows.Input.Cursors.SizeNS : null;
            ToolTip = over ? "Glisse vers le haut ou le bas pour changer l'altitude. Un point : double-clic pour revenir à la croisière." : null;
            return;
        }

        double ft = Math.Round(FtAt(p.Y) / SnapFt) * SnapFt;
        if (_drag == DragKind.Cruise) _dragCruise = ft;
        else _dragProfile = WithAltitude(_dragIndex, ft);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;
        var p = e.GetPosition(this);
        if (MarkerAt(p) is int index)
        {
            if (e.ClickCount >= 2) { AltitudeEdited?.Invoke(index, null); e.Handled = true; return; }
            _drag = DragKind.Waypoint;
            _dragIndex = index;
        }
        else if (OnCruiseLine(p)) _drag = DragKind.Cruise;
        else return;

        _frozenYMax = _gYMax; // l'échelle ne bouge pas pendant qu'on déplace
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragKind.None) return;
        var kind = _drag;
        int index = _dragIndex;
        double? ft = kind == DragKind.Cruise ? _dragCruise : _dragProfile?.AtWaypoint(index);
        _drag = DragKind.None;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (ft is { } value) AltitudeEdited?.Invoke(kind == DragKind.Waypoint ? index : null, value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragKind.None) return;
        _drag = DragKind.None; // capture perdue sans relâchement : on annule
        _dragProfile = null; _dragCruise = null; _frozenYMax = null;
        InvalidateVisual();
    }

    /// <summary>Profil d'altitude où un seul point a la valeur donnée, pour l'aperçu pendant le glissement.</summary>
    private AltitudeProfile WithAltitude(int index, double ft) =>
        new([.. _route.Select((w, i) => i == index ? new Waypoint { Name = w.Name, Lat = w.Lat, Lon = w.Lon, AltitudeFt = ft } : w)], _cruiseFt);
}
