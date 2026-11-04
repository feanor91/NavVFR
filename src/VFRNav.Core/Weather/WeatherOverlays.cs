using System.Globalization;
using System.Text.Json;

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
    public const string AttributionText = "Weather data by RainViewer";
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

            if (!host.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidDataException("Adresse des tuiles radar inattendue.");
            if (frames.Count == 0 || frames.Any(f => f.Path.Length == 0)) throw new InvalidDataException("Aucune image radar disponible.");
            frames.Sort((a, b) => a.Time.CompareTo(b.Time));
            return new RadarMaps(host.TrimEnd('/'), frames);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Réponse de RainViewer illisible : " + ex.Message, ex);
        }
    }

    /// <summary>Modèle d'adresse de tuile ({z}/{x}/{y}) pour une image : tuiles de 256 px, palette « universelle », contours lissés, neige distincte.</summary>
    public string TileUrlTemplate(RadarFrame frame) => $"{Host}{frame.Path}/256/{{z}}/{{x}}/{{y}}/2/1_1.png";

    /// <summary>Âge de l'image la plus récente.</summary>
    public TimeSpan Age(DateTimeOffset now) => now - Latest.Time;
}

/// <summary>
/// Nuages vus du satellite : image infrarouge 10,5 µm de Meteosat Troisième Génération, via le service WMS gratuit d'EUMETSAT
/// (EUMETView, sans clé). Sans paramètre de date, le service renvoie l'image la plus récente (une toutes les 10 minutes,
/// avec 10 à 20 minutes de décalage). L'infrarouge montre surtout les nuages moyens et élevés : les stratus bas et le brouillard,
/// presque aussi chauds que le sol, y sont peu visibles, surtout la nuit.
/// </summary>
public static class SatelliteClouds
{
    public const string WmsUrl = "https://view.eumetsat.int/geoserver/ows";
    public const string Layer = "mtg_fd:ir105_hrfi";
    public const string AttributionText = "Nuages : © EUMETSAT (Meteosat MTG, infrarouge 10,5 µm)";
    public const string AttributionUrl = "https://view.eumetsat.int";
    public const int MaxZoom = 8;

    /// <summary>Requête GetMap pour une tuile, en projection Web Mercator (EPSG:3857, mètres), fond transparent.</summary>
    public static string GetMapUrl(double minX, double minY, double maxX, double maxY, int size = 256)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv,
            $"{WmsUrl}?service=WMS&version=1.3.0&request=GetMap&layers={Uri.EscapeDataString(Layer)}&styles=&format=image/png&transparent=true" +
            $"&crs=EPSG:3857&bbox={minX:0.###},{minY:0.###},{maxX:0.###},{maxY:0.###}&width={size}&height={size}");
    }

    // Teinte des nuages : un gris-bleu foncé, lisible sur le fond « clair » comme sur le relief.
    private const byte R = 88, G = 108, B = 146;
    private const double DarkLimit = 105, BrightLimit = 215, MaxAlpha = 0.8;

    /// <summary>Opacité d'un point de l'image infrarouge (0 à 1) d'après sa luminosité : le sol chaud (sombre) disparaît, les nuages froids (clairs) restent.</summary>
    public static double CloudAlpha(double luminance)
    {
        double t = Math.Clamp((luminance - DarkLimit) / (BrightLimit - DarkLimit), 0, 1);
        return t * t * (3 - 2 * t) * MaxAlpha;   // lissage : pas de bord dur entre nuage et ciel clair
    }

    /// <summary>
    /// Transforme une image infrarouge en niveaux de gris, opaque, en calque transparent de nuages.
    /// Entrée et sortie : pixels RGBA de 4 octets, alpha non prémultiplié. Les points déjà transparents (hors du disque vu par le satellite) le restent.
    /// </summary>
    public static void ToCloudOverlay(ReadOnlySpan<byte> rgba, Span<byte> output)
    {
        if (output.Length < rgba.Length) throw new ArgumentException("Tampon de sortie trop petit.", nameof(output));
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            double luminance = 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
            double alpha = CloudAlpha(luminance) * (rgba[i + 3] / 255.0);
            output[i] = R;
            output[i + 1] = G;
            output[i + 2] = B;
            output[i + 3] = (byte)Math.Round(alpha * 255);
        }
    }
}
