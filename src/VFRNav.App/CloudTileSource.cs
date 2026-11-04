using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using SkiaSharp;
using VFRNav.Core.Weather;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>
/// Tuiles d'une couche de nuages : l'image du satellite (service WMS d'EUMETSAT, sans clé) est demandée pour l'emprise exacte de chaque tuile,
/// puis transformée en calque transparent teinté (voir <see cref="SatelliteClouds.ToOverlay"/>) pour laisser voir la carte dessous.
/// La couverture (masque de nuages) et les sommets froids (infrarouge) sont deux sources séparées.
/// <para>
/// Le service WMS met de 0,3 à 2 s par requête : ce qui ralentit, c'est le nombre de requêtes. D'où les tuiles de 512 px, un niveau maximal
/// raisonnable et un cache (mémoire puis disque) des tuiles transformées, rangées par créneau de 15 minutes.
/// </para>
/// </summary>
public sealed class CloudTileSource : HttpTileSource
{
    /// <summary>
    /// Tuiles de 512 px : quatre fois moins de requêtes qu'en 256 px pour la même résolution, et le service met à peu près autant de temps
    /// pour une image que pour une autre. Le niveau maximal 6 correspond au niveau 7 en 256 px, soit environ 1 km par pixel :
    /// c'est déjà plus fin que les satellites (2 à 3 km).
    /// </summary>
    public const int TileSize = 512;
    public const int MaxZoom = 6;

    private static readonly CloudTileCache DiskCache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFRNav", "cache", "clouds"));

    // Tuiles transformées gardées en mémoire, partagées par toutes les sources : un retour sur une zone vue ne relit ni le disque ni le réseau.
    private static readonly ConcurrentDictionary<string, byte[]> Memory = new();
    private const int MaxMemoryTiles = 400;

    private readonly CloudLayerKind _kind;

    /// <summary>Créneau de 15 minutes de cette source : une source recréée dans le même créneau retrouve ses tuiles.</summary>
    public long Slot { get; }

    public CloudTileSource(CloudLayerKind kind) : base(
        Schema(Loc.Get("CloudTileSource_Nuages")),
        new WmsUrlBuilder(SatelliteClouds.LayerFor(kind)),
        name: kind == CloudLayerKind.Cover ? Loc.Get("CloudTileSource_Couverture_nuageuse_EUMETSAT") : Loc.Get("CloudTileSource_Sommets_de_nuages_EUMETSAT"),
        persistentCache: null,
        attribution: new Attribution(SatelliteClouds.AttributionText, SatelliteClouds.AttributionUrl),
        configureHttpRequestMessage: null)
    {
        _kind = kind;
        Slot = CloudTileCache.SlotOf(DateTimeOffset.UtcNow);
        DiskCache.PruneOlderThan(Slot);
    }

    /// <summary>Pyramide Web Mercator de tuiles de 512 px (le niveau 0 est une seule tuile : le monde entier).</summary>
    private static TileSchema Schema(string name)
    {
        const double half = 20037508.342789244;
        var schema = new TileSchema
        {
            Name = name,
            Format = "png",
            Srs = "EPSG:3857",
            YAxis = YAxis.OSM,
            Extent = new Extent(-half, -half, half, half),
            OriginX = -half,
            OriginY = half,
        };
        for (int level = 0; level <= MaxZoom; level++)
            schema.Resolutions[level] = new Resolution(level, 2 * half / TileSize / Math.Pow(2, level), TileSize, TileSize, -half, half, 1 << level, 1 << level);
        return schema;
    }

    /// <summary>Créneau courant : une couche affichée est à renouveler quand celui-ci a changé (le satellite a publié une nouvelle image).</summary>
    public static long CurrentSlot => CloudTileCache.SlotOf(DateTimeOffset.UtcNow);

    public override async Task<byte[]?> GetTileAsync(HttpClient httpClient, TileInfo tileInfo, CancellationToken? cancellationToken = null)
    {
        var index = tileInfo.Index;
        string layer = SatelliteClouds.LayerFor(_kind);
        string key = $"{layer}/{Slot}/{index.Level}/{index.Col}/{index.Row}";

        if (Memory.TryGetValue(key, out var cached)) return cached;
        if (await Task.Run(() => DiskCache.Get(layer, Slot, index.Level, index.Col, index.Row)) is { } onDisk)
        {
            Remember(key, onDisk);
            return onDisk;
        }

        byte[]? png = await base.GetTileAsync(httpClient, tileInfo, cancellationToken);
        if (png is null) return null;
        byte[] overlay = await Task.Run(() => ToOverlay(png));
        Remember(key, overlay);
        DiskCache.Put(layer, Slot, index.Level, index.Col, index.Row, overlay);
        return overlay;
    }

    private static void Remember(string key, byte[] tile)
    {
        if (Memory.Count >= MaxMemoryTiles) Memory.Clear();
        Memory[key] = tile;
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
            new(SatelliteClouds.GetMapUrl(layer, info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY, TileSize));
    }
}
