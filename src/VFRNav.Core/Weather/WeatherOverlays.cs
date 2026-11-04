using System.Globalization;
using System.Text.Json;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Weather;

/// <summary>Une image du radar : instant de la mesure et chemin des tuiles chez RainViewer.</summary>
public sealed record RadarFrame(DateTimeOffset Time, string Path);

/// <summary>
/// Index des images radar de RainViewer (api.rainviewer.com/public/weather-maps.json) : une image toutes les 10 minutes
/// sur les deux dernières heures, mosaïque mondiale. Gratuit pour un usage personnel, à condition d'afficher
/// « Weather data by RainViewer » avec un lien vers rainviewer.com.
/// </summary>
public sealed class RadarMaps
{
    public const string IndexUrl = "https://api.rainviewer.com/public/weather-maps.json";
    public static string AttributionText => "Weather data by RainViewer";
    public const string AttributionUrl = "https://www.rainviewer.com";

    /// <summary>Niveau de zoom maximal des tuiles (au-delà, l'image est agrandie).</summary>
    public const int MaxZoom = 7;

    private RadarMaps(string host, IReadOnlyList<RadarFrame> frames)
    {
        Host = host;
        Frames = frames;
    }

    public string Host { get; }

    /// <summary>Images passées, de la plus ancienne à la plus récente.</summary>
    public IReadOnlyList<RadarFrame> Frames { get; }

    public RadarFrame Latest => Frames[^1];

    /// <summary>Les <paramref name="count"/> images les plus récentes, de la plus ancienne à la plus récente (moins si le service en fournit moins).</summary>
    public IReadOnlyList<RadarFrame> LastFrames(int count) => Frames.Skip(Math.Max(0, Frames.Count - Math.Max(1, count))).ToList();

    public static RadarMaps Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string host = root.GetProperty("host").GetString() ?? "";
            var frames = new List<RadarFrame>();
            foreach (var f in root.GetProperty("radar").GetProperty("past").EnumerateArray())
                frames.Add(new RadarFrame(DateTimeOffset.FromUnixTimeSeconds(f.GetProperty("time").GetInt64()), f.GetProperty("path").GetString() ?? ""));

            if (!host.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidDataException(Loc.Get("WeatherOverlays_Adresse_des_tuiles_radar_inattendue"));
            if (frames.Count == 0 || frames.Any(f => f.Path.Length == 0)) throw new InvalidDataException(Loc.Get("WeatherOverlays_Aucune_image_radar_disponible"));
            frames.Sort((a, b) => a.Time.CompareTo(b.Time));
            return new RadarMaps(host.TrimEnd('/'), frames);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Loc.Get("WeatherOverlays_Reponse_de_RainViewer_illisible") + ex.Message, ex);
        }
    }

    /// <summary>Modèle d'adresse de tuile ({z}/{x}/{y}) pour une image : tuiles de 256 px, palette « universelle », contours lissés, neige distincte.</summary>
    public string TileUrlTemplate(RadarFrame frame) => $"{Host}{frame.Path}/256/{{z}}/{{x}}/{{y}}/2/1_1.png";

    /// <summary>Âge de l'image la plus récente.</summary>
    public TimeSpan Age(DateTimeOffset now) => now - Latest.Time;
}

/// <summary>Les deux couches de nuages du satellite, que l'on peut afficher séparément ou ensemble.</summary>
public enum CloudLayerKind
{
    /// <summary>Couverture nuageuse : où il y a des nuages, à tous les niveaux (stratus bas compris), jour et nuit. Gris-bleu.</summary>
    Cover,

    /// <summary>Sommets froids : nuages épais ou élevés (orages, front), d'après l'infrarouge. Violet.</summary>
    Tops,
}

/// <summary>
/// Nuages vus du satellite, via le service WMS gratuit d'EUMETSAT (EUMETView, sans clé), en deux couches indépendantes :
/// la <b>couverture</b> (masque de nuages de Meteosat : présence de nuages à tous les niveaux, y compris les stratus bas)
/// et les <b>sommets froids</b> (image infrarouge 10,5 µm de Meteosat Troisième Génération : nuages épais ou élevés).
/// Sans paramètre de date, le service renvoie l'image la plus récente (une toutes les 10 à 15 minutes, avec 10 à 20 minutes de décalage).
/// Le masque ne dit ni l'épaisseur ni la hauteur : « nuageux » ne veut pas dire « plafond bas ».
/// </summary>
public static class SatelliteClouds
{
    public const string WmsUrl = "https://view.eumetsat.int/geoserver/ows";

    /// <summary>Infrarouge 10,5 µm de Meteosat Troisième Génération : sommets de nuages froids.</summary>
    public const string Layer = "mtg_fd:ir105_hrfi";

    /// <summary>Masque de nuages de Meteosat Seconde Génération (nuage / sol dégagé / mer dégagée), une image toutes les 15 minutes.</summary>
    public const string MaskLayer = "msg_fes:clm";

    public static string AttributionText => Loc.Get("WeatherOverlays_Nuages_EUMETSAT_Meteosat");
    public const string AttributionUrl = "https://view.eumetsat.int";
    public const int MaxZoom = 8;

    public static string LayerFor(CloudLayerKind kind) => kind == CloudLayerKind.Cover ? MaskLayer : Layer;

    /// <summary>Requête GetMap pour une tuile de la couche infrarouge, en projection Web Mercator (EPSG:3857, mètres), fond transparent.</summary>
    public static string GetMapUrl(double minX, double minY, double maxX, double maxY, int size = 256) =>
        GetMapUrl(Layer, minX, minY, maxX, maxY, size);

    public static string GetMapUrl(string layer, double minX, double minY, double maxX, double maxY, int size = 256)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv,
            $"{WmsUrl}?service=WMS&version=1.3.0&request=GetMap&layers={Uri.EscapeDataString(layer)}&styles=&format=image/png&transparent=true" +
            $"&crs=EPSG:3857&bbox={minX:0.###},{minY:0.###},{maxX:0.###},{maxY:0.###}&width={size}&height={size}");
    }

    // Deux teintes bien distinctes : un gris-bleu pour la couverture, un violet pour les sommets froids.
    public static (byte R, byte G, byte B) Tint(CloudLayerKind kind) => kind == CloudLayerKind.Cover ? ((byte)112, (byte)128, (byte)150) : ((byte)116, (byte)62, (byte)196);

    public static string TintHex(CloudLayerKind kind)
    {
        var (r, g, b) = Tint(kind);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private const double DarkLimit = 105, BrightLimit = 215;

    /// <summary>Opacité maximale de chaque couche avant le curseur de l'utilisateur.</summary>
    public const double CoverAlpha = 0.85, TopsMaxAlpha = 0.9;

    /// <summary>
    /// Le masque de nuages est livré en couleurs : blanc = nuage, vert = terre dégagée, bleu = mer dégagée.
    /// Un point est nuageux s'il est (presque) blanc et opaque.
    /// </summary>
    public static bool IsCloudInMask(byte r, byte g, byte b, byte a) => a >= 128 && r >= 225 && g >= 225 && b >= 225;

    /// <summary>Opacité d'un point de l'image infrarouge (0 à 1) d'après sa luminosité : le sol chaud (sombre) disparaît, les nuages froids (clairs) restent.</summary>
    public static double CloudAlpha(double luminance)
    {
        double t = Math.Clamp((luminance - DarkLimit) / (BrightLimit - DarkLimit), 0, 1);
        return t * t * (3 - 2 * t) * TopsMaxAlpha;   // lissage : pas de bord dur entre nuage et ciel clair
    }

    /// <summary>
    /// Couche « couverture » : transforme le masque de nuages (couleurs opaques) en calque transparent, teinté là où il y a un nuage.
    /// Entrée et sortie : pixels RGBA de 4 octets, alpha non prémultiplié.
    /// </summary>
    public static void ToCoverOverlay(ReadOnlySpan<byte> maskRgba, Span<byte> output)
    {
        if (output.Length < maskRgba.Length) throw new ArgumentException(Loc.Get("WeatherOverlays_Tampon_de_sortie_trop_petit"), nameof(output));
        var (r, g, b) = Tint(CloudLayerKind.Cover);
        for (int i = 0; i + 3 < maskRgba.Length; i += 4)
        {
            bool cloud = IsCloudInMask(maskRgba[i], maskRgba[i + 1], maskRgba[i + 2], maskRgba[i + 3]);
            output[i] = r;
            output[i + 1] = g;
            output[i + 2] = b;
            output[i + 3] = cloud ? (byte)Math.Round(CoverAlpha * 255) : (byte)0;
        }
    }

    /// <summary>
    /// Couche « sommets froids » : transforme l'image infrarouge en niveaux de gris, opaque, en calque transparent où seuls les nuages froids (clairs) restent.
    /// Les points déjà transparents (hors du disque vu par le satellite) le restent.
    /// </summary>
    public static void ToTopsOverlay(ReadOnlySpan<byte> infraredRgba, Span<byte> output)
    {
        if (output.Length < infraredRgba.Length) throw new ArgumentException(Loc.Get("WeatherOverlays_Tampon_de_sortie_trop_petit"), nameof(output));
        var (r, g, b) = Tint(CloudLayerKind.Tops);
        for (int i = 0; i + 3 < infraredRgba.Length; i += 4)
        {
            double luminance = 0.299 * infraredRgba[i] + 0.587 * infraredRgba[i + 1] + 0.114 * infraredRgba[i + 2];
            double alpha = CloudAlpha(luminance) * (infraredRgba[i + 3] / 255.0);
            output[i] = r;
            output[i + 1] = g;
            output[i + 2] = b;
            output[i + 3] = (byte)Math.Round(alpha * 255);
        }
    }

    /// <summary>Convertit l'image d'une couche vers son calque transparent.</summary>
    public static void ToOverlay(CloudLayerKind kind, ReadOnlySpan<byte> rgba, Span<byte> output)
    {
        if (kind == CloudLayerKind.Cover) ToCoverOverlay(rgba, output);
        else ToTopsOverlay(rgba, output);
    }
}

/// <summary>Un cran de la légende du radar : réflectivité, couleur de la palette RainViewer, intensité de pluie correspondante.</summary>
public sealed record RadarLegendEntry(int Dbz, string Hex, double MmPerHour, string Intensity);

/// <summary>
/// Légende de la palette « Universal Blue » (identifiant 2) de RainViewer. Les couleurs sont celles de la table officielle
/// (rainviewer.com/api/color-schemes.html) pour les réflectivités en dBZ ; l'intensité en millimètres par heure est
/// indicative (relation de Marshall-Palmer Z = 200 R^1,6) : un radar ne mesure pas directement la pluie au sol.
/// </summary>
public static class RadarLegend
{
    private static readonly Localized<IReadOnlyList<RadarLegendEntry>> RainByLanguage = new(() => [
        Entry(15, "#88DDEE", Loc.Get("WeatherOverlays_tres_faible")),
        Entry(20, "#00A3E0", Loc.Get("WeatherDecoder_faible")),
        Entry(25, "#0077AA", Loc.Get("WeatherDecoder_faible")),
        Entry(30, "#005588", Loc.Get("WeatherOverlays_moderee")),
        Entry(35, "#FFEE00", Loc.Get("WeatherOverlays_moderee")),
        Entry(40, "#FFAA00", Loc.Get("WeatherDecoder_forte")),
        Entry(45, "#FF4400", Loc.Get("WeatherOverlays_tres_forte")),
        Entry(50, "#C10000", Loc.Get("Radar_Violent")),
        Entry(55, "#FFAAFF", Loc.Get("WeatherOverlays_extreme")),
    ]);
    public static IReadOnlyList<RadarLegendEntry> Rain => RainByLanguage.Value;

    /// <summary>Neige : un dégradé du clair au bleu soutenu, plus la réflectivité est forte plus la teinte est foncée.</summary>
    public static (string Light, string Dark) Snow { get; } = ("#C3FFFF", "#3F7FFF");

    /// <summary>Intensité de pluie en mm/h pour une réflectivité en dBZ : R = (10^(dBZ/10) / 200)^(1/1,6).</summary>
    public static double MmPerHour(double dbz) => Math.Pow(Math.Pow(10, dbz / 10) / 200, 1 / 1.6);

    private static RadarLegendEntry Entry(int dbz, string hex, string intensity) => new(dbz, hex, MmPerHour(dbz), intensity);
}
