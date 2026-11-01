using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia.SkiaStyles;
using Mapsui.Styles;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace VFRNav.App;

/// <summary>Contour d'espace aérien : trait (plein ou tireté), bande intérieure et nom écrit le long du trait.</summary>
public sealed class AirspaceStyle : VectorStyle
{
    public required Color Stroke { get; init; }
    public double Width { get; init; } = 1.5;
    public bool Dashed { get; init; }
    /// <summary>Bande intérieure hachurée (zones réglementées) au lieu d'une bande unie légère.</summary>
    public bool Hatched { get; init; }
    public string Label { get; init; } = "";
    public string ShortLabel { get; init; } = "";
}

/// <summary>Dessine un <see cref="AirspaceStyle"/> avec Skia, le texte suivant les segments du contour comme sur OpenAIP.</summary>
public sealed class AirspaceRenderer : ISkiaStyleRenderer
{
    private const float FontSize = 11;
    private const float BandWidth = 7;
    private static readonly Dictionary<uint, SKShader> HatchShaders = [];

    // Étiquettes déjà posées pendant ce rendu : une étiquette qui en recouvrirait une autre est omise.
    private static readonly List<SKRect> PlacedLabels = [];
    private static readonly HashSet<IFeature> LabelledFeatures = [];
    private static long _labelIteration = -1;

    public bool Draw(SKCanvas canvas, Viewport viewport, ILayer layer, IFeature feature, IStyle style, RenderService renderService, long iteration)
    {
        if (style is not AirspaceStyle s || feature is not GeometryFeature { Geometry: LineString line } || line.NumPoints < 3) return false;

        var pts = new SKPoint[line.NumPoints];
        for (int i = 0; i < pts.Length; i++)
        {
            var c = line.GetCoordinateN(i);
            var sp = viewport.WorldToScreen(new MPoint(c.X, c.Y));
            pts[i] = new SKPoint((float)sp.X, (float)sp.Y);
        }

        using var path = new SKPath();
        path.MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++) path.LineTo(pts[i]);
        path.Close();

        var bounds = path.Bounds;
        var view = canvas.LocalClipBounds;
        if (!bounds.IntersectsWith(view) || (bounds.Width < 3 && bounds.Height < 3)) return true;

        var color = new SKColor((byte)s.Stroke.R, (byte)s.Stroke.G, (byte)s.Stroke.B);

        // Bande intérieure : donne le sens de la zone sans cacher le fond de carte.
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, true);
        using (var band = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = BandWidth * 2 })
        {
            if (s.Hatched) band.Shader = Hatch(color);
            else band.Color = color.WithAlpha(40);
            canvas.DrawPath(path, band);
        }
        canvas.Restore();

        using (var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = (float)s.Width })
        {
            if (s.Dashed) outline.PathEffect = SKPathEffect.CreateDash([9f, 5f], 0);
            canvas.DrawPath(path, outline);
        }

        if (s.Label.Length > 0 && viewport.Resolution < 400) DrawLabel(canvas, path, pts, view, s, color, feature, iteration);
        return true;
    }

    private static SKShader Hatch(SKColor color)
    {
        lock (HatchShaders)
        {
            if (HatchShaders.TryGetValue((uint)color, out var cached)) return cached;
            using var bitmap = new SKBitmap(8, 8);
            using (var c = new SKCanvas(bitmap))
            {
                c.Clear(SKColors.Transparent);
                using var p = new SKPaint { IsAntialias = true, Color = color.WithAlpha(150), StrokeWidth = 1.6f, Style = SKPaintStyle.Stroke };
                c.DrawLine(-1, 9, 9, -1, p);
            }
            var shader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            HatchShaders[(uint)color] = shader;
            return shader;
        }
    }

    /// <summary>Écrit le texte sur le plus long segment visible, du côté intérieur de la zone.</summary>
    private static void DrawLabel(SKCanvas canvas, SKPath polygon, SKPoint[] pts, SKRect view, AirspaceStyle s, SKColor color, IFeature feature, long iteration)
    {
        using var font = new SKFont(SKTypeface.Default, FontSize);

        // Meilleur segment : le plus long une fois ramené à la partie visible de l'écran.
        var visible = SKRect.Inflate(view, -6, -6);
        float bestLength = 0;
        SKPoint bestA = default, bestB = default;
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            if (!ClipSegment(a, b, visible, out var ca, out var cb)) continue;
            float len = SKPoint.Distance(ca, cb);
            if (len > bestLength) { bestLength = len; bestA = ca; bestB = cb; }
        }
        if (bestLength < 28) return;

        string? text = null;
        float width = 0;
        foreach (string candidate in new[] { s.Label, s.ShortLabel })
        {
            if (candidate.Length == 0) continue;
            float w = font.MeasureText(candidate);
            if (w <= bestLength - 6) { text = candidate; width = w; break; }
        }
        if (text is null) return;

        // Le texte se lit de gauche à droite.
        if (bestB.X < bestA.X) (bestA, bestB) = (bestB, bestA);
        float dx = bestB.X - bestA.X, dy = bestB.Y - bestA.Y;
        var mid = new SKPoint((bestA.X + bestB.X) / 2, (bestA.Y + bestB.Y) / 2);
        var up = new SKPoint(dy / bestLength, -dx / bestLength);
        bool insideIsUp = polygon.Contains(mid.X + up.X * 6, mid.Y + up.Y * 6);
        float vOffset = insideIsUp ? -3 : FontSize + 1;

        // Un nouveau rendu commence quand l'itération change ou quand une zone est redessinée.
        lock (PlacedLabels)
        {
            if (iteration != _labelIteration || !LabelledFeatures.Add(feature))
            {
                PlacedLabels.Clear();
                LabelledFeatures.Clear();
                LabelledFeatures.Add(feature);
                _labelIteration = iteration;
            }
            float ux = dx / bestLength, uy = dy / bestLength;
            var from = new SKPoint(mid.X - ux * width / 2, mid.Y - uy * width / 2);
            var to = new SKPoint(mid.X + ux * width / 2, mid.Y + uy * width / 2);
            var box = new SKRect(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
            box.Inflate(FontSize * 0.7f, FontSize * 0.7f);
            if (PlacedLabels.Any(r => r.IntersectsWith(box))) return;
            PlacedLabels.Add(box);
        }

        using var segment = new SKPath();
        segment.MoveTo(bestA);
        segment.LineTo(bestB);
        float hOffset = (bestLength - width) / 2;

        using var halo = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill };
        canvas.DrawTextOnPath(text, segment, hOffset, vOffset, font, halo);
        canvas.DrawTextOnPath(text, segment, hOffset, vOffset, font, fill);
    }

    /// <summary>Liang-Barsky : partie du segment ab qui est dans le rectangle.</summary>
    private static bool ClipSegment(SKPoint a, SKPoint b, SKRect r, out SKPoint ca, out SKPoint cb)
    {
        float t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        ca = a; cb = b;
        float[] p = [-dx, dx, -dy, dy];
        float[] q = [a.X - r.Left, r.Right - a.X, a.Y - r.Top, r.Bottom - a.Y];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;
                continue;
            }
            float t = q[i] / p[i];
            if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        ca = new SKPoint(a.X + t0 * dx, a.Y + t0 * dy);
        cb = new SKPoint(a.X + t1 * dx, a.Y + t1 * dy);
        return true;
    }
}
