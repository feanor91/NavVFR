using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;

namespace VFRNav.App;

/// <summary>Fonds de carte raster disponibles, tous utilisables sans clé.</summary>
public static class BaseMaps
{
    /// <summary>Même ordre que la liste déroulante.</summary>
    public static readonly string[] Keys = ["clair", "carto-light", "carto-voyager", "relief", "rues"];

    public static bool NeedsCartoKey(string key) => key.StartsWith("carto", StringComparison.Ordinal);

    private const string EsriGray = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas";

    /// <summary>Couches du fond, de la plus basse à la plus haute (le « clair » a une couche de noms par-dessus).</summary>
    public static ILayer[] Create(string key, string cartoKey = "") => key switch
    {
        "carto-light" when cartoKey.Length > 0 => [Carto("light_all", "CARTO Positron", cartoKey)],
        "carto-voyager" when cartoKey.Length > 0 => [Carto("rastertiles/voyager", "CARTO Voyager", cartoKey)],
        "relief" =>
        [
            Tiles(new HttpTileSource(new GlobalSphericalMercator(0, 17),
                "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", ["a", "b", "c"], name: "OpenTopoMap",
                attribution: new BruTile.Attribution("© OpenTopoMap (CC-BY-SA), © OpenStreetMap, OurAirports, OpenAIP")))
        ],
        "rues" =>
        [
            Tiles(new HttpTileSource(new GlobalSphericalMercator(0, 19),
                "https://tile.openstreetmap.org/{z}/{x}/{y}.png", name: "OpenStreetMap",
                attribution: new BruTile.Attribution("© OpenStreetMap contributors, OurAirports, OpenAIP")))
        ],
        _ =>
        [
            Tiles(new HttpTileSource(new GlobalSphericalMercator(0, 16),
                $"{EsriGray}/World_Light_Gray_Base/MapServer/tile/{{z}}/{{y}}/{{x}}", name: "Esri Light Gray",
                attribution: new BruTile.Attribution("© Esri, HERE, Garmin, © OpenStreetMap contributors, OurAirports, OpenAIP"))),
            Tiles(new HttpTileSource(new GlobalSphericalMercator(0, 16),
                $"{EsriGray}/World_Light_Gray_Reference/MapServer/tile/{{z}}/{{y}}/{{x}}", name: "Esri Light Gray Reference"))
        ]
    };

    private static TileLayer Carto(string style, string name, string cartoKey) =>
        Tiles(new HttpTileSource(new GlobalSphericalMercator(0, 19),
            $"https://basemaps.cartocdn.com/{style}/{{z}}/{{x}}/{{y}}.png?key={Uri.EscapeDataString(cartoKey)}", name: name,
            attribution: new BruTile.Attribution("© OpenStreetMap contributors © CARTO, OurAirports, OpenAIP")));

    private static TileLayer Tiles(HttpTileSource source) => new(source) { Name = "Fond" };
}
