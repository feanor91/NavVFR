using System.Net.Http;
using System.Runtime.InteropServices;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using SkiaSharp;
using VFRNav.Core.Weather;

namespace VFRNav.App;

/// <summary>
/// Tuiles de nuages : le masque de nuages et l'image infrarouge du satellite (service WMS d'EUMETSAT, sans clé) sont demandés pour l'emprise exacte de chaque tuile,
/// puis fusionnés en un calque transparent (voir <see cref="SatelliteClouds.ToCloudOverlay(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/>) pour laisser voir la carte dessous.
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
        // Infrarouge et masque de nuages sont demandés ensemble pour la même emprise.
        var infraredTask = base.GetTileAsync(httpClient, tileInfo, cancellationToken);
        var maskTask = FetchMaskAsync(httpClient, tileInfo, cancellationToken);
        byte[]? infrared = await infraredTask;
        byte[]? mask = await maskTask;
        if (infrared is null) return null;
        return await Task.Run(() => Merge(infrared, mask));
    }

    /// <summary>Le masque est un plus : s'il ne répond pas, la tuile garde l'infrarouge seul au lieu de disparaître.</summary>
    private static async Task<byte[]?> FetchMaskAsync(HttpClient httpClient, TileInfo tile, CancellationToken? ct)
    {
        var e = tile.Extent;
        try { return await httpClient.GetByteArrayAsync(SatelliteClouds.GetMapUrl(SatelliteClouds.MaskLayer, e.MinX, e.MinY, e.MaxX, e.MaxY), ct ?? CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static byte[] Merge(byte[] infraredPng, byte[]? maskPng)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(infraredPng);
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var infrared = SKBitmap.Decode(infraredPng, info);
            if (infrared is null) return infraredPng;

            var infraredPixels = infrared.GetPixelSpan();
            var result = new byte[infraredPixels.Length];

            using var mask = maskPng is null ? null : SKBitmap.Decode(maskPng, info);
            if (mask is not null && mask.GetPixelSpan().Length == infraredPixels.Length)
                SatelliteClouds.ToCloudOverlay(infraredPixels, mask.GetPixelSpan(), result);
            else
                SatelliteClouds.ToCloudOverlay(infraredPixels, result);   // pas de masque : infrarouge seul

            using var target = new SKBitmap(info);
            Marshal.Copy(result, 0, target.GetPixels(), result.Length);
            using var image = SKImage.FromBitmap(target);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return infraredPng; // image illisible : on la laisse telle quelle plutôt que de perdre la tuile
        }
    }

    private sealed class WmsUrlBuilder : IUrlBuilder
    {
        public Uri GetUrl(TileInfo info) =>
            new(SatelliteClouds.GetMapUrl(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY));
    }
}
