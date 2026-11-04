using System.Net.Http;
using System.Runtime.InteropServices;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using SkiaSharp;
using VFRNav.Core.Weather;

namespace VFRNav.App;

/// <summary>
/// Tuiles d'une couche de nuages : l'image du satellite (service WMS d'EUMETSAT, sans clé) est demandée pour l'emprise exacte de chaque tuile,
/// puis transformée en calque transparent teinté (voir <see cref="SatelliteClouds.ToOverlay"/>) pour laisser voir la carte dessous.
/// La couverture (masque de nuages) et les sommets froids (infrarouge) sont deux sources séparées.
/// </summary>
public sealed class CloudTileSource : HttpTileSource
{
    private readonly CloudLayerKind _kind;

    public CloudTileSource(CloudLayerKind kind) : base(
        new GlobalSphericalMercator(0, SatelliteClouds.MaxZoom, "Nuages"),
        new WmsUrlBuilder(SatelliteClouds.LayerFor(kind)),
        name: kind == CloudLayerKind.Cover ? "Couverture nuageuse EUMETSAT" : "Sommets de nuages EUMETSAT",
        persistentCache: null,
        attribution: new Attribution(SatelliteClouds.AttributionText, SatelliteClouds.AttributionUrl),
        configureHttpRequestMessage: null)
    {
        _kind = kind;
    }

    public override async Task<byte[]?> GetTileAsync(HttpClient httpClient, TileInfo tileInfo, CancellationToken? cancellationToken = null)
    {
        byte[]? png = await base.GetTileAsync(httpClient, tileInfo, cancellationToken);
        return png is null ? null : await Task.Run(() => ToOverlay(png));
    }

    private byte[] ToOverlay(byte[] png)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(png);
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var source = SKBitmap.Decode(png, info);
            if (source is null) return png;

            var result = new byte[source.GetPixelSpan().Length];
            SatelliteClouds.ToOverlay(_kind, source.GetPixelSpan(), result);

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

    private sealed class WmsUrlBuilder(string layer) : IUrlBuilder
    {
        public Uri GetUrl(TileInfo info) =>
            new(SatelliteClouds.GetMapUrl(layer, info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY));
    }
}
