using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Profil vertical de la route : relief, altitude de croisière, zones traversées et points de passage.</summary>
public sealed class ProfileChart : FrameworkElement
{
    private static CultureInfo Fr => VFRNav.Core.Localization.Loc.FormatCulture;
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
    private const double MarkerHitPx = 11, LineHitPx = 6, SnapFt = 100;

    // Zoom : la partie visible du profil, en distance (NM) et en altitude (ft). Null : tout.
    private double? _viewX0, _viewX1, _viewY0, _viewY1;
    private double _viewTotal;
    private double _autoYMax = 1;
    private const double MinSpanNm = 1, MinSpanFt = 300, ZoomStep = 1.3;

    // Ce que la dernière image a dessiné, pour retrouver la distance et l'altitude sous la souris.
    private double _gPlotW = 1, _gPlotH = 1, _gX0, _gX1 = 1, _gY0, _gY1 = 1, _gTotal = 1;

    // Déplacement du profil zoomé à la souris.
    private bool _panning;
    private Point _panStart;
    private (double X0, double X1, double Y0, double Y1) _panView;

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

        // Un zoom survit aux recalculs (changer une altitude, par exemple) mais pas à une route plus longue ou plus courte.
        if (terrain is { Samples.Count: >= 2 } && Math.Abs(terrain.TotalNm - _viewTotal) > 0.5)
        {
            ResetZoom();
            _viewTotal = terrain.TotalNm;
        }
        InvalidateVisual();
    }

    /// <summary>Revient à la vue d'ensemble de la route.</summary>
    public void ResetZoom()
    {
        _viewX0 = _viewX1 = _viewY0 = _viewY1 = null;
        InvalidateVisual();
    }

    private bool Zoomed => _viewX0 is not null || _viewY0 is not null;

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

    /// <summary>Pas de la grille d'altitude, adapté à la hauteur visible.</summary>
    private static double AltitudeStep(double span) => span switch
    {
        <= 600 => 100,
        <= 1500 => 250,
        <= 3000 => 500,
        <= 6000 => 1000,
        <= 12000 => 2000,
        _ => 5000
    };

    /// <summary>Pas de la grille de distance, adapté à la longueur visible.</summary>
    private static double DistanceStep(double span) => span switch
    {
        <= 4 => 0.5,
        <= 10 => 1,
        <= 20 => 2,
        <= 40 => 5,
        <= 120 => 10,
        <= 300 => 25,
        _ => 50
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w < 80 || h < 60) return;
        dc.DrawRectangle(Brushes.White, new Pen(Solid(180, 190, 205), 1), new Rect(0, 0, w, h));

        if (_terrain is null || _terrain.Samples.Count < 2)
        {
            dc.DrawText(Text(_message ?? Loc.Get("ProfileChart_Aucun_profil_calcule_Pose_une"), 13, Solid(90, 100, 120)), new Point(16, h / 2 - 10));
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
        _autoYMax = yMax;

        // Partie visible : tout, ou la fenêtre de zoom ramenée dans les limites de la route et de l'échelle.
        double vx0 = 0, vx1 = total, vy0 = 0, vy1 = yMax;
        if (_viewX0 is { } ax && _viewX1 is { } bx && bx - ax < total - 1e-6)
        {
            vx1 = Math.Clamp(bx, MinSpanNm, total);
            vx0 = Math.Clamp(ax, 0, vx1 - MinSpanNm);
        }
        if (_viewY0 is { } ay && _viewY1 is { } by && by - ay < yMax - 1e-6)
        {
            vy1 = Math.Clamp(by, MinSpanFt, yMax);
            vy0 = Math.Clamp(ay, 0, vy1 - MinSpanFt);
        }
        (_gPlotW, _gPlotH, _gX0, _gX1, _gY0, _gY1, _gTotal) = (plotW, plotH, vx0, vx1, vy0, vy1, total);

        double X(double nm) => Left + (nm - vx0) / (vx1 - vx0) * plotW;
        double Y(double ft) => Top + plotH - (ft - vy0) / (vy1 - vy0) * plotH;

        var gridPen = new Pen(Solid(225, 230, 238), 1);
        var gridText = Solid(110, 120, 140);
        double gridStep = AltitudeStep(vy1 - vy0);
        for (double ft = Math.Ceiling(vy0 / gridStep) * gridStep; ft <= vy1 + 1e-6; ft += gridStep)
        {
            dc.DrawLine(gridPen, new Point(Left, Y(ft)), new Point(w - Right, Y(ft)));
            var label = Text($"{ft.ToString("N0", Fr)} ft", 10, gridText);
            dc.DrawText(label, new Point(Left - label.Width - 6, Y(ft) - label.Height / 2));
        }
        double nmStep = DistanceStep(vx1 - vx0);
        for (double nm = Math.Ceiling(vx0 / nmStep) * nmStep; nm <= vx1 + 1e-6; nm += nmStep)
        {
            dc.DrawLine(gridPen, new Point(X(nm), Top), new Point(X(nm), h - Bottom));
            var label = Text($"{nm.ToString("0.#", Fr)} NM", 10, gridText);
            dc.DrawText(label, new Point(X(nm) - label.Width / 2, h - Bottom + 4));
        }

        // Tout ce qui suit est tracé dans le cadre du graphique : zoomé, le reste en déborderait sur les axes.
        dc.PushClip(new RectangleGeometry(new Rect(Left, Top, plotW, plotH)));

        // Zones traversées : bandes entre leur plancher et leur plafond.
        var labels = new List<(AirspaceCrossing Crossing, double X0, double X1, double YTop, double YBottom)>();
        foreach (var c in _crossings)
        {
            var (r, g, b) = ZoneColor(c.Zone.Kind);
            double x0 = X(c.EntryNm), x1 = Math.Max(X(c.ExitNm), x0 + 2);
            if (x1 < Left || x0 > Left + plotW) continue; // hors de la partie visible
            double yTop = Y(Math.Min(c.Zone.UpperFt, Math.Max(yMax, vy1))), yBottom = Y(Math.Max(c.Zone.LowerFt, 0));
            if (yBottom - yTop < 1) continue;
            dc.DrawRectangle(Solid(r, g, b, 30), new Pen(Solid(r, g, b, 190), 1), new Rect(x0, yTop, x1 - x0, yBottom - yTop));
            // Le nom se place dans la partie visible de la bande.
            labels.Add((c, Math.Max(x0, Left), Math.Min(x1, Left + plotW), Math.Max(yTop, Top), Math.Min(yBottom, Top + plotH)));
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
            var cruiseLabel = Text(Loc.F("ProfileChart_Croisiere_ft", cruise.ToString("N0", Fr)), 10.5, pink, bold: true);
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
            var hint = Text(Loc.Get("ProfileChart_Relache_pour_fixer_l_altitude"), 10, Solid(110, 120, 140));
            dc.DrawText(hint, new Point(Left + 6, h - Bottom - hint.Height - 4));
        }

        // Lignes pointillées des points de passage.
        double done = 0;
        var wpPen = new Pen(Solid(70, 80, 100), 1) { DashStyle = DashStyles.Dot };
        var wpX = new double[_route.Count];
        for (int i = 0; i < _route.Count; i++)
        {
            if (i > 0) done += NavMath.DistanceNm(_route[i - 1], _route[i]);
            wpX[i] = X(done);
            dc.DrawLine(wpPen, new Point(wpX[i], Top), new Point(wpX[i], h - Bottom));
        }

        // Point le plus haut.
        if (_terrain.Highest is { } top)
        {
            dc.DrawEllipse(Solid(215, 30, 30), new Pen(Brushes.White, 1.5), new Point(X(top.DistanceNm), Y(top.ElevationFt)), 4, 4);
            var label = Text($"{top.ElevationFt.ToString("N0", Fr)} ft", 10.5, Solid(150, 20, 20), bold: true);
            double lx = Math.Clamp(X(top.DistanceNm) - label.Width / 2, Left, w - Right - label.Width);
            dc.DrawText(label, new Point(lx, Math.Max(Top, Y(top.ElevationFt) - label.Height - 6)));
        }
        dc.Pop();

        // Noms des points de passage, au-dessus du cadre : seuls ceux de la partie visible.
        for (int i = 0; i < _route.Count; i++)
        {
            if (wpX[i] < Left - 1 || wpX[i] > Left + plotW + 1) continue;
            var label = Text(string.IsNullOrEmpty(_route[i].Icao) ? _route[i].Name : _route[i].Icao!, 10, Solid(30, 40, 70), bold: true);
            double lx = Math.Clamp(wpX[i] - label.Width / 2, 2, w - label.Width - 2);
            dc.DrawText(label, new Point(lx, 5));
        }

        // Rappel du zoom et du moyen de revenir à la vue d'ensemble.
        if (Zoomed)
        {
            var zoomNote = Text(Loc.F("ProfileChart_Zoom_sur_la_distance_sur", total / (vx1 - vx0), yMax / (vy1 - vy0)), 9.5, Solid(110, 120, 140));
            dc.DrawText(zoomNote, new Point(w - Right - zoomNote.Width - 4, h - 16));
        }
    }

    // ---------- coordonnées sous la souris ----------

    private double YOf(double ft) => Top + _gPlotH - (ft - _gY0) / (_gY1 - _gY0) * _gPlotH;
    private double XOf(double nm) => Left + (nm - _gX0) / (_gX1 - _gX0) * _gPlotW;
    private double FtAt(double y) => Math.Clamp(_gY0 + (Top + _gPlotH - y) / _gPlotH * (_gY1 - _gY0), _gY0, _gY1);
    private bool InPlot(Point p) => p.X >= Left && p.X <= Left + _gPlotW && p.Y >= Top && p.Y <= Top + _gPlotH;

    // ---------- glisser-déposer de l'altitude ----------

    /// <summary>Point de la route dont la poignée est sous la souris (le plus proche), ou null.</summary>
    private int? MarkerAt(Point p)
    {
        var prof = _dragProfile ?? _profile;
        if (prof is null || !InPlot(p)) return null;
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Interactive) return;
        var p = e.GetPosition(this);

        if (_panning)
        {
            Pan(p);
            return;
        }

        if (_drag == DragKind.None)
        {
            bool over = MarkerAt(p) is not null || OnCruiseLine(p);
            Cursor = over ? Cursors.SizeNS : Zoomed && InPlot(p) ? Cursors.SizeAll : null;
            ToolTip = over
                ? Loc.Get("ProfileChart_Glisse_vers_le_haut_ou")
                : InPlot(p) ? Loc.Get("ProfileChart_Molette_zoom_sur_la_distance") : null;
            return;
        }

        double ft = Math.Round(FtAt(p.Y) / SnapFt) * SnapFt;
        if (_drag == DragKind.Cruise) _dragCruise = ft;
        else _dragProfile = WithAltitude(_dragIndex, ft);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
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
        else
        {
            // Le fond du graphique : double-clic pour tout revoir, sinon on déplace la partie visible (si elle est zoomée).
            if (!InPlot(p)) return;
            if (e.ClickCount >= 2) { ResetZoom(); e.Handled = true; return; }
            if (!Zoomed) return;
            _panning = true;
            _panStart = p;
            _panView = (_gX0, _gX1, _gY0, _gY1);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        _frozenYMax = _autoYMax; // l'échelle ne bouge pas pendant qu'on déplace
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
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

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = false;
        if (_drag == DragKind.None) return;
        _drag = DragKind.None; // capture perdue sans relâchement : on annule
        _dragProfile = null; _dragCruise = null; _frozenYMax = null;
        InvalidateVisual();
    }

    /// <summary>Profil d'altitude où un seul point a la valeur donnée, pour l'aperçu pendant le glissement.</summary>
    private AltitudeProfile WithAltitude(int index, double ft) =>
        new([.. _route.Select((w, i) => i == index ? new Waypoint { Name = w.Name, Lat = w.Lat, Lon = w.Lon, AltitudeFt = ft } : w)], _cruiseFt,
            _profile?.ClimbFtPerNm ?? AltitudeProfile.GradientFtPerNm, _profile?.DescentFtPerNm ?? AltitudeProfile.GradientFtPerNm);

    // ---------- zoom et déplacement ----------

    /// <summary>Molette : zoom sur la distance autour du curseur ; avec Ctrl, zoom sur l'altitude.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Interactive || _drag != DragKind.None || _panning) return;
        var p = e.GetPosition(this);
        if (!InPlot(p)) return;

        double factor = e.Delta > 0 ? 1 / ZoomStep : ZoomStep;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomAltitude(factor, p);
        else ZoomDistance(factor, p);
        e.Handled = true;
        InvalidateVisual();
    }

    private void ZoomDistance(double factor, Point p)
    {
        double span = _gX1 - _gX0;
        double newSpan = Math.Clamp(span * factor, Math.Min(MinSpanNm, _gTotal), _gTotal);
        double fraction = Math.Clamp((p.X - Left) / _gPlotW, 0, 1);
        double anchor = _gX0 + fraction * span;
        double x0 = Math.Clamp(anchor - fraction * newSpan, 0, _gTotal - newSpan);
        if (newSpan >= _gTotal - 1e-6) { _viewX0 = _viewX1 = null; return; }
        (_viewX0, _viewX1) = (x0, x0 + newSpan);
    }

    private void ZoomAltitude(double factor, Point p)
    {
        double span = _gY1 - _gY0;
        double newSpan = Math.Clamp(span * factor, Math.Min(MinSpanFt, _autoYMax), _autoYMax);
        double fraction = Math.Clamp((Top + _gPlotH - p.Y) / _gPlotH, 0, 1); // depuis le bas du cadre
        double anchor = _gY0 + fraction * span;
        double y0 = Math.Clamp(anchor - fraction * newSpan, 0, _autoYMax - newSpan);
        if (newSpan >= _autoYMax - 1e-6) { _viewY0 = _viewY1 = null; return; }
        (_viewY0, _viewY1) = (y0, y0 + newSpan);
    }

    /// <summary>Déplace la partie visible : le profil suit la souris.</summary>
    private void Pan(Point p)
    {
        double spanX = _panView.X1 - _panView.X0, spanY = _panView.Y1 - _panView.Y0;
        if (_viewX0 is not null)
        {
            double x0 = Math.Clamp(_panView.X0 - (p.X - _panStart.X) / _gPlotW * spanX, 0, _gTotal - spanX);
            (_viewX0, _viewX1) = (x0, x0 + spanX);
        }
        if (_viewY0 is not null)
        {
            double y0 = Math.Clamp(_panView.Y0 + (p.Y - _panStart.Y) / _gPlotH * spanY, 0, _autoYMax - spanY);
            (_viewY0, _viewY1) = (y0, y0 + spanY);
        }
        InvalidateVisual();
    }
}
