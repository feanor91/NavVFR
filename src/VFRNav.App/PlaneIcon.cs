namespace VFRNav.App;

/// <summary>Silhouette d'avion vue de dessus, nez vers le haut : la carte la fait tourner selon le cap.</summary>
public static class PlaneIcon
{
    public static string Source { get; } = Render();

    private static string Render()
    {
        const int size = 96;
        using var bitmap = new SkiaSharp.SKBitmap(size, size);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            // Demi-contour (côté droit) en coordonnées 0..96, puis miroir pour le côté gauche.
            (float X, float Y)[] right =
            [
                (48, 4), (52, 12), (53, 34), (92, 58), (92, 66), (53, 56), (52, 78), (66, 88), (66, 94), (48, 90)
            ];
            using var path = new SkiaSharp.SKPath();
            path.MoveTo(right[0].X, right[0].Y);
            foreach (var (x, y) in right.Skip(1)) path.LineTo(x, y);
            foreach (var (x, y) in right.Skip(1).Reverse()) path.LineTo(size - x, y);
            path.Close();
            using var outline = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.StrokeAndFill, StrokeWidth = 8, StrokeJoin = SkiaSharp.SKStrokeJoin.Round, Color = SkiaSharp.SKColors.White };
            using var fill = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Fill, Color = new SkiaSharp.SKColor(230, 0, 160) };
            canvas.DrawPath(path, outline);
            canvas.DrawPath(path, fill);
        }
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return "base64-content://" + Convert.ToBase64String(data.ToArray());
    }
}
