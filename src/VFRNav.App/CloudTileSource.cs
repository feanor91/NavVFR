using System.Net.Http;
using System.Runtime.InteropServices;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using SkiaSharp;
using VFRNav.Core.Weather;

namespace VFRNav.App;

/// <summary>
/// Tuiles de nuages : l'image infrarouge du satellite (service WMS d'EUMETSAT, sans clé) est demandée pour l'emprise exacte de chaque tuile,
/// puis ses niveaux de gris deviennent de la transparence (voir <see cref="SatelliteClouds.ToCloudOverlay"/>) pour laisser voir la carte dessous.
/// </summary>
public sealed class CloudTileSource : HttpTileSource
{
    public CloudTileSource() : base(
        new GlobalSphericalMercator(0, SatelliteClouds.MaxZoom, "Nuages"),
        new WmsUrlBuilder(),
        name: "Nuages EUMETSAT",
        persistentCache: null,
        attribution: new Attribution(SatelliteClouds.AttributionText, SatelliteClouds.AttributionUrl),
        configureHttpRequestMessage: null)
    {
    }

    public override async Task<byte[]?> GetTileAsync(HttpClient httpClient, TileInfo tileInfo, CancellationToken? cancellationToken = null)
    {
        byte[]? png = await base.GetTileAsync(httpClient, tileInfo, cancellationToken);
        return png is null ? null : await Task.Run(() => ToOverlay(png));
    }

    private static byte[] ToOverlay(byte[] png)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(png);
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var source = SKBitmap.Decode(png, info);
            if (source is null) return png;

            var pixels = source.GetPixelSpan();
            var result = new byte[pixels.Length];
            SatelliteClouds.ToCloudOverlay(pixels, result);

            using var target = new SKBitmap(info);
            Marshal.Copy(result, 0, target.GetPixels(), result.Length);
            using var image = SKImage.FromBitmap(target);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return png; // image illisible : on la laisse telle quelle plutôt que de perdre la tuile
        }
    }

    private sealed class WmsUrlBuilder : IUrlBuilder
    {
        public Uri GetUrl(TileInfo info) =>
            new(SatelliteClouds.GetMapUrl(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY));
    }
}
