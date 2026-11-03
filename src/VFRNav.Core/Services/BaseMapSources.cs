namespace VFRNav.Core.Services;

/// <summary>Adresses des tuiles des fonds de carte (les mêmes que ceux de la carte de MSFS Rhumb), pour les partager avec StormATC.</summary>
public static class BaseMapSources
{
    private const string EsriGray = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas";

    /// <summary>Fond réellement affiché : un fond CARTO sans clé retombe sur le fond clair.</summary>
    public static string Effective(string key, string cartoKey) => key switch
    {
        "carto-light" or "carto-voyager" when cartoKey.Length > 0 => key,
        "relief" or "rues" => key,
        _ => "clair"
    };

    /// <summary>Nombre de couches superposées du fond (le « clair » a une couche de noms par-dessus).</summary>
    public static int LayerCount(string key, string cartoKey) => Effective(key, cartoKey) == "clair" ? 2 : 1;

    public static int MaxZoom(string key, string cartoKey) => Effective(key, cartoKey) switch
    {
        "carto-light" or "carto-voyager" or "rues" => 19,
        "relief" => 17,
        _ => 16
    };

    /// <summary>Adresse de la tuile, ou null si la couche ou la tuile n'existe pas.</summary>
    public static string? TileUrl(string key, string cartoKey, int layer, int z, int x, int y)
    {
        if (layer < 0 || layer >= LayerCount(key, cartoKey) || z < 0 || z > MaxZoom(key, cartoKey)) return null;
        int n = 1 << z;
        if (x < 0 || x >= n || y < 0 || y >= n) return null;
        string effective = Effective(key, cartoKey);
        return effective switch
        {
            "carto-light" => $"https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png?key={Uri.EscapeDataString(cartoKey)}",
            "carto-voyager" => $"https://basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png?key={Uri.EscapeDataString(cartoKey)}",
            "relief" => $"https://{"abc"[(x + y) % 3]}.tile.opentopomap.org/{z}/{x}/{y}.png",
            "rues" => $"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            _ => $"{EsriGray}/{(layer == 0 ? "World_Light_Gray_Base" : "World_Light_Gray_Reference")}/MapServer/tile/{z}/{y}/{x}"
        };
    }
}
